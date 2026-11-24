using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Events;
using Sandcastle.Application.Hooks;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Hooks;
using Sandcastle.Infrastructure.Agent;

namespace Sandcastle.Infrastructure.Hooks;

/// <summary>
/// SPEC-127: <c>.openhands/hooks/*.hook.json</c> file hooks. Discovery is
/// layered — repo (<c>.openhands/hooks/</c> inside the workspace) first,
/// then user (<c>~/.openhands/hooks/</c> in the sandbox home). Command
/// hooks run through the sandbox executor; prompt hooks inject a
/// hook-sourced message into history. Every firing appends a durable
/// <see cref="HookFiredEvent"/>; a failing <c>blocking</c> command hook
/// vetoes the step.
/// </summary>
public sealed class WorkspaceHookService(
    IEventService events,
    ILogger<WorkspaceHookService> logger,
    IConfiguration? config = null) : IWorkspaceHookService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private const string HookTypeCommand = "command";

    /// <summary>SPEC-173 RF-005: kill switch global (<c>Hooks:Enabled</c>).</summary>
    private bool Enabled => config?.GetValue("Hooks:Enabled", true) ?? true;

    /// <summary>
    /// SPEC-173 RF-005: compat read-only com <c>.devin/</c> e
    /// <c>.claude/</c> (<c>Hooks:CompatDirs</c>, default on).
    /// </summary>
    private bool CompatDirs =>
        config?.GetValue("Hooks:CompatDirs", true) ?? true;

    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    private sealed record CacheEntry(
        IReadOnlyList<WorkspaceHookDefinition> Hooks, DateTimeOffset LoadedAt);

    /// <inheritdoc />
    public async Task<bool> RunAsync(
        HookEvent point, HookContext context, CancellationToken ct)
    {
        if (!Enabled)
        {
            return false;
        }

        var hooks = await LoadAsync(context, ct);
        if (hooks.Count == 0)
        {
            return false;
        }

        var eventName = EventName(point);
        var blocked = false;
        foreach (var hook in hooks)
        {
            if (!string.Equals(hook.Event, eventName,
                    StringComparison.OrdinalIgnoreCase)
                || !GlobMatches(hook.Matcher, context.ToolName))
            {
                continue;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var success = true;
            int? exitCode = null;
            string? error = null;
            string? stdout = null;
            var decisionVeto = false;
            try
            {
                if (hook.Type == "prompt")
                {
                    await InjectPromptAsync(context, hook);
                }
                else
                {
                    (success, exitCode, error, stdout) =
                        await RunCommandAsync(context, hook, ct);

                    // SPEC-173 RF-002: stdout {"decision":"deny|block"} =
                    // veto explícito em qualquer ponto do ciclo.
                    if (stdout is not null
                        && TryDecision(stdout, out var reason))
                    {
                        success = false;
                        error = reason
                            ?? $"hook decision on {hook.FilePath ?? hook.Name}";
                        decisionVeto = true;
                    }

                    // SPEC-173 RF-002: UserPromptSubmit command hook —
                    // stdout vira contexto extra <hook_context> (cap 4 KB).
                    if (success && eventName == "on_message"
                        && !string.IsNullOrWhiteSpace(stdout))
                    {
                        await InjectHookContextAsync(context, stdout);
                    }
                }
            }
            catch (Exception ex)
            {
                success = false;
                error = SecretRedactor.Redact(ex.Message);
                logger.LogWarning(ex,
                    "Workspace hook {Hook} failed at {Point}",
                    hook.Name, point);
            }

            sw.Stop();
            await events.AppendAsync(context.ConversationId,
                new HookFiredEvent
                {
                    HookEventType = eventName,
                    HookName = hook.Name,
                    Layer = hook.Layer,
                    FilePath = hook.FilePath,
                    Matcher = hook.Matcher,
                    HookType = hook.Type,
                    ToolName = context.ToolName,
                    Success = success,
                    ExitCode = exitCode,
                    Blocking = hook.Blocking || decisionVeto,
                    Error = error,
                    DurationMs = sw.ElapsedMilliseconds,
                    ParentId = context.ActionId,
                }, CancellationToken.None);

            if (decisionVeto || (hook.Blocking && !success))
            {
                blocked = true;
            }
        }

        return blocked;
    }

    /// <summary>
    /// Loads hook definitions for the conversation workspace, cached with a
    /// short TTL so edits land on the next run, not mid-tool-loop.
    /// </summary>
    private async Task<IReadOnlyList<WorkspaceHookDefinition>> LoadAsync(
        HookContext context, CancellationToken ct)
    {
        if (_cache.TryGetValue(context.ConversationId, out var hit)
            && hit.LoadedAt > DateTimeOffset.UtcNow - CacheTtl)
        {
            return hit.Hooks;
        }

        var hooks = await DiscoverAsync(context, ct);
        _cache[context.ConversationId] =
            new CacheEntry(hooks, DateTimeOffset.UtcNow);
        return hooks;
    }

    /// <summary>
    /// One sandbox exec dumps every hook file (repo layer first, then user
    /// layer) as <c>===layer:name</c>-delimited bodies parsed locally.
    /// </summary>
    private async Task<IReadOnlyList<WorkspaceHookDefinition>> DiscoverAsync(
        HookContext context, CancellationToken ct)
    {
        var exec = context.Services?
            .GetService(typeof(ISandboxExecutor)) as ISandboxExecutor;
        if (context.Sandbox is null || exec is null)
        {
            return [];
        }

        var dump = BuildDumpCommand();

        CommandResult result;
        try
        {
            result = await exec.ExecAsync(context.Sandbox, dump,
                TimeSpan.FromSeconds(15), ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Workspace hook discovery failed");
            return [];
        }

        if (result.ExitCode != 0 || string.IsNullOrEmpty(result.StdOut))
        {
            return [];
        }

        return ParseDump(result.StdOut);
    }

    /// <summary>
    /// SPEC-173 RF-001/005: comando de descoberta multi-fonte — canônico
    /// <c>.agents/</c> primeiro, depois <c>.openhands/</c> (SPEC-127),
    /// compat <c>.devin/</c> + <c>.claude/</c> (quando
    /// <see cref="CompatDirs"/>), e camada de usuário em <c>$HOME</c>.
    /// <c>Hooks:RepoFiles</c> (lista <c>;</c>) sobrescreve os alvos de repo.
    /// </summary>
    internal string BuildDumpCommand()
    {
        var repo = new List<string>
        {
            ".agents/hooks.json", ".agents/hooks/*.json",
            ".openhands/hooks/*.hook.json",
        };
        var custom = config?["Hooks:RepoFiles"];
        if (!string.IsNullOrWhiteSpace(custom))
        {
            repo = custom.Split(';',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries).ToList();
        }
        else if (CompatDirs)
        {
            repo.AddRange(
            [
                ".devin/hooks.v1.json", ".devin/config.json",
                ".claude/settings.json", ".claude/settings.local.json",
            ]);
        }

        var user = new List<string>
        {
            "\"$HOME/.agents/hooks.json\"",
            "\"$HOME/.openhands/hooks/\"*.hook.json",
        };
        if (CompatDirs)
        {
            user.Add("\"$HOME/.claude/settings.json\"");
        }

        var files = string.Join(' ', repo.Concat(user));
        return
            "shopt -s nullglob 2>/dev/null; " +
            $"for f in {files}; do " +
            "[ -f \"$f\" ] || continue; " +
            "case \"$f\" in \"$HOME\"/*) l=user;; *) l=repo;; esac; " +
            "printf '===%s:%s\\n' \"$l\" \"$f\"; cat \"$f\"; " +
            "printf '\\n'; done";
    }

    internal List<WorkspaceHookDefinition> ParseDump(string stdout)
    {
        var hooks = new List<WorkspaceHookDefinition>();
        string? layer = null;
        string? path = null;
        var body = new System.Text.StringBuilder();

        void Flush()
        {
            if (layer is null || path is null)
            {
                return;
            }

            var text = body.ToString();
            body.Clear();
            try
            {
                using var doc = JsonDocument.Parse(text);
                hooks.AddRange(ParseFile(doc.RootElement, layer, path));
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex,
                    "Workspace hook {Name}: invalid JSON — skipped", path);
            }
        }

        foreach (var line in stdout.Split('\n'))
        {
            if (line.StartsWith("===", StringComparison.Ordinal)
                && line.IndexOf(':', 3) is > 3 and var sep)
            {
                Flush();
                layer = line[3..sep];
                path = line[(sep + 1)..].TrimEnd('\r');
            }
            else
            {
                body.AppendLine(line);
            }
        }

        Flush();
        return hooks;
    }

    /// <summary>
    /// SPEC-173 RF-001: dispatch por formato — schema plano SPEC-127
    /// (<c>event</c>/<c>on</c> presente) ou schema agrupado
    /// Devin/Claude-compat (<c>{hooks|Event:[{matcher,hooks:[…]}]}</c>).
    /// </summary>
    private List<WorkspaceHookDefinition> ParseFile(
        JsonElement el, string layer, string path)
    {
        var hooks = new List<WorkspaceHookDefinition>();
        if (el.ValueKind != JsonValueKind.Object)
        {
            return hooks;
        }

        if (el.TryGetProperty("event", out _)
            || el.TryGetProperty("on", out _))
        {
            if (ParseHook(el, layer, BaseName(path), path) is { } flat)
            {
                hooks.Add(flat);
            }
            return hooks;
        }

        hooks = ParseGrouped(el, layer, path);
        if (hooks.Count == 0)
        {
            logger.LogWarning(
                "Workspace hook {Path}: no recognizable hook entries"
                    + " — skipped", path);
        }
        return hooks;
    }

    /// <summary>
    /// SPEC-173 RF-001: schema agrupado — objeto <c>hooks</c> interno
    /// (Claude settings / Devin config) ou o próprio root
    /// (<c>.agents/hooks.json</c>, <c>.devin/hooks.v1.json</c>).
    /// </summary>
    private List<WorkspaceHookDefinition> ParseGrouped(
        JsonElement root, string layer, string path)
    {
        var hooks = new List<WorkspaceHookDefinition>();
        var map = root.TryGetProperty("hooks", out var hm)
            && hm.ValueKind == JsonValueKind.Object
                ? hm : root;
        var idx = 0;
        foreach (var ev in map.EnumerateObject())
        {
            var eventName = NormalizeEvent(ev.Name);
            if (eventName is null
                || ev.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in ev.Value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("hooks", out var inner)
                    || inner.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var matcher = Str(entry, "matcher");
                foreach (var ih in inner.EnumerateArray())
                {
                    if (ParseInnerHook(ih, eventName, matcher, layer,
                            path, idx++) is { } def)
                    {
                        hooks.Add(def);
                    }
                }
            }
        }
        return hooks;
    }

    /// <summary>Um hook dentro de <c>{matcher, hooks:[…]}</c>.</summary>
    private WorkspaceHookDefinition? ParseInnerHook(
        JsonElement ih, string eventName, string? matcher,
        string layer, string path, int idx)
    {
        if (ih.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = Str(ih, "type") ?? HookTypeCommand;
        var command = Str(ih, HookTypeCommand) ?? Str(ih, "prompt");
        if (type is not (HookTypeCommand or "prompt")
            || string.IsNullOrWhiteSpace(command))
        {
            logger.LogWarning(
                "Workspace hook {Path}: bad type/missing command"
                    + " — skipped", path);
            return null;
        }

        return new WorkspaceHookDefinition
        {
            Event = eventName,
            Matcher = matcher,
            Type = type,
            Command = type == HookTypeCommand ? command.Trim() : command,
            TimeoutSeconds = TimeoutFrom(ih),
            // SPEC-173: fontes agrupadas — PreToolUse deny semântico
            // (exit≠0 ou decision JSON vetam; sem opt-in `blocking`).
            Blocking = eventName == "on_tool_pre",
            Name = $"{BaseName(path)}#{eventName}[{idx}]",
            FilePath = path,
            Layer = layer,
        };
    }

    private static int TimeoutFrom(JsonElement el)
    {
        foreach (var key in new[] { "timeout_sec", "timeout" })
        {
            if (el.TryGetProperty(key, out var t)
                && t.ValueKind == JsonValueKind.Number
                && t.TryGetInt32(out var n))
            {
                return Math.Clamp(n, 1, 300);
            }
        }
        return 60;
    }

    private WorkspaceHookDefinition? ParseHook(
        JsonElement el, string layer, string fileName, string? path)
    {
        var eventName = NormalizeEvent(Str(el, "event") ?? Str(el, "on"));
        if (eventName is null)
        {
            logger.LogWarning(
                "Workspace hook {Name}: unknown event '{Event}' — skipped",
                fileName, Str(el, "event"));
            return null;
        }

        var type = Str(el, "type") ?? HookTypeCommand;
        var command = Str(el, HookTypeCommand) ?? Str(el, "prompt");
        if (type is not (HookTypeCommand or "prompt")
            || string.IsNullOrWhiteSpace(command))
        {
            logger.LogWarning(
                "Workspace hook {Name}: bad type/missing command — skipped",
                fileName);
            return null;
        }

        if (type == HookTypeCommand)
        {
            command = command.Trim();
        }

        return new WorkspaceHookDefinition
        {
            Event = eventName,
            Matcher = Str(el, "matcher"),
            Type = type,
            Command = command,
            TimeoutSeconds = TimeoutFrom(el),
            Blocking = el.TryGetProperty("blocking", out var b)
                && b.ValueKind == JsonValueKind.True,
            Name = fileName,
            FilePath = path,
            Layer = layer,
        };
    }

    /// <summary>
    /// SPEC-173 RF-002: stdout JSON <c>{"decision":"deny|block"}</c> de um
    /// command hook — veto explícito (com <c>reason</c> opcional).
    /// </summary>
    internal static bool TryDecision(string stdout, out string? reason)
    {
        reason = null;
        var s = stdout.Trim();
        if (!s.StartsWith('{'))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.TryGetProperty("decision", out var d)
                && d.ValueKind == JsonValueKind.String
                && d.GetString() is "deny" or "block")
            {
                reason = root.TryGetProperty("reason", out var r)
                    && r.ValueKind == JsonValueKind.String
                        ? r.GetString() : null;
                return true;
            }
        }
        catch (JsonException)
        {
            // não-JSON — não é decisão.
        }
        return false;
    }

    private static string BaseName(string path)
    {
        var i = path.LastIndexOf('/');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    /// <summary>
    /// Canonical SC event names plus OWUIC/Claude-Code aliases so hook
    /// files port over without renames.
    /// </summary>
    internal static string? NormalizeEvent(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "on_conversation_start" or "sessionstart" => "on_conversation_start",
            "on_message" or "userpromptsubmit" => "on_message",
            "on_tool_pre" or "pretooluse" => "on_tool_pre",
            "on_tool_post" or "posttooluse" => "on_tool_post",
            "on_finish" or "stop" or "sessionend" => "on_finish",
            _ => null,
        };

    private static string EventName(HookEvent e) => e switch
    {
        HookEvent.OnConversationStart => "on_conversation_start",
        HookEvent.OnMessage => "on_message",
        HookEvent.OnToolPre => "on_tool_pre",
        HookEvent.OnToolPost => "on_tool_post",
        _ => "on_finish",
    };

    /// <summary>Glob match: <c>*</c> = any run, <c>?</c> = one char,
    /// <c>|</c> = alternação (SPEC-173 — <c>"Bash|File*"</c>);
    /// null matcher or null tool ⇒ all/none semantics per event.</summary>
    internal static bool GlobMatches(string? matcher, string? toolName)
    {
        if (matcher is null)
        {
            return true;
        }

        if (toolName is null)
        {
            return false;
        }

        foreach (var alt in matcher.Split('|',
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries))
        {
            var pattern = "^" + Regex.Escape(alt)
                .Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (Regex.IsMatch(toolName, pattern,
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(250)))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<(bool Success, int? Exit, string? Error,
        string? StdOut)> RunCommandAsync(
            HookContext context, WorkspaceHookDefinition hook,
            CancellationToken ct)
    {
        var exec = context.Services?
            .GetService(typeof(ISandboxExecutor)) as ISandboxExecutor;
        if (context.Sandbox is null || exec is null)
        {
            return (false, null,
                "command hook requires a running sandbox", null);
        }

        // SPEC-173 RF-002: env do hook — TOOL_NAME, TOOL_ARGS_JSON
        // (cap 8 KB), TOOL_MATCHER e HOOK_EVENT antes do comando.
        var argsJson = context.HookInput is { } hi
            ? hi.GetRawText() : "{}";
        if (argsJson.Length > 8192)
        {
            argsJson = argsJson[..8192];
        }
        var env = "export"
            + $" TOOL_NAME={Q(context.ToolName)}"
            + $" TOOL_MATCHER={Q(hook.Matcher)}"
            + $" HOOK_EVENT={Q(hook.Event)}"
            + $" TOOL_ARGS_JSON={Q(argsJson)}; ";

        var result = await exec.ExecAsync(context.Sandbox,
            env + hook.Command,
            TimeSpan.FromSeconds(hook.TimeoutSeconds), ct);
        return result.ExitCode == 0
            ? (true, result.ExitCode, null, result.StdOut)
            : (false, result.ExitCode,
                $"exit_code={result.ExitCode}: "
                + SecretRedactor.Redact(result.StdErr), result.StdOut);
    }

    private static string Q(string? value) =>
        "'" + (value ?? "").Replace("'", "'\\''") + "'";

    /// <summary>
    /// SPEC-173 RF-002: UserPromptSubmit — stdout do command hook vira
    /// mensagem hook-sourced com envelope <c>&lt;hook_context&gt;</c>
    /// (cap 4 KB).
    /// </summary>
    private async Task InjectHookContextAsync(
        HookContext context, string stdout)
    {
        var text = stdout.Trim();
        if (text.Length > 4096)
        {
            text = text[..4096];
        }
        await events.AppendAsync(context.ConversationId,
            new MessageEvent
            {
                Source = EventSource.Hook,
                LlmMessage = JsonSerializer.SerializeToElement(new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = $"<hook_context>\n{text}\n</hook_context>",
                        },
                    },
                }, EventJson.Options),
            }, CancellationToken.None);
    }

    private async Task InjectPromptAsync(
        HookContext context, WorkspaceHookDefinition hook) =>
        await events.AppendAsync(context.ConversationId,
            new MessageEvent
            {
                Source = EventSource.Hook,
                LlmMessage = JsonSerializer.SerializeToElement(new
                {
                    role = "user",
                    content = new[]
                    {
                        new { type = "text", text = hook.Command },
                    },
                }, EventJson.Options),
            }, CancellationToken.None);

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}