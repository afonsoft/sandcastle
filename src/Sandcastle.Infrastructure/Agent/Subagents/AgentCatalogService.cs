using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Sandbox;
using Sandcastle.Infrastructure.Persistence;

using RuntimeSandbox = Sandcastle.Domain.Sandbox.Sandbox;

namespace Sandcastle.Infrastructure.Agent.Subagents;

/// <summary>
/// Layered declarative-agent catalog (SPEC-95 RF-001/002/006): built-in
/// presets &lt; host user dirs (<c>~/.sandcastle/agents</c> et al.)
/// &lt; workspace dirs (<c>.sandcastle/agents</c>, <c>.claude/agents</c>,
/// <c>.agents/agents</c>, <c>.opencode/agents</c> under the repo root).
/// Same <c>name</c>: the higher layer wins. Invalid files are skipped
/// with a warning — a hostile repo can never break the run.
/// </summary>
public sealed class AgentCatalogService(
    ILogger<AgentCatalogService> logger,
    IServiceScopeFactory? scopes = null)
    : IAgentCatalog
{
    private const string RepoRoot = "/workspace/repo";
    private const int MaxFileBytes = 64 * 1024;
    private const int MaxAgents = 64;
    private const string BuiltinSource = "builtin";

    /// <summary>Host user-level agent dirs, in precedence order.</summary>
    public static readonly string[] UserAgentDirs =
    [
        ".sandcastle/agents", ".claude/agents", ".agents/agents",
    ];

    /// <summary>Workspace agent dirs (repo-root relative), in
    /// precedence order — mesma família dos skill dirs.</summary>
    public static readonly string[] WorkspaceAgentDirs =
    [
        ".sandcastle/agents", ".claude/agents",
        ".agents/agents", ".opencode/agents",
    ];

    /// <summary>Test seam: resolves the process HOME.</summary>
    internal static Func<string?> HomeResolver { get; set; } =
        () => Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);

    private readonly ConcurrentDictionary<Guid, IReadOnlyList<AgentDefinition>>
        _workspaceCache = new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentDefinition>> ListAsync(
        RuntimeSandbox? sandbox, ISandboxExecutor? executor,
        CancellationToken ct)
    {
        var map = new Dictionary<string, AgentDefinition>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var builtin in Builtins())
        {
            map[builtin.Name] = builtin;
        }
        foreach (var embedded in ScanEmbeddedLayer())
        {
            map[embedded.Name] = embedded;
        }
        // SPEC-181: system/embedded < org < user < workspace. Database
        // definitions join the same precedence pipeline when the sandbox is
        // bound to a conversation; file/embedded behavior stays unchanged.
        var databaseDefinitions = sandbox?.ConversationId is not null
            ? await ScanDatabaseLayerAsync(sandbox.ConversationId.Value, ct)
            : [];
        foreach (var dbDef in databaseDefinitions.Where(d => d.Scope == "org"))
        {
            map[dbDef.Definition.Name] = dbDef.Definition;
        }
        foreach (var user in ScanUserLayer())
        {
            map[user.Name] = user;
        }
        foreach (var dbDef in databaseDefinitions.Where(d => d.Scope == "user"))
        {
            map[dbDef.Definition.Name] = dbDef.Definition;
        }
        if (sandbox is not null && executor is not null)
        {
            foreach (var ws in await ScanWorkspaceLayerAsync(
                sandbox, executor, ct))
            {
                map[ws.Name] = ws;
            }
        }
        if (map.Count > MaxAgents)
        {
            logger.LogWarning(
                "agent catalog exceeds {Max} entries — truncating",
                MaxAgents);
        }
        return map.Values
            .OrderBy(a => a.Source, StringComparer.Ordinal)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAgents)
            .ToList();
    }

    /// <summary>Invalida o cache de workspace do sandbox.</summary>
    public void InvalidateWorkspace(Guid sandboxId) =>
        _workspaceCache.TryRemove(sandboxId, out _);

    /// <summary>
    /// SPEC-123: agentes builtin via <c>agents/*.agent.md</c> embutidos —
    /// parseados pelo mesmo pipeline YAML-lite dos arquivos do usuário.
    /// Embedded vence sobre o preset hardcoded de mesmo nome (carrega
    /// <see cref="AgentDefinition.Body"/> com instruções completas).
    /// </summary>
    private IEnumerable<AgentDefinition> ScanEmbeddedLayer()
    {
        var asm = typeof(AgentCatalogService).Assembly;
        const string prefix = "Sandcastle.Infrastructure.agents.";
        foreach (var res in asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)
                && n.EndsWith(".agent.md", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal))
        {
            AgentDefinition? def;
            try
            {
                using var stream = asm.GetManifestResourceStream(res);
                if (stream is null)
                {
                    continue;
                }
                using var reader = new StreamReader(stream);
                def = Parse(reader.ReadToEnd(),
                    BuiltinSource, $"embedded:{res[prefix.Length..]}");
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "agent file embutido {Path} ilegível — skip", res);
                continue;
            }

            if (def is not null)
            {
                yield return def;
            }
        }
    }


    private async Task<IReadOnlyList<(string Scope, AgentDefinition Definition)>>
        ScanDatabaseLayerAsync(Guid conversationId, CancellationToken ct)
    {
        if (scopes is null) return [];
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetService<AppDbContext>();
            if (db is null) return [];
            var conv = await db.Conversations.AsNoTracking()
                .Where(c => c.Id == conversationId)
                .Select(c => new { c.CreatedByUserId, c.OrgId })
                .FirstOrDefaultAsync(ct);
            if (conv is null) return [];
            var rows = await db.AgentDefinitions.AsNoTracking()
                .Where(a => a.Enabled
                    && ((a.Scope == "user" && a.OwnerId == conv.CreatedByUserId)
                        || (a.Scope == "org" && a.OrgId == conv.OrgId)))
                .ToListAsync(ct);
            return rows.Select(row => (row.Scope, new AgentDefinition(
                row.Name, row.Description, ParseJsonList(row.ToolsJson),
                row.Model, SecretRedactor.Redact(row.Instructions),
                row.Scope, $"db:{row.Id}", row.ModelProfile,
                ParseJsonList(row.SkillsJson), row.Mode,
                row.PermissionPreset))).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex,
                "database agent definitions unavailable — skipping layer");
            return [];
        }
    }

    private static List<string>? ParseJsonList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var items = JsonSerializer.Deserialize<List<string>>(json);
            return items is { Count: > 0 } ? items : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Built-in presets como definições do catálogo.</summary>
    internal static IReadOnlyList<AgentDefinition> Builtins() =>
    [
        new("explore",
            "Explore the workspace: search, file_read, bash",
            ["search", "file_read", "bash", "think", "finish"],
            null, "", BuiltinSource, ""),
        new("scout",
            "Web research: web_search, web_fetch, search, file_read",
            ["web_search", "web_fetch", "search", "file_read", "think", "finish"],
            null, "", BuiltinSource, ""),
        new("plan",
            "Read-only planning: search, file_read, task_tracker",
            ["search", "file_read", "think", "task_tracker", "finish"],
            null, "", BuiltinSource, ""),
        new("general",
            "Full toolset minus task tools (no nested delegation)",
            null, null, "", BuiltinSource, ""),
    ];

    private IEnumerable<AgentDefinition> ScanUserLayer()
    {
        var home = HomeResolver();
        if (string.IsNullOrEmpty(home))
        {
            yield break;
        }
        foreach (var dir in UserAgentDirs)
        {
            var path = Path.Combine(home, dir);
            if (!Directory.Exists(path))
            {
                continue;
            }
            foreach (var file in Directory
                .EnumerateFiles(path, "*.agent.md",
                    SearchOption.TopDirectoryOnly))
            {
                var def = TryParseFile(file, "user",
                    static f => File.ReadAllText(f));
                if (def is not null)
                {
                    yield return def;
                }
            }
        }
    }

    private async Task<IReadOnlyList<AgentDefinition>>
        ScanWorkspaceLayerAsync(
            RuntimeSandbox sandbox, ISandboxExecutor executor,
            CancellationToken ct)
    {
        if (_workspaceCache.TryGetValue(sandbox.Id, out var cached))
        {
            return cached;
        }
        var defs = new List<AgentDefinition>();
        foreach (var dir in WorkspaceAgentDirs)
        {
            IReadOnlyList<FileEntry> entries;
            try
            {
                entries = await executor.ListFilesAsync(
                    sandbox, $"{RepoRoot}/{dir}", ct);
            }
            catch (Exception ex) when (ex
                is not OperationCanceledException)
            {
                logger.LogDebug(ex,
                    "agent dir {Dir} inacessível — skip", dir);
                continue;
            }

            // entry.Path pode ser nome relativo ao dir ou path
            // completo — normaliza para o nome do arquivo.
            foreach (var fileName in entries
                .Where(e => !e.IsDirectory && e.Path.EndsWith(
                    ".agent.md", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Path.Contains('/')
                    ? e.Path[e.Path.LastIndexOf('/')..]
                        .TrimStart('/')
                    : e.Path))
            {
                var path = $"{RepoRoot}/{dir}/{fileName}";
                var def = await TryParseWorkspaceFileAsync(
                    sandbox, executor, path, ct);
                if (def is not null)
                {
                    defs.Add(def);
                }
            }
        }
        return _workspaceCache[sandbox.Id] = defs;
    }

    private async Task<AgentDefinition?> TryParseWorkspaceFileAsync(
        RuntimeSandbox sandbox, ISandboxExecutor executor,
        string path, CancellationToken ct)
    {
        try
        {
            var md = await executor.ReadFileAsync(sandbox, path, ct);
            return Parse(md, "workspace", path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "agent file {Path} ilegível — skip", path);
            return null;
        }
    }

    private AgentDefinition? TryParseFile(
        string path, string source, Func<string, string> read)
    {
        try
        {
            var md = read(path);
            return Parse(md, source, path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "agent file {Path} ilegível — skip", path);
            return null;
        }
    }

    /// <summary>
    /// Frontmatter YAML-lite: <c>name</c>/<c>description</c> obrigatórios;
    /// <c>tools</c> como <c>[a, b]</c> ou CSV; <c>model</c> opcional;
    /// corpo markdown = instruções do agente. Retorna null (com warning)
    /// para arquivo inválido.
    /// </summary>
    internal AgentDefinition? Parse(
        string md, string source, string path)
    {
        if (md.Length > MaxFileBytes)
        {
            logger.LogWarning(
                "agent file {Path} excede {Max}KB — skip",
                path, MaxFileBytes / 1024);
            return null;
        }

        if (SplitFrontmatter(md, path) is not { } parts)
        {
            return null;
        }

        var (fm, body) = parts;
        string? name = null, desc = null, model = null, modelProfile = null,
            mode = null, permissionPreset = null;
        List<string>? tools = null, skills = null;
        foreach (var line in fm.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                name = val;
            }
            else if (key.Equals("description",
                StringComparison.OrdinalIgnoreCase))
            {
                desc = val;
            }
            else if (key.Equals("model",
                StringComparison.OrdinalIgnoreCase))
            {
                model = val.Equals("inherit", StringComparison.OrdinalIgnoreCase)
                    ? null : val;
            }
            else if (key.Equals("tools",
                StringComparison.OrdinalIgnoreCase))
            {
                tools = ParseList(val);
            }
            else if (key.Equals("model_profile",
                StringComparison.OrdinalIgnoreCase)
                || key.Equals("profile", StringComparison.OrdinalIgnoreCase))
            {
                modelProfile = val.Equals("inherit", StringComparison.OrdinalIgnoreCase)
                    ? null : val;
            }
            else if (key.Equals("skills",
                StringComparison.OrdinalIgnoreCase))
            {
                skills = ParseList(val);
            }
            else if (key.Equals("mode",
                StringComparison.OrdinalIgnoreCase))
            {
                mode = val;
            }
            else if (key.Equals("permission_preset",
                StringComparison.OrdinalIgnoreCase))
            {
                permissionPreset = val;
            }
        }

        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(desc))
        {
            logger.LogWarning(
                "agent file {Path} sem name/description — skip", path);
            return null;
        }
        return new AgentDefinition(
            name.Trim(), desc.Trim(), tools, model, body,
            source, path, modelProfile, skills, mode, permissionPreset);
    }

    /// <summary>Extrai (frontmatter, body) entre os fences `---`.</summary>
    private (string Fm, string Body)? SplitFrontmatter(
        string md, string path)
    {
        var span = md.AsSpan().TrimStart('﻿');
        if (!span.StartsWith("---"))
        {
            logger.LogWarning(
                "agent file {Path} sem frontmatter — skip", path);
            return null;
        }
        var nl = span.IndexOf('\n');
        if (nl < 0)
        {
            return null;
        }
        var rest = span[(nl + 1)..];
        var end = rest.ToString().IndexOf("\n---", StringComparison.Ordinal);
        if (end < 0)
        {
            logger.LogWarning(
                "agent file {Path} sem fence de fechamento — skip", path);
            return null;
        }

        return (rest[..end].ToString(),
            rest[(end + 4)..].ToString().Trim());
    }

    /// <summary><c>[a, b]</c> ou CSV → lista normalizada.</summary>
    private static List<string>? ParseList(string val)
    {
        var inner = val.StartsWith('[') && val.EndsWith(']')
            ? val[1..^1] : val;
        var items = inner.Split(',',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Select(i => i.Trim('"', '\''))
            .Where(i => i.Length > 0)
            .ToList();
        return items.Count > 0 ? items : null;
    }
}