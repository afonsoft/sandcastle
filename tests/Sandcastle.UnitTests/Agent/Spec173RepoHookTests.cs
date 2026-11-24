using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Sandcastle.Application.Hooks;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Hooks;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Hooks;

namespace Sandcastle.UnitTests.Agent;

/// <summary>
/// SPEC-173: descoberta multi-fonte de hooks — <c>.agents/hooks.json</c>
/// canônico + compat <c>.devin</c>/<c>.claude</c>, schema agrupado,
/// alternância <c>|</c> no matcher, deny semântico (exit≠0 e
/// <c>{"decision":"deny|block"}</c>), <c>&lt;hook_context&gt;</c> em
/// UserPromptSubmit, e config <c>Hooks:*</c>.
/// </summary>
public sealed class Spec173RepoHookTests
{
    private const string AgentsDump = """
        ===repo:.agents/hooks.json
        {"PreToolUse":[{"matcher":"Bash|File*","hooks":[{"type":"command","command":"./guard.sh","timeout_sec":15}]}],"UserPromptSubmit":[{"hooks":[{"type":"command","command":"./ctx.sh"}]}]}
        ===repo:.devin/hooks.v1.json
        {"PostToolUse":[{"matcher":"*","hooks":[{"type":"command","command":"./audit.sh"}]}]}
        ===repo:.claude/settings.json
        {"permissions":{"allow":[]},"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"./init.sh"}]}]}}
        ===user:$HOME/.agents/hooks.json
        {"PreToolUse":[{"hooks":[{"type":"prompt","prompt":"be careful"}]}]}
        """;

    private static WorkspaceHookService Sut(FakeEventService events) =>
        new(events, NullLogger<WorkspaceHookService>.Instance);

    private static HookContext Ctx(
        FakeEventService events, FakeSandboxExecutor exec, string? tool,
        string? argsJson = null)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISandboxExecutor>(exec);
        return new HookContext
        {
            ConversationId = Guid.NewGuid(),
            Sandbox = new Sandbox(Guid.NewGuid(), Guid.Empty, null, "fake",
                SandboxStatus.Running, null, DateTimeOffset.UtcNow, null,
                null),
            Services = sc.BuildServiceProvider(),
            ToolName = tool,
            HookInput = argsJson is null ? null
                : System.Text.Json.JsonDocument.Parse(argsJson).RootElement,
        };
    }

    [Fact]
    public void ParseDump_Grouped_Schema_All_Sources()
    {
        var hooks = Sut(new FakeEventService()).ParseDump(AgentsDump);
        hooks.Count.ShouldBe(5);
        // .agents — dois eventos no mesmo arquivo.
        hooks[0].Event.ShouldBe("on_tool_pre");
        hooks[0].Matcher.ShouldBe("Bash|File*");
        hooks[0].TimeoutSeconds.ShouldBe(15);
        hooks[0].Blocking.ShouldBeTrue(); // PreToolUse agrupado = deny
        hooks[0].FilePath.ShouldBe(".agents/hooks.json");
        hooks[0].Layer.ShouldBe("repo");
        hooks[1].Event.ShouldBe("on_message");
        hooks[1].Blocking.ShouldBeFalse();
        // compat .devin
        hooks[2].Event.ShouldBe("on_tool_post");
        hooks[2].FilePath.ShouldBe(".devin/hooks.v1.json");
        // compat .claude — hooks dentro de settings["hooks"].
        hooks[3].Event.ShouldBe("on_conversation_start");
        hooks[3].FilePath.ShouldBe(".claude/settings.json");
        // usuário — prompt hook em ~/.agents.
        hooks[4].Event.ShouldBe("on_tool_pre");
        hooks[4].Type.ShouldBe("prompt");
        hooks[4].Layer.ShouldBe("user");
        hooks[4].FilePath.ShouldBe("$HOME/.agents/hooks.json");
    }

    [Fact]
    public void ParseDump_Grouped_Skips_Unknown_Keys()
    {
        var hooks = Sut(new FakeEventService()).ParseDump(
            """
            ===repo:.devin/config.json
            {"model":"x","hooks":{"PreToolUse":[{"hooks":[{"command":"./a"}]}]},"other":42}
            """);
        hooks.ShouldHaveSingleItem().Event.ShouldBe("on_tool_pre");
    }

    [Fact]
    public void GlobMatches_Pipe_Alternation()
    {
        WorkspaceHookService.GlobMatches("Bash|File*", "bash")
            .ShouldBeTrue();
        WorkspaceHookService.GlobMatches("Bash|File*", "file_read")
            .ShouldBeTrue();
        WorkspaceHookService.GlobMatches("Bash|File*", "web_search")
            .ShouldBeFalse();
    }

    [Fact]
    public void TryDecision_Deny_And_Block_With_Reason()
    {
        WorkspaceHookService.TryDecision(
            """{"decision":"deny","reason":"nope"}""", out var r1)
            .ShouldBeTrue();
        r1.ShouldBe("nope");
        WorkspaceHookService.TryDecision(
            """{"decision":"block"}""", out _).ShouldBeTrue();
        WorkspaceHookService.TryDecision(
            """{"decision":"approve"}""", out _).ShouldBeFalse();
        WorkspaceHookService.TryDecision("plain text", out _)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Grouped_PreToolUse_ExitNonZero_Denies_Without_Blocking()
    {
        var events = new FakeEventService();
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = cmd => cmd.Contains("for f in")
                ? new CommandResult(0, AgentsDump, "", false)
                : new CommandResult(1, "", "boom", false),
        };

        var blocked = await Sut(events).RunAsync(
            HookEvent.OnToolPre, Ctx(events, exec, "bash"),
            CancellationToken.None);

        blocked.ShouldBeTrue();
        var fired = events.Events.OfType<HookFiredEvent>().ToList();
        fired[0].FilePath.ShouldBe(".agents/hooks.json");
        fired[0].Success.ShouldBeFalse();
        exec.Commands.ShouldContain(c => c.Contains("guard.sh")
            && c.Contains("TOOL_NAME='bash'"));
    }

    [Fact]
    public async Task Decision_Deny_Stdout_Vetoes()
    {
        var events = new FakeEventService();
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = cmd => cmd.Contains("for f in")
                ? new CommandResult(0, AgentsDump, "", false)
                : new CommandResult(0,
                    """{"decision":"deny","reason":"policy"}""", "", false),
        };

        var blocked = await Sut(events).RunAsync(
            HookEvent.OnToolPre, Ctx(events, exec, "bash"),
            CancellationToken.None);

        blocked.ShouldBeTrue();
        events.Events.OfType<HookFiredEvent>().First().Error
            .ShouldBe("policy");
    }

    [Fact]
    public async Task OnMessage_Stdout_Becomes_HookContext()
    {
        var events = new FakeEventService();
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = cmd => cmd.Contains("for f in")
                ? new CommandResult(0, AgentsDump, "", false)
                : new CommandResult(0, "repo uses dotnet 10", "", false),
        };

        await Sut(events).RunAsync(HookEvent.OnMessage,
            Ctx(events, exec, null), CancellationToken.None);

        var msg = events.Events.OfType<MessageEvent>().Single();
        msg.Source.ShouldBe(EventSource.Hook);
        var text = msg.LlmMessage.GetRawText();
        text.ShouldContain("hook_context"); // < escapa como < no JSON
        text.ShouldContain("dotnet 10");
    }

    [Fact]
    public void BuildDump_Canonical_First_Compat_On()
    {
        var cmd = Sut(new FakeEventService()).BuildDumpCommand();
        cmd.ShouldContain(".agents/hooks.json");
        cmd.ShouldContain(".agents/hooks/*.json");
        cmd.ShouldContain(".devin/hooks.v1.json");
        cmd.ShouldContain(".claude/settings.json");
        cmd.ShouldContain("$HOME/.agents/hooks.json");
    }

    [Fact]
    public void BuildDump_CompatDirs_Off_Skips_Devin_And_Claude()
    {
        var cfg = new FakeConfiguration(
            new Dictionary<string, string?>
            { ["Hooks:CompatDirs"] = "false" });
        var svc = new WorkspaceHookService(new FakeEventService(),
            NullLogger<WorkspaceHookService>.Instance, cfg);
        var cmd = svc.BuildDumpCommand();
        cmd.ShouldNotContain(".devin");
        cmd.ShouldNotContain(".claude");
        cmd.ShouldContain(".agents/hooks.json");
    }

    [Fact]
    public async Task Hooks_Enabled_False_ShortCircuits()
    {
        var cfg = new FakeConfiguration(
            new Dictionary<string, string?>
            { ["Hooks:Enabled"] = "false" });
        var events = new FakeEventService();
        var exec = new FakeSandboxExecutor
        {
            ExecHandler = _ => new CommandResult(0, AgentsDump, "", false),
        };
        var svc = new WorkspaceHookService(events,
            NullLogger<WorkspaceHookService>.Instance, cfg);

        (await svc.RunAsync(HookEvent.OnToolPre,
            Ctx(events, exec, "bash"), CancellationToken.None))
            .ShouldBeFalse();
        exec.Commands.ShouldBeEmpty();
    }

    /// <summary>IConfiguration mínimo dict-backed (evita o pacote Memory).</summary>
    private sealed class FakeConfiguration(Dictionary<string, string?> values)
        : Microsoft.Extensions.Configuration.IConfiguration
    {
        public string? this[string key]
        {
            get => values.TryGetValue(key, out var v) ? v : null;
            set => values[key] = value;
        }

        public IEnumerable<Microsoft.Extensions.Configuration
            .IConfigurationSection> GetChildren() => [];

        public Microsoft.Extensions.Primitives.IChangeToken
            GetReloadToken() => new NullToken();

        public Microsoft.Extensions.Configuration.IConfigurationSection
            GetSection(string key) => new Section(this, key);

        private sealed class Section(FakeConfiguration root, string key)
            : Microsoft.Extensions.Configuration.IConfigurationSection
        {
            public string Key => key[(key.LastIndexOf(':') + 1)..];
            public string Path => key;
            public string? Value
            {
                get => root[key];
                set => root[key] = value;
            }

            public string? this[string sub]
            {
                get => root[$"{key}:{sub}"];
                set => root[$"{key}:{sub}"] = value;
            }

            public IEnumerable<Microsoft.Extensions.Configuration
                .IConfigurationSection> GetChildren() => [];

            public Microsoft.Extensions.Primitives.IChangeToken
                GetReloadToken() => new NullToken();

            public Microsoft.Extensions.Configuration.IConfigurationSection
                GetSection(string sub) =>
                new Section(root, $"{key}:{sub}");
        }

        private sealed class NullToken
            : Microsoft.Extensions.Primitives.IChangeToken
        {
            public bool HasChanged => false;
            public bool ActiveChangeCallbacks => false;
            public IDisposable RegisterChangeCallback(
                Action<object?> callback, object? state) =>
                new NoopDisposable();

            private sealed class NoopDisposable : IDisposable
            {
                public void Dispose() { }
            }
        }
    }
}
