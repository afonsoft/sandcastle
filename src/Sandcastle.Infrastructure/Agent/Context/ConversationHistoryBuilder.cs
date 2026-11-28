using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Events;
using Sandcastle.Application.Identity;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Conversations;

namespace Sandcastle.Infrastructure.Agent.Context;

/// <summary>
/// Rebuilds the LLM history from the event stream (SPEC-09 resume, SPEC-22
/// replay-consistent compaction): applies <c>Condensation.ForgottenEventIds</c>
/// and <c>PrunedEventIds</c>, re-inserts the latest summary after the system
/// prompt, and appends a <c>SystemPromptEvent</c> when the stream has none.
/// </summary>
public sealed class ConversationHistoryBuilder(IServiceProvider services)
{
    private readonly HashSet<Guid> _skillAuditRecorded = [];

    /// <summary>Result of a rebuild: messages + parallel event ids + tail.</summary>
    public sealed record RebuiltHistory(
        List<LlmMessage> Messages,
        List<string?> EventIds,
        string? LastEventId,
        IReadOnlyList<Domain.Skills.Skill> Skills);

    /// <summary>Rebuilds <paramref name="conv"/>'s history (resume-safe).</summary>
    public async Task<RebuiltHistory> BuildAsync(
        Guid conversationId, ConversationRecord conv, IToolRegistry tools,
        CancellationToken ct)
    {
        var events = services.GetRequiredService<IEventService>();
        var all = await LoadAllEventsAsync(events, conversationId, ct);

        var forgotten = all.OfType<Condensation>()
            .SelectMany(c => c.ForgottenEventIds).ToHashSet();
        var pruned = all.OfType<Condensation>()
            .SelectMany(c => c.PrunedEventIds).ToHashSet();
        var lastSummary = all.OfType<Condensation>()
            .LastOrDefault(c => c.Summary is not null);

        var history = new List<LlmMessage>();
        var ids = new List<string?>();
        var (hasSystem, lastId) = ReplayEvents(
            all, forgotten, pruned, history, ids);

        var agentProfile = await LoadAgentProfileAsync(conv, ct);
        var skillResolution = await ResolveSkillsAsync(
            conversationId, conv, all, agentProfile, ct);
        var activeSkills = skillResolution.Skills;
        if (!hasSystem)
        {
            lastId = await AppendSystemPromptAsync(events, conversationId,
                conv, tools, new(all, lastId, history, ids),
                activeSkills, skillResolution.Catalog, skillResolution.Loaded,
                agentProfile, ct);
        }

        if (lastSummary is { Summary: { } summary })
        {
            // O summary re-entra depois do system (ou no início) — posição
            // determinística: min(SummaryOffset, count).
            var at = Math.Min(
                lastSummary.SummaryOffset ?? (hasSystem ? 1 : 0),
                history.Count);
            var insertAt = Math.Max(hasSystem ? 1 : 0, at);
            history.Insert(insertAt, new LlmMessage("system",
                $"## Earlier context (compacted)\n{summary}"));
            ids.Insert(insertAt, lastSummary.Id);
        }

        return new RebuiltHistory(history, ids, lastId, activeSkills);
    }

    private static async Task<List<EventBase>> LoadAllEventsAsync(
        IEventService events, Guid conversationId, CancellationToken ct)
    {
        var all = new List<EventBase>();
        string? cursor = null;
        do
        {
            var page = await events.SearchAsync(conversationId,
                new EventQuery(PageId: cursor, Limit: 100), ct);
            all.AddRange(page.Items);
            cursor = page.NextPageId;
        }
        while (cursor is not null);

        return all;
    }

    private static (bool HasSystem, string? LastId) ReplayEvents(
        List<EventBase> all, HashSet<string> forgotten,
        HashSet<string> pruned,
        List<LlmMessage> history, List<string?> ids)
    {
        string? lastId = null;
        var hasSystem = false;
        foreach (var e in all)
        {
            if (forgotten.Contains(e.Id))
            {
                continue;
            }

            lastId = e.Id;
            switch (e)
            {
                case SystemPromptEvent sp:
                    hasSystem = true;
                    history.Add(new LlmMessage("system",
                        Flatten(sp.SystemPrompt)));
                    ids.Add(e.Id);
                    break;
                case MessageEvent m:
                    history.Add(ToLlmMessage(m));
                    ids.Add(e.Id);
                    break;
                case ActionEvent a:
                    history.Add(LlmMessage.AssistantToolCall(
                        Flatten(a.Thought),
                        [new LlmToolCall(a.ToolCallId, a.ToolName,
                            a.Action?.GetRawText() ?? "{}")]));
                    ids.Add(e.Id);
                    break;
                case ObservationEvent o:
                    history.Add(LlmMessage.ToolResult(o.ToolCallId,
                        pruned.Contains(o.Id)
                            ? "[Output truncated by condenser]"
                            : o.Observation.GetRawText()));
                    ids.Add(e.Id);
                    break;
                case UserRejectObservation r:
                    history.Add(LlmMessage.ToolResult(
                        r.ToolCallId,
                        $"User rejected: {r.RejectionReason}"));
                    ids.Add(e.Id);
                    break;
            }
        }

        return (hasSystem, lastId);
    }

    /// <summary>Fatia mutável do histórico em construção (SPEC-104).</summary>
    private sealed record HistorySlice(
        List<EventBase> All, string? LastId,
        List<LlmMessage> History, List<string?> Ids);

    private sealed record SkillResolution(
        IReadOnlyList<Domain.Skills.Skill> Skills,
        IReadOnlyList<Domain.Skills.Skill> Catalog,
        Discovery.RepoContextLoader.LoadedContext? Loaded);

    private async Task<SkillResolution> ResolveSkillsAsync(
        Guid conversationId, ConversationRecord conv,
        IReadOnlyList<EventBase> all,
        Persistence.AgentProfileRecord? agentProfile, CancellationToken ct)
    {
        // SPEC-11 RF-002: resolve skills por trigger (always/keyword/task)
        // usando a última mensagem do usuário e as tags da conversa.
        var lastUserText = all.OfType<MessageEvent>()
            .Where(m => m.Source == EventSource.User)
            .Select(m => ToLlmMessage(m).Content)
            .LastOrDefault();
        var referencedPaths = all.OfType<FilesChangedEvent>()
            .SelectMany(e => e.Paths.Select(p => p.Path))
            .Concat(Skills.SkillService.ExtractPaths(lastUserText))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();
        IReadOnlyList<Domain.Skills.Skill> skills = [];
        IReadOnlyList<Domain.Skills.Skill> catalog = [];
        if (services.GetService<Application.Skills.ISkillService>()
            is { } skillSvc)
        {
            skills = await skillSvc.ResolveForConversationAsync(
                conversationId, lastUserText, null,
                conv.CreatedByUserId, conv.OrgId?.ToString(), ct,
                referencedPaths);
            // SPEC-194 RF-004: the catalog intentionally includes skills
            // whose triggers did not match; full bodies remain on demand.
            catalog = await skillSvc.ListAsync(
                conv.CreatedByUserId, conv.OrgId?.ToString(), ct);
        }

        // SPEC-25 RF-001/002: repo skills federadas (o markdown de
        // contexto segue sendo carregado só quando o prompt é criado).
        Discovery.RepoContextLoader.LoadedContext? loaded = null;
        if (services.GetService<Discovery.RepoContextLoader>()
            is { } repoLoader)
        {
            loaded = await repoLoader.LoadAsync(conv, all, ct);
            skills = MergeRepoSkills(
                skills, loaded.RepoSkills, lastUserText, referencedPaths);
            catalog = catalog.Concat(loaded.RepoSkills)
                .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(s => s.Source).First())
                .Take(100)
                .ToList();
        }

        var profileSkills = ParseStringArray(agentProfile?.SkillsJson);
        if (profileSkills is not null)
        {
            var allowed = profileSkills.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            skills = skills.Where(s => allowed.Contains(s.Name)).ToList();
            catalog = catalog.Where(s => allowed.Contains(s.Name)).ToList();
        }

        if (skills.Count > 0
            && _skillAuditRecorded.Add(conversationId)
            && services.GetService<IAuditService>() is { } audit)
        {
            await audit.RecordAsync("agent.skills_loaded",
                conv.CreatedByUserId, conv.OrgId, "conversation",
                conversationId.ToString(),
                metadata: new { count = skills.Count, skills = skills.Select(s => s.Name).ToArray() },
                cancellationToken: ct);
        }
        return new SkillResolution(skills, catalog, loaded);
    }


    private async Task<string?> LoadPromptPackInstructionsAsync(
        string reference, ConversationRecord conv, CancellationToken ct)
    {
        if (!reference.StartsWith("pack:", StringComparison.OrdinalIgnoreCase)
            || services.GetService<Persistence.AppDbContext>() is not { } db)
        {
            return null;
        }
        var name = reference[5..].Trim();
        if (name.Length == 0) return null;
        var rows = await db.PromptPacks.AsNoTracking()
            .Where(p => p.Active && p.Name == name
                && ((p.Scope == "user" && p.OwnerId == conv.CreatedByUserId)
                    || (p.Scope == "org" && p.OrgId == conv.OrgId)))
            .ToListAsync(ct);
        var pack = rows
            .OrderByDescending(p => p.Scope == "user")
            .ThenByDescending(p => p.Version)
            .FirstOrDefault();
        if (pack is null || string.IsNullOrWhiteSpace(pack.BasePrompt)) return null;

        var sections = new List<string>();
        void Add(string title, string? body)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                sections.Add($"## {title}\n" + SecretRedactor.Redact(body.Trim()));
            }
        }
        Add("Prompt pack base instructions", pack.BasePrompt);
        Add("Harness rules", pack.HarnessRules);
        Add(conv.Mode == "plan" ? "Plan mode instructions" : "Build mode instructions",
            conv.Mode == "plan" ? pack.PlanModePrompt : pack.BuildModePrompt);
        Add("Advisor instructions", pack.AdvisorPrompt);
        Add("Permission judge instructions", pack.PermissionJudgePrompt);
        return "\n\n" + string.Join("\n\n", sections);
    }

    private async Task<string?> AppendSystemPromptAsync(
        IEventService events, Guid conversationId,
        ConversationRecord conv, IToolRegistry tools,
        HistorySlice h, IReadOnlyList<Domain.Skills.Skill> skills,
        IReadOnlyList<Domain.Skills.Skill> skillCatalog,
        Discovery.RepoContextLoader.LoadedContext? loaded,
        Persistence.AgentProfileRecord? agentProfile,
        CancellationToken ct)
    {
        var repoContext = loaded?.RenderedMarkdown;

        var userContextFiles =
            await LoadUserContextFilesAsync(conv, ct);

        // SPEC-90 RF-008: prompt composto por contributors via DI
        // (ordem/falha toleradas pelo composer). Sem composer
        // registrado (testes com provider manual), cai no conjunto
        // default de contributors.
        var composition = await ResolveComposer()
            .ComposeDetailedAsync(new Application.Agent.AgentPromptContext
            {
                SelectedRepository = conv.SelectedRepository,
                // SPEC-146: repos efetivamente montados (clone ok) —
                // filtra pelo state event do worker quando presente.
                SelectedRepositories = MountedRepositories(conv, h.All),
                Tools = tools.All.Select(t => t.Schema).ToList(),
                Skills = skills,
                SkillCatalog = skillCatalog,
                UserContextFiles = userContextFiles,
                RepoContext = repoContext,
                Ct = ct,
                SubagentCatalog =
                    await ResolveSubagentCatalogAsync(
                        conv.SandboxId, ct),
                SddProbeFactory = probeCt => ProbeSddSurfaceAsync(
                    services, conv.SandboxId, probeCt),
                BlueprintKnowledge =
                    await LoadBlueprintKnowledgeAsync(
                        conv.SandboxId, ct),
            });
        var prompt = composition.Text;
        if (!string.IsNullOrWhiteSpace(agentProfile?.PromptPack))
        {
            var packInstructions = await LoadPromptPackInstructionsAsync(
                agentProfile.PromptPack, conv, ct);
            prompt += packInstructions is not null
                ? packInstructions
                : "\n\n## Agent profile instructions\n"
                    + agentProfile.PromptPack.Trim();
        }
        var sp = new SystemPromptEvent
        {
            ParentId = h.LastId,
            SystemPrompt = JsonSerializer.SerializeToElement(
                new[] { new { type = "text", text = prompt } },
                EventJson.Options),
            Tools = JsonSerializer.SerializeToElement(
                tools.All.Select(t => new
                {
                    name = t.Schema.Name,
                    description = t.Schema.Description,
                    input_schema = t.Schema.InputSchema,
                }), EventJson.Options),
            DynamicContext = JsonSerializer.SerializeToElement(new
            {
                prompt_cache = new
                {
                    static_chars = composition.StaticChars,
                    dynamic_chars = composition.DynamicChars,
                    sections = composition.Sections.Select(s => new
                    {
                        contributor = s.Contributor,
                        order = s.Order,
                        tier = s.Tier.ToString().ToLowerInvariant(),
                        chars = s.Chars,
                    }).ToArray(),
                },
            }, EventJson.Options),
        };
        var newId = (await events.AppendAsync(conversationId, sp, ct)).Id;
        h.History.Insert(0, new LlmMessage("system", prompt));
        h.Ids.Insert(0, sp.Id);
        return newId;
    }


    private async Task<Persistence.AgentProfileRecord?> LoadAgentProfileAsync(
        ConversationRecord conv, CancellationToken ct)
    {
        if (conv.AgentProfileId is not { } id
            || services.GetService<Persistence.AppDbContext>() is not { } db)
            return null;
        return await db.AgentProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    private static IReadOnlyList<string>? ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// SPEC-146 RF-003: repos efetivamente montados — parseia o último
    /// <c>ConversationStateUpdateEvent(key="repositories")</c> do worker
    /// (status="cloned"); sem o evento → seleção inteira (clone pendente
    /// ou conversa legada).
    /// </summary>
    internal static IReadOnlyList<string>? MountedRepositories(
        ConversationRecord conv, IReadOnlyList<EventBase> events)
    {
        var repos = conv.GetRepositories();
        if (repos.Count == 0)
        {
            return null;
        }

        var state = events.OfType<ConversationStateUpdateEvent>()
            .Where(e => e.Key == "repositories")
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefault();
        if (state is null)
        {
            return repos;
        }

        try
        {
            if (state.Value is not { } v
                || v.ValueKind != JsonValueKind.Object
                || !v.TryGetProperty("repositories", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return repos;
            }
            var cloned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in arr.EnumerateArray())
            {
                if (r.TryGetProperty("status", out var s)
                    && s.GetString() == "cloned"
                    && r.TryGetProperty("name", out var n)
                    && n.GetString() is { } name)
                {
                    cloned.Add(name);
                }
            }
            var mounted = repos.Where(cloned.Contains).ToList();
            return mounted.Count > 0 ? mounted : repos;
        }
        catch (Exception)
        {
            return repos; // payload inesperado — não derruba o prompt
        }
    }

    // SPEC-25 RF-002: db ∪ repo, dedupe por nome (db ganha), cap 20.
    // Repo skills entram via trigger Keyword(nome) — lastUserText
    // contém o nome → injetada; o catálogo completo sempre aparece
    // na seção Repository context.
    private static IReadOnlyList<Domain.Skills.Skill> MergeRepoSkills(
        IReadOnlyList<Domain.Skills.Skill> db,
        IReadOnlyList<Domain.Skills.Skill> repo, string? message,
        IReadOnlyList<string>? paths = null)
    {
        if (repo.Count == 0)
        {
            return db;
        }

        var names = db.Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = db.ToList();
        foreach (var s in repo)
        {
            if (names.Contains(s.Name))
            {
                continue;
            }

            var explicitMention = message?.Contains(s.Name,
                StringComparison.OrdinalIgnoreCase) == true;
            var pathTriggered = s.Triggers.Any(t =>
                t.Type == Domain.Skills.SkillTriggerType.Path
                && t.Value is not null
                && Skills.SkillService.PathMatches(t.Value, message, paths));
            if (explicitMention || pathTriggered)
            {
                merged.Add(s);
                names.Add(s.Name);
            }

            if (merged.Count >= 20)
            {
                break;
            }
        }

        return merged;
    }

    /// <summary>MessageEvent → LlmMessage (também usado pelo runner p/ pendings).</summary>
    internal static LlmMessage ToLlmMessage(MessageEvent m)
    {
        try
        {
            var el = m.LlmMessage;
            var role = el.TryGetProperty("role", out var r)
                ? r.GetString() ?? "user" : "user";
            var content = ContentText(el);
            return new LlmMessage(role, content);
        }
        catch (JsonException)
        {
            return new LlmMessage("user", m.LlmMessage.GetRawText());
        }
    }

    /// <summary>Extrai o texto de content (string | array de parts | raw).</summary>
    private static string? ContentText(JsonElement el)
    {
        if (!el.TryGetProperty("content", out var c))
        {
            return null;
        }

        if (c.ValueKind == JsonValueKind.String)
        {
            return c.GetString();
        }

        if (c.ValueKind != JsonValueKind.Array)
        {
            return c.GetRawText();
        }

        return string.Concat(c.EnumerateArray().Select(b =>
            b.TryGetProperty("text", out var t)
                ? t.GetString() : null));
    }

    private static string? Flatten(JsonElement? el) => el switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Array } arr => string.Concat(
            arr.EnumerateArray().Select(b =>
                b.TryGetProperty("text", out var t) ? t.GetString() : null)),
        { } other => other.GetRawText(),
    };

    /// <summary>
    /// SPEC-95 RF-007: catálogo efetivo de subagents via
    /// <see cref="IAgentCatalog"/> (mesma fonte do tool
    /// <c>agent_catalog</c>); sem serviço ou em falha, cai nos presets.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveSubagentCatalogAsync(
        Guid? sandboxId, CancellationToken ct)
    {
        if (services.GetService<IAgentCatalog>() is not { } catalog)
        {
            return Subagents.SubagentPresets.Names;
        }
        try
        {
            var sandbox = sandboxId is { } id
                && services.GetService<Application.Sandbox.ISandboxService>()
                    is { } svc
                ? await svc.GetAsync(id, ct)
                : null;
            var executor = services
                .GetService<Application.Sandbox.ISandboxExecutor>();
            var names = await catalog.ListNamesAsync(
                sandbox, executor, ct);
            return names.Count > 0 ? names
                : Subagents.SubagentPresets.Names;
        }
        catch
        {
            return Subagents.SubagentPresets.Names;
        }
    }

    /// <summary>
    /// SPEC-172 RF-004: notas <c>knowledge:</c> do blueprint persistidas
    /// no sandbox record (<c>blueprint_knowledge_json</c>) — fail-open.
    /// </summary>
    private async Task<IReadOnlyList<(string Name, string Contents)>?>
        LoadBlueprintKnowledgeAsync(Guid? sandboxId, CancellationToken ct)
    {
        if (sandboxId is not { } id
            || services.GetService<Persistence.AppDbContext>()
                is not { } db)
        {
            return null;
        }
        var json = await db.Sandboxes
            .Where(s => s.Id == id)
            .Select(s => s.BlueprintKnowledgeJson)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrEmpty(json)
            ? null
            : Infrastructure.Sandbox.BlueprintService
                .DeserializeKnowledge(json)
                .Select(k => (k.Name, k.Contents)).ToList();
    }

    /// <summary>
    /// SPEC-90: resolve o composer da DI; providers montados à mão
    /// (testes sem AddInfrastructure) recebem o conjunto default.
    /// </summary>
    private SystemPrompt.SystemPromptComposer ResolveComposer()
    {
        if (services.GetService<SystemPrompt.SystemPromptComposer>()
            is { } composer)
        {
            return composer;
        }
        var cfg = services.GetService<IConfiguration>()
            ?? new ConfigurationBuilder().Build();
        var logger = services.GetService<ILoggerFactory>()
            ?.CreateLogger<SystemPrompt.SystemPromptComposer>()
            ?? NullLogger<SystemPrompt.SystemPromptComposer>.Instance;
        var registered = services
            .GetServices<IAgentPromptContributor>().ToList();
        IReadOnlyList<IAgentPromptContributor> contributors =
            registered.Count > 0
                ? registered
                :
                [
                    new SystemPrompt.BaseAgentContributor(),
                    new SystemPrompt.AutonomyTiersContributor(),
                    new SystemPrompt.SddWorkflowContributor(cfg),
                    new SystemPrompt.RepoContextContributor(),
                    new SystemPrompt.SkillCatalogContributor(),
                    new SystemPrompt.SkillsContributor(),
                    new SystemPrompt.MemoryDigestContributor(),
                    new SystemPrompt.DelegationPolicyContributor(),
                ];
        return new SystemPrompt.SystemPromptComposer(contributors, logger);
    }

    /// <summary>
    /// SPEC-90 RF-006: SDD-surface probe — exec inside the conversation
    /// sandbox checking for <c>.specs/</c> or <c>docs/specs/</c>. Any
    /// failure (no sandbox, executor down, timeout) degrades to
    /// <c>false</c> — prompt building never breaks on a probe.
    /// </summary>
    /// <summary>
    /// SPEC-157 RF-004: context files do workspace — anexos do dono da
    /// conversa (e itens da org efetiva) viram seção
    /// <c>user_context_files</c>, 8KB por arquivo com flag truncated.
    /// </summary>
    private async Task
        <IReadOnlyList<(string Filename, string Text, bool Truncated)>?>
        LoadUserContextFilesAsync(
            ConversationRecord conv, CancellationToken ct)
    {
        if (conv.CreatedByUserId is not { } owner
            || services.GetService<Persistence.AppDbContext>()
                is not { } db)
            return null;
        const int MaxContextFileBytes = 8 * 1024;
        var rows = await db.WorkspaceItems
            .Where(i => i.Kind == "context_file"
                && (i.OwnerId == owner
                    || (conv.OrgId != null && i.OrgId == conv.OrgId)))
            .ToListAsync(ct);
        var files =
            new List<(string, string, bool)>(rows.Count);
        foreach (var it in rows)
        {
            using var doc = JsonDocument.Parse(it.BodyJson);
            var text = doc.RootElement
                .GetProperty("text").GetString() ?? "";
            var filename = doc.RootElement
                .TryGetProperty("filename", out var fn)
                ? fn.GetString() ?? it.Name : it.Name;
            var truncated = false;
            if (System.Text.Encoding.UTF8.GetByteCount(text)
                > MaxContextFileBytes)
            {
                text = text[..MaxContextFileBytes];
                truncated = true;
            }
            files.Add((filename, text, truncated));
        }
        return files.Count > 0 ? files : null;
    }

    private static async Task<bool> ProbeSddSurfaceAsync(
        IServiceProvider services, Guid? sandboxId, CancellationToken ct)
    {
        if (sandboxId is null
            || services.GetService<Application.Sandbox.ISandboxService>()
                is not { } sandboxSvc
            || services.GetService<Application.Sandbox.ISandboxExecutor>()
                is not { } executor)
        {
            return false;
        }
        try
        {
            var sandbox = await sandboxSvc.GetAsync(sandboxId.Value, ct);
            if (sandbox is null)
            {
                return false;
            }
            var result = await executor.ExecAsync(sandbox,
                SystemPrompt.SddSurfaceProbe.Command,
                TimeSpan.FromSeconds(10), ct);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}