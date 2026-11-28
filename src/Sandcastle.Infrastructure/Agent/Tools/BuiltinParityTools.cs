using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Events;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-182: parity-oriented agent tools — validation, surgical context,
/// reminders/mode/plan transitions, granular browser actions, context ops
/// and compatibility aliases. Implementations are sandbox-safe and fail
/// explicitly when an optional backend capability is unavailable.
/// </summary>
public static class BuiltinParityTools
{
    private const string WorkspaceProbe =
        "cd /workspace/repo 2>/dev/null || cd /workspace";

    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static string? Str(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Sh(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string TrimCap(string? text, int max = 16_000)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
        return text[..max] + $"\n[truncated at {max} chars]";
    }

    private static string Xml(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static AgentTool Alias(
        string name, string target, string description, bool risky,
        string schema) => new(
        new ToolSchema(name, description, Schema(schema)),
        Risky: risky,
        Handler: async (ctx, args, ct) =>
        {
            var registry = ctx.Services.GetService<IToolRegistry>();
            var tool = registry?.Find(target);
            return tool is null
                ? ToolResult.Error($"alias target '{target}' unavailable")
                : await tool.Handler(ctx, args, ct);
        });

    /// <summary>SPEC-182 RF-005: shell_exec compatibility alias.</summary>
    public static AgentTool ShellExecAlias() => Alias(
        "shell_exec", "bash",
        "Compatibility alias for bash — run a shell command in the workspace.",
        risky: true,
        schema: """{"type":"object","properties":{"command":{"type":"string"},"timeout_seconds":{"type":"integer"}},"required":["command"]}""");

    /// <summary>SPEC-182 RF-005: fetch_url compatibility alias.</summary>
    public static AgentTool FetchUrlAlias() => Alias(
        "fetch_url", "web_fetch",
        "Compatibility alias for web_fetch — fetch a URL and return markdown-lite text.",
        risky: false,
        schema: """{"type":"object","properties":{"url":{"type":"string"},"max_chars":{"type":"integer"}},"required":["url"]}""");

    /// <summary>SPEC-182 RF-005: todo_write compatibility alias.</summary>
    public static AgentTool TodoWriteAlias() => Alias(
        "todo_write", "task_tracker",
        "Compatibility alias for task_tracker — replace the visible task checklist.",
        risky: false,
        schema: """{"type":"object","properties":{"items":{"type":"array","items":{"type":"object"}}},"required":["items"]}""");

    /// <summary>SPEC-182 RF-001: project validation evidence.</summary>
    public static AgentTool AgentValidate() => new(
        new ToolSchema("agent_validate",
            "Validate the project: auto-detect its test command or run an explicit command, then return exit evidence plus git cleanliness.",
            Schema("""
                {"type":"object","properties":{
                  "command":{"type":"string","description":"Optional validation command override"},
                  "timeout_seconds":{"type":"integer","default":300}},
                 "additionalProperties":false}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var command = Str(args, "command");
            var detected = command is null;
            if (command is null)
            {
                var detect = await ctx.Executor.ExecAsync(ctx.Sandbox,
                    WorkspaceProbe + "; if find . -maxdepth 3 \\( -name '*.sln' -o -name '*.slnx' -o -name '*.csproj' \\) -print -quit | grep -q .; then echo 'dotnet test'; elif test -f package.json; then echo 'npm test'; elif test -f pyproject.toml -o -f pytest.ini; then echo 'pytest'; elif test -f go.mod; then echo 'go test ./...'; elif test -f Cargo.toml; then echo 'cargo test'; fi",
                    TimeSpan.FromSeconds(20), ct);
                command = detect.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim();
            }
            if (string.IsNullOrWhiteSpace(command))
            {
                return ToolResult.Error("no recognized project stack; pass command explicitly");
            }

            var seconds = args.TryGetProperty("timeout_seconds", out var ts)
                ? Math.Clamp(ts.GetInt32(), 1, 600) : 300;
            var run = await ctx.Executor.ExecAsync(ctx.Sandbox,
                WorkspaceProbe + "; " + command,
                TimeSpan.FromSeconds(seconds), ct);
            var git = await ctx.Executor.ExecAsync(ctx.Sandbox,
                WorkspaceProbe + "; git status --porcelain 2>/dev/null | wc -l",
                TimeSpan.FromSeconds(15), ct);
            var uncommitted = int.TryParse(git.StdOut.Trim(), out var count) ? count : 0;
            return ToolResult.Ok(new
            {
                validated = run.ExitCode == 0 && !run.TimedOut,
                command,
                detected,
                exit_code = run.ExitCode,
                timed_out = run.TimedOut,
                git_clean = uncommitted == 0,
                uncommitted,
                output = SecretRedactor.Redact(TrimCap(run.StdOut + run.StdErr)),
            });
        });

    /// <summary>SPEC-182 RF-001: python/node snippet execution.</summary>
    public static AgentTool CodeInterpreter() => new(
        new ToolSchema("code_interpreter",
            "Run a python3 or node snippet in the workspace and return stdout/stderr evidence.",
            Schema("""
                {"type":"object","properties":{
                  "language":{"type":"string","enum":["python3","python","node"]},
                  "code":{"type":"string"},
                  "timeout_seconds":{"type":"integer","default":60}},
                 "required":["language","code"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var language = Str(args, "language");
            var code = Str(args, "code");
            if (string.IsNullOrWhiteSpace(code)) return ToolResult.Error("code_required");
            var (binary, ext) = language switch
            {
                "python3" or "python" => ("python3", ".py"),
                "node" => ("node", ".js"),
                _ => (null, null),
            };
            if (binary is null) return ToolResult.Error("unsupported_language");
            var seconds = args.TryGetProperty("timeout_seconds", out var ts)
                ? Math.Clamp(ts.GetInt32(), 1, 300) : 60;
            var path = $"/workspace/.sandcastle-tmp/{Guid.NewGuid():N}{ext}";
            await ctx.Executor.ExecAsync(ctx.Sandbox,
                "mkdir -p /workspace/.sandcastle-tmp", TimeSpan.FromSeconds(10), ct);
            await ctx.Executor.WriteFileAsync(ctx.Sandbox, path, code, ct);
            try
            {
                var result = await ctx.Executor.ExecAsync(ctx.Sandbox,
                    WorkspaceProbe + "; " + binary + " " + Sh(path),
                    TimeSpan.FromSeconds(seconds), ct);
                return ToolResult.Ok(new
                {
                    language = binary,
                    exit_code = result.ExitCode,
                    timed_out = result.TimedOut,
                    stdout = SecretRedactor.Redact(TrimCap(result.StdOut)),
                    stderr = SecretRedactor.Redact(TrimCap(result.StdErr, 4_000)),
                });
            }
            finally
            {
                await ctx.Executor.ExecAsync(ctx.Sandbox,
                    "rm -f -- " + Sh(path), TimeSpan.FromSeconds(10), CancellationToken.None);
            }
        });

    /// <summary>SPEC-182 RF-001: restricted instant context collection.</summary>
    public static AgentTool InstantContext() => new(
        new ToolSchema("instant_context",
            "Collect precise read-only workspace context. Commands: 'rg <pattern>', 'readfile <path> <start> <end>', 'tree <path> [depth]'. Returns strict XML snippets.",
            Schema("""
                {"type":"object","properties":{
                  "goal":{"type":"string"},
                  "commands":{"type":"array","items":{"type":"string"},"maxItems":8}},
                 "required":["commands"]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            if (!args.TryGetProperty("commands", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return ToolResult.Error("commands_required");
            }
            var xml = new StringBuilder("<ANSWER>");
            var errors = new List<string>();
            foreach (var item in arr.EnumerateArray().Take(8))
            {
                var command = item.GetString() ?? "";
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string shell;
                if (parts.Length >= 2 && parts[0] == "rg")
                {
                    var pattern = command[3..].Trim();
                    shell = "rg -n --max-count 20 -- " + Sh(pattern) + " . 2>/dev/null | head -80";
                }
                else if (parts.Length == 4 && parts[0] == "readfile"
                    && int.TryParse(parts[2], out var a) && int.TryParse(parts[3], out var b)
                    && a >= 1 && b >= a && b - a <= 400
                    && !parts[1].Contains("..", StringComparison.Ordinal))
                {
                    shell = "sed -n " + Sh($"{a},{b}p") + " -- " + Sh(parts[1]) + " 2>/dev/null";
                }
                else if (parts.Length is 2 or 3 && parts[0] == "tree"
                    && !parts[1].Contains("..", StringComparison.Ordinal))
                {
                    var depth = parts.Length == 3 && int.TryParse(parts[2], out var d)
                        ? Math.Clamp(d, 1, 5) : 2;
                    shell = "find " + Sh(parts[1]) + " -maxdepth " + depth + " -print 2>/dev/null | sort | head -200";
                }
                else
                {
                    errors.Add(command + " — unsupported or invalid restricted command");
                    continue;
                }
                var result = await ctx.Executor.ExecAsync(ctx.Sandbox,
                    WorkspaceProbe + "; " + shell, TimeSpan.FromSeconds(20), ct);
                var text = TrimCap(result.StdOut + result.StdErr, 6_000);
                xml.Append("<command name=\"").Append(Xml(parts[0])).Append("\">")
                    .Append(Xml(text)).Append("</command>");
            }
            xml.Append("</ANSWER>");
            return ToolResult.Ok(new
            {
                goal = Str(args, "goal"),
                xml = xml.ToString(),
                errors,
            });
        });

    /// <summary>SPEC-182 RF-001: structured durable memory lesson.</summary>
    public static AgentTool MemoryLearn() => new(
        new ToolSchema("memory_learn",
            "Persist a durable lesson learned. Use a stable key and concise text; scope may be project or org.",
            Schema("""
                {"type":"object","properties":{
                  "key":{"type":"string"},"text":{"type":"string"},
                  "scope":{"type":"string","enum":["project","org"],"default":"project"}},
                 "required":["key","text"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var key = Str(args, "key");
            var text = Str(args, "text");
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(text))
                return ToolResult.Error("key_and_text_required");
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                op = "remember",
                key,
                text,
                scope = Str(args, "scope") ?? "project",
            }));
            return await BuiltinTools.Memory().Handler(ctx, doc.RootElement, ct);
        });

    /// <summary>SPEC-182 RF-001: conversation-local reminder.</summary>
    public static AgentTool Reminder() => new(
        new ToolSchema("reminder",
            "Schedule a conversation-local reminder. It is surfaced by unseen_messages when due.",
            Schema("""
                {"type":"object","properties":{
                  "text":{"type":"string"},
                  "delay_minutes":{"type":"integer","default":5}},
                 "required":["text"]}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var db = ctx.Services.GetService<AppDbContext>();
            var text = Str(args, "text");
            if (db is null) return ToolResult.Error("reminder store unavailable");
            if (string.IsNullOrWhiteSpace(text)) return ToolResult.Error("text_required");
            var minutes = args.TryGetProperty("delay_minutes", out var dm)
                ? Math.Clamp(dm.GetInt32(), 1, 7 * 24 * 60) : 5;
            var id = Guid.NewGuid();
            var due = DateTimeOffset.UtcNow.AddMinutes(minutes);
            db.ConversationMemories.Add(new ConversationMemoryRecord
            {
                ConversationId = ctx.ConversationId,
                Key = "reminder:" + id,
                Value = JsonSerializer.Serialize(new { text, due }),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            return ToolResult.Ok(new { scheduled = true, id, due });
        });

    /// <summary>SPEC-182 RF-001: read pending user steering and due reminders.</summary>
    public static AgentTool UnseenMessages() => new(
        new ToolSchema("unseen_messages",
            "Read user messages that arrived while you were working, plus due conversation reminders.",
            Schema("""{"type":"object","properties":{}}""")),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var page = await ctx.Events.SearchAsync(ctx.ConversationId,
                new EventQuery(KindEq: "MessageEvent",
                    Sort: EventSortOrder.TimestampDesc, Limit: 100), ct);
            var unseen = new List<string>();
            foreach (var ev in page.Items.OfType<MessageEvent>())
            {
                if (ev.Source == EventSource.Agent) break;
                if (ev.Source == EventSource.User) unseen.Add(ContentText(ev));
            }
            var reminders = new List<string>();
            var db = ctx.Services.GetService<AppDbContext>();
            if (db is not null)
            {
                var rows = await db.ConversationMemories
                    .Where(m => m.ConversationId == ctx.ConversationId
                        && m.Key.StartsWith("reminder:"))
                    .ToListAsync(ct);
                foreach (var row in rows)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(row.Value);
                        if (doc.RootElement.TryGetProperty("due", out var dueEl)
                            && DateTimeOffset.TryParse(dueEl.GetString(), out var due)
                            && due <= DateTimeOffset.UtcNow)
                        {
                            reminders.Add(doc.RootElement.GetProperty("text").GetString() ?? "");
                            db.ConversationMemories.Remove(row);
                        }
                    }
                    catch (JsonException) { db.ConversationMemories.Remove(row); }
                }
                await db.SaveChangesAsync(ct);
            }
            return ToolResult.Ok(new
            {
                count = unseen.Count + reminders.Count,
                messages = unseen.AsEnumerable().Reverse().ToArray(),
                reminders,
                text = unseen.Count + reminders.Count == 0
                    ? "no new messages"
                    : string.Join('\n', unseen.AsEnumerable().Reverse().Concat(reminders)),
            });
        });

    /// <summary>SPEC-182 RF-003: manual context compaction.</summary>
    public static AgentTool ContextCompact() => new(
        new ToolSchema("context_compact",
            "Compact the stored conversation history now, optionally guided by instructions.",
            Schema("""
                {"type":"object","properties":{"instructions":{"type":"string"}}}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var compaction = ctx.Services.GetService<ICompactionService>();
            if (compaction is null) return ToolResult.Error("compaction service unavailable");
            var compacted = await compaction.CompactStoredAsync(ctx.ConversationId,
                CompactionTrigger.Manual, Str(args, "instructions"), ct);
            return ToolResult.Ok(new { compacted });
        });

    /// <summary>SPEC-182 RF-003: recover latest compaction summary.</summary>
    public static AgentTool ContextRecovery() => new(
        new ToolSchema("context_recovery",
            "Recover the latest durable condensation summary and context ledger hints.",
            Schema("""{"type":"object","properties":{}}""")),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var page = await ctx.Events.SearchAsync(ctx.ConversationId,
                new EventQuery(Sort: EventSortOrder.TimestampDesc, Limit: 100), ct);
            var summary = page.Items.OfType<CondensationSummaryEvent>()
                .FirstOrDefault()?.Summary
                ?? page.Items.OfType<Condensation>().FirstOrDefault()?.Summary;
            return summary is null
                ? ToolResult.Error("no compaction summary available")
                : ToolResult.Ok(new { summary });
        });

    /// <summary>SPEC-182 RF-003: operational context usage payload.</summary>
    public static AgentTool ContextUsage() => new(
        new ToolSchema("context_usage",
            "Return current context usage evidence from the latest context ledger, when available.",
            Schema("""{"type":"object","properties":{}}""")),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var page = await ctx.Events.SearchAsync(ctx.ConversationId,
                new EventQuery(KindEq: "ConversationStateUpdateEvent",
                    Sort: EventSortOrder.TimestampDesc, Limit: 100), ct);
            var ledger = page.Items.OfType<ConversationStateUpdateEvent>()
                .FirstOrDefault(e => e.Key == "context_state");
            if (ledger?.Value is not { } value)
            {
                return ToolResult.Error("no context ledger available yet");
            }
            return ToolResult.Ok(new
            {
                tokens_estimate = value.TryGetProperty("tokens_estimate", out var tokens)
                    ? tokens.GetInt32() : 0,
                window = value.TryGetProperty("Window", out var window)
                    && window.ValueKind == JsonValueKind.Number ? window.GetInt32() : (int?)null,
                compactions = value.TryGetProperty("Compactions", out var compactions)
                    ? compactions.GetInt32() : 0,
                model = value.TryGetProperty("Model", out var model)
                    ? model.GetString() : null,
                updated_at = value.TryGetProperty("updated_at", out var updated)
                    ? updated.GetDateTimeOffset() : (DateTimeOffset?)null,
                raw = value,
            });
        });

    /// <summary>SPEC-182 RF-003: explicit context-window extension request.</summary>
    public static AgentTool ExtendContext() => new(
        new ToolSchema("extend_context",
            "Request a bounded emergency-context extension. Use only when compaction would lose critical detail.",
            Schema("""
                {"type":"object","properties":{"reason":{"type":"string"}}}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var budget = ctx.Services.GetService<ExtendContextBudget>()
                ?? new ExtendContextBudget();
            var status = budget.Request(ctx.ConversationId.ToString());
            await ctx.EmitAsync(new ConversationStateUpdateEvent
            {
                Key = "extend_context",
                Value = JsonSerializer.SerializeToElement(new
                {
                    status = status.ToString().ToLowerInvariant(),
                    reason = Str(args, "reason"),
                }),
            }, ct);
            return ToolResult.Ok(new
            {
                status = status == ExtendContextBudget.Status.Granted ? "granted" : "at_ceiling",
                granted = status == ExtendContextBudget.Status.Granted,
            });
        });

    /// <summary>SPEC-182 RF-001: leave plan mode after approved artifact.</summary>
    public static AgentTool PlanExit() => new(
        new ToolSchema("plan_exit",
            "Exit plan mode only after the persisted plan artifact is fully approved; marks it executed and switches the conversation to build.",
            Schema("""{"type":"object","properties":{}}""")),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var plans = ctx.Services.GetService<IPlanArtifactService>();
            var conversations = ctx.Services.GetService<IAppConversationService>();
            if (plans is null || conversations is null)
                return ToolResult.Error("plan services unavailable");
            try
            {
                var artifact = await plans.ExecuteAsync(ctx.ConversationId, ct);
                if (artifact is null) return ToolResult.Error("plan artifact not found");
                await conversations.SetModeAsync(ctx.ConversationId, "build", ct);
                return ToolResult.Ok(new { exited = true, version = artifact.Version });
            }
            catch (PlanArtifactStateException ex)
            {
                return ToolResult.Error(ex.Code);
            }
        });

    /// <summary>SPEC-182 RF-001: explicit conversation mode switch.</summary>
    public static AgentTool ModeSwitch() => new(
        new ToolSchema("mode_switch",
            "Switch conversation mode. build→plan is immediate; plan→build is only possible through plan_exit after approval.",
            Schema("""
                {"type":"object","properties":{"mode":{"type":"string","enum":["plan","build"]}},"required":["mode"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var mode = Str(args, "mode");
            if (mode is not ("plan" or "build")) return ToolResult.Error("mode_invalid");
            var conversations = ctx.Services.GetService<IAppConversationService>();
            if (conversations is null) return ToolResult.Error("conversation service unavailable");
            if (mode == "build")
            {
                return ToolResult.Error("plan_exit_required");
            }
            var updated = await conversations.SetModeAsync(ctx.ConversationId, mode, ct);
            return updated is null
                ? ToolResult.Error("conversation not found")
                : ToolResult.Ok(new { mode });
        });

    private static AgentTool BrowserTool(
        string name, string description, string schema,
        Func<ToolCallContext, JsonElement, IBrowserService, CancellationToken, Task<ToolResult>> invoke,
        bool risky) => new(
        new ToolSchema(name, description, Schema(schema)), risky,
        async (ctx, args, ct) =>
        {
            var browser = ctx.Services.GetService<IBrowserService>();
            return browser is null
                ? ToolResult.Error("browser service unavailable")
                : await invoke(ctx, args, browser, ct);
        });

    private static async Task<ToolResult> BrowserResultAsync(
        IBrowserService browser, Guid conversationId,
        BrowserActRequest request, CancellationToken ct)
    {
        var result = await browser.ActAsync(conversationId, request, ct);
        return result.Ok
            ? ToolResult.Ok(new
            {
                ok = true,
                url = result.Url,
                title = result.Title,
                content = result.Content,
                screenshot_base64 = result.ScreenshotBase64,
            })
            : ToolResult.Error(result.Error ?? "browser action failed");
    }

    /// <summary>SPEC-182 RF-002: granular browser navigation.</summary>
    public static AgentTool BrowserNavigate() => BrowserTool(
        "browser_navigate", "Navigate the isolated conversation browser to a URL.",
        """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}""",
        (ctx, args, browser, ct) => BrowserResultAsync(browser, ctx.ConversationId,
            new BrowserActRequest(BrowserActionKind.Goto, Url: Str(args, "url")), ct),
        risky: true);

    /// <summary>SPEC-182 RF-002: browser text observation.</summary>
    public static AgentTool BrowserObserve() => BrowserTool(
        "browser_observe", "Observe visible text, URL and title in the isolated conversation browser.",
        """{"type":"object","properties":{}}""",
        (ctx, args, browser, ct) => BrowserResultAsync(browser, ctx.ConversationId,
            new BrowserActRequest(BrowserActionKind.Content), ct),
        risky: false);

    /// <summary>SPEC-182 RF-002: granular browser action.</summary>
    public static AgentTool BrowserAct() => BrowserTool(
        "browser_act", "Act in the isolated browser: click, type, scroll, back or forward.",
        """{"type":"object","properties":{"action":{"type":"string","enum":["click","type","scroll","back","forward"]},"selector":{"type":"string"},"text":{"type":"string"},"direction":{"type":"string","enum":["up","down"]}},"required":["action"]}""",
        (ctx, args, browser, ct) =>
        {
            if (!Enum.TryParse<BrowserActionKind>(Str(args, "action"), true, out var kind)
                || kind is BrowserActionKind.Goto or BrowserActionKind.Screenshot or BrowserActionKind.Content)
                return Task.FromResult(ToolResult.Error("unsupported browser action"));
            return BrowserResultAsync(browser, ctx.ConversationId,
                new BrowserActRequest(kind, Selector: Str(args, "selector"),
                    Text: Str(args, "text"), Direction: Str(args, "direction")), ct);
        },
        risky: true);

    /// <summary>SPEC-182 RF-002: browser screenshot for visual reasoning.</summary>
    public static AgentTool BrowserScreenshot() => BrowserTool(
        "browser_screenshot", "Capture a screenshot of the isolated conversation browser for visual reasoning.",
        """{"type":"object","properties":{}}""",
        (ctx, args, browser, ct) => BrowserResultAsync(browser, ctx.ConversationId,
            new BrowserActRequest(BrowserActionKind.Screenshot), ct),
        risky: false);

    /// <summary>SPEC-182 RF-002: close isolated browser context.</summary>
    public static AgentTool BrowserClose() => BrowserTool(
        "browser_close", "Close the isolated browser context for this conversation.",
        """{"type":"object","properties":{}}""",
        async (ctx, args, browser, ct) =>
        {
            await browser.CloseAsync(ctx.ConversationId, ct);
            return ToolResult.Ok(new { closed = true });
        },
        risky: false);

    private static AgentTool MediaTool(string name, string kind) => new(
        new ToolSchema(name,
            $"Generate {kind.ToLowerInvariant()} media through the configured optional provider. Returns an explicit error when disabled or unconfigured.",
            Schema("""
                {"type":"object","properties":{"prompt":{"type":"string"},"size":{"type":"string"},"seconds":{"type":"integer"}},"required":["prompt"]}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) =>
        {
            var prompt = Str(args, "prompt");
            if (string.IsNullOrWhiteSpace(prompt)) return ToolResult.Error("prompt_required");
            var config = ctx.Services.GetService<IConfiguration>();
            var enabled = config?.GetValue<bool>($"Media:{kind}GenerationEnabled") == true;
            var provider = config?[$"Media:{kind}Provider"];
            var endpoint = config?[$"Media:{kind}Endpoint"];
            if (!enabled || string.IsNullOrWhiteSpace(provider)
                || string.IsNullOrWhiteSpace(endpoint))
            {
                return ToolResult.Error(
                    $"{name} unavailable: enable Media:{kind}GenerationEnabled and configure Media:{kind}Provider/Endpoint");
            }
            if (!string.Equals(provider, "openai-compatible", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Error($"{name} provider '{provider}' is not supported");
            }

            var model = config![$"Media:{kind}Model"]
                ?? (kind == "Image" ? "gpt-image-1" : "video-generation");
            var apiKey = config[$"Media:{kind}ApiKey"];
            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["prompt"] = prompt,
            };
            if (Str(args, "size") is { } size) payload["size"] = size;
            if (args.TryGetProperty("seconds", out var sec)) payload["seconds"] = sec.GetInt32();

            var factory = ctx.Services.GetService<IHttpClientFactory>();
            var client = factory?.CreateClient($"media-{kind}") ?? new HttpClient();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await client.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    return ToolResult.Error(
                        $"{name} provider returned {(int)response.StatusCode}: {TrimCap(SecretRedactor.Redact(body), 1_000)}");
                }
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array
                    || data.GetArrayLength() == 0)
                {
                    return ToolResult.Error($"{name} provider returned no media data");
                }
                var first = data[0];
                byte[] bytes;
                if (first.TryGetProperty("b64_json", out var b64)
                    && !string.IsNullOrWhiteSpace(b64.GetString()))
                {
                    bytes = Convert.FromBase64String(b64.GetString()!);
                }
                else if (first.TryGetProperty("url", out var urlEl)
                    && Uri.TryCreate(urlEl.GetString(), UriKind.Absolute, out var mediaUrl))
                {
                    bytes = await client.GetByteArrayAsync(mediaUrl, ct);
                }
                else
                {
                    return ToolResult.Error($"{name} provider response lacks b64_json or url");
                }

                var extension = kind == "Image" ? "png" : "mp4";
                var path = $"/workspace/repo/generated/{kind.ToLowerInvariant()}-{Guid.NewGuid():N}.{extension}";
                await ctx.Executor.ExecAsync(ctx.Sandbox,
                    "mkdir -p /workspace/repo/generated", TimeSpan.FromSeconds(10), ct);
                await ctx.Executor.WriteFileBytesAsync(ctx.Sandbox, path, bytes, ct);
                return ToolResult.Ok(new
                {
                    generated = true,
                    path,
                    bytes = bytes.Length,
                    provider,
                    model,
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException)
            {
                return ToolResult.Error($"{name} failed: {ex.Message}");
            }
            finally
            {
                if (factory is null) client.Dispose();
            }
        });

    /// <summary>SPEC-182 RF-004: optional image generation.</summary>
    public static AgentTool GenerateImage() => MediaTool("generate_image", "Image");

    /// <summary>SPEC-182 RF-004: optional video generation.</summary>
    public static AgentTool GenerateVideo() => MediaTool("generate_video", "Video");

    private static string ContentText(MessageEvent ev)
    {
        try
        {
            if (!ev.LlmMessage.TryGetProperty("content", out var content))
                return "";
            if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
            if (content.ValueKind == JsonValueKind.Array)
            {
                return string.Join('\n', content.EnumerateArray()
                    .Where(c => c.ValueKind == JsonValueKind.Object)
                    .Select(c => c.TryGetProperty("text", out var t) ? t.GetString() : null)
                    .Where(t => !string.IsNullOrWhiteSpace(t))!);
            }
        }
        catch (InvalidOperationException) { }
        return "";
    }
}

/// <summary>SPEC-182 RF-003: per-conversation emergency-context extension budget.</summary>
public sealed class ExtendContextBudget
{
    /// <summary>Status returned by an extension request.</summary>
    public enum Status { Granted, AtCeiling }

    private sealed class State
    {
        public int Granted;
        public int Pending;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    /// <summary>Request one extension; ceiling is two per conversation/run scope.</summary>
    public Status Request(string key, int ceiling = 2)
    {
        lock (_states)
        {
            var state = _states.TryGetValue(key, out var existing)
                ? existing
                : _states[key] = new State();
            if (state.Granted >= ceiling) return Status.AtCeiling;
            state.Granted++;
            state.Pending++;
            return Status.Granted;
        }
    }

    /// <summary>Consumes one granted extension, allowing the loop to skip one emergency compaction.</summary>
    public bool TryConsume(string key)
    {
        lock (_states)
        {
            if (!_states.TryGetValue(key, out var state) || state.Pending <= 0) return false;
            state.Pending--;
            return true;
        }
    }
}