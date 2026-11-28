using System.Net;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Events;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Agent.Browser;
using Sandcastle.Infrastructure.Agent.Tools;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.UnitTests.Agent;

/// <summary>SPEC-182 RF-001..RF-006: schema, dispatch, permissions and sandbox-facing parity tools.</summary>
public sealed class Spec182ParityToolsTests
{
    private static Domain.Sandbox.Sandbox FakeSandbox() => new(
        Guid.NewGuid(), Guid.Empty, null, "fake", SandboxStatus.Running,
        null, DateTimeOffset.UtcNow, null, null);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static ToolCallContext Ctx(
        IServiceProvider services, FakeSandboxExecutor? executor = null,
        FakeEventService? events = null, Guid? conversationId = null) => new(
        conversationId ?? Guid.NewGuid(), FakeSandbox(),
        executor ?? new FakeSandboxExecutor(), events ?? new FakeEventService(),
        services, TimeSpan.FromSeconds(30));

    private static ServiceProvider RegistryServices(Action<IServiceCollection>? configure = null)
    {
        var sc = new ServiceCollection()
            .AddSingleton<ITaskTrackerService, InMemoryTaskTrackerService>()
            .AddSingleton<IEventService, FakeEventService>()
            .AddSingleton(Options.Create(new AgentLlmOptions()))
            .AddSingleton<IToolRegistry, ToolRegistry>();
        configure?.Invoke(sc);
        return sc.BuildServiceProvider();
    }

    [Fact]
    public void Spec182_Tools_Are_Registered_Once_And_Deferred()
    {
        var registry = RegistryServices().GetRequiredService<IToolRegistry>();
        string[] names =
        [
            "agent_validate", "instant_context", "memory_learn", "reminder",
            "code_interpreter", "plan_exit", "mode_switch", "unseen_messages",
            "context_compact", "context_recovery", "extend_context", "context_usage",
            "browser_navigate", "browser_observe", "browser_act", "browser_screenshot",
            "browser_close", "generate_image", "generate_video",
            "shell_exec", "fetch_url", "todo_write",
        ];
        foreach (var name in names)
        {
            registry.Find(name).ShouldNotBeNull();
            registry.All.Count(t => t.Schema.Name == name).ShouldBe(1);
        }
        registry.Find("browser").ShouldNotBeNull();
        registry.Find("shell_exec")!.Risky.ShouldBeTrue();
        registry.Find("browser_observe")!.Risky.ShouldBeFalse();
        registry.Find("browser_screenshot")!.Risky.ShouldBeFalse();
        registry.Find("browser_close")!.Risky.ShouldBeFalse();
    }

    [Fact]
    public async Task ShellExec_Alias_Dispatches_To_Bash_Without_Duplicate_Name()
    {
        var executor = new FakeSandboxExecutor();
        var services = RegistryServices();
        var tool = services.GetRequiredService<IToolRegistry>().Find("shell_exec")!;
        var result = await tool.Handler(Ctx(services, executor),
            Args(new { command = "echo alias-ok" }), CancellationToken.None);
        result.IsError.ShouldBeFalse();
        executor.Commands.ShouldContain("echo alias-ok");
        result.Observation.GetProperty("exit_code").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task TodoWrite_Alias_Dispatches_To_TaskTracker()
    {
        var services = RegistryServices();
        var tool = services.GetRequiredService<IToolRegistry>().Find("todo_write")!;
        var result = await tool.Handler(Ctx(services), Args(new
        {
            items = new[] { new { title = "slice", status = "todo" } },
        }), CancellationToken.None);
        result.IsError.ShouldBeFalse();
        result.Observation.GetProperty("count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task AgentValidate_Autodetects_And_Returns_Test_And_Git_Evidence()
    {
        var executor = new FakeSandboxExecutor
        {
            ExecHandler = command =>
            {
                if (command.Contains("git status --porcelain", StringComparison.Ordinal))
                    return new CommandResult(0, "0\n", "", false);
                if (command.Contains("find . -maxdepth", StringComparison.Ordinal))
                    return new CommandResult(0, "dotnet test\n", "", false);
                if (command.Contains("dotnet test", StringComparison.Ordinal))
                    return new CommandResult(0, "passed\n", "", false);
                return new CommandResult(0, "ok\n", "", false);
            },
        };
        var tool = BuiltinParityTools.AgentValidate();
        var result = await tool.Handler(Ctx(new ServiceCollection().BuildServiceProvider(), executor),
            Args(new { }), CancellationToken.None);
        result.IsError.ShouldBeFalse();
        result.Observation.GetProperty("validated").GetBoolean().ShouldBeTrue();
        result.Observation.GetProperty("command").GetString()!.ShouldBe("dotnet test");
        result.Observation.GetProperty("detected").GetBoolean().ShouldBeTrue();
        result.Observation.GetProperty("git_clean").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task CodeInterpreter_Writes_Script_In_Workspace_And_Returns_Output()
    {
        var executor = new FakeSandboxExecutor
        {
            ExecHandler = command => command.Contains("python3", StringComparison.Ordinal)
                ? new CommandResult(0, "42\n", "", false)
                : new CommandResult(0, "ok\n", "", false),
        };
        var tool = BuiltinParityTools.CodeInterpreter();
        var result = await tool.Handler(Ctx(new ServiceCollection().BuildServiceProvider(), executor),
            Args(new { language = "python3", code = "print(42)" }), CancellationToken.None);
        result.IsError.ShouldBeFalse();
        result.Observation.GetProperty("exit_code").GetInt32().ShouldBe(0);
        result.Observation.GetProperty("stdout").GetString()!.ShouldContain("42");
    }

    [Fact]
    public async Task InstantContext_Rejects_Arbitrary_Commands_And_Returns_Xml()
    {
        var executor = new FakeSandboxExecutor
        {
            ExecHandler = _ => new CommandResult(0, "src/Foo.cs:12:Foo\n", "", false),
        };
        var tool = BuiltinParityTools.InstantContext();
        var result = await tool.Handler(Ctx(new ServiceCollection().BuildServiceProvider(), executor),
            Args(new { commands = new[] { "rg FooService", "rm -rf /" } }), CancellationToken.None);
        result.IsError.ShouldBeFalse();
        var xml = result.Observation.GetProperty("xml").GetString()!;
        xml.ShouldContain("<ANSWER>");
        xml.ShouldContain("src/Foo.cs");
        result.Observation.GetProperty("errors").GetArrayLength().ShouldBe(1);
        executor.Commands.ShouldNotContain(c => c.Contains("rm -rf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MemoryLearn_Uses_Project_Memory_Path()
    {
        var executor = new FakeSandboxExecutor();
        var tool = BuiltinParityTools.MemoryLearn();
        var result = await tool.Handler(Ctx(new ServiceCollection().BuildServiceProvider(), executor),
            Args(new { key = "validation first", text = "Run tests before delivery." }),
            CancellationToken.None);
        result.IsError.ShouldBeFalse();
        executor.Commands.ShouldContain(c => c.Contains(".openhands/memory", StringComparison.Ordinal));
    }

    private sealed class FakeBrowserService : IBrowserService
    {
        public BrowserActRequest? LastRequest { get; private set; }
        public bool Closed { get; private set; }

        public Task<BrowserResult> ActAsync(
            Guid conversationId, BrowserActRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new BrowserResult(
                true, "https://example.com", "Example", "aGk=", "visible text", null));
        }

        public Task CloseAsync(Guid conversationId, CancellationToken ct)
        {
            Closed = true;
            return Task.CompletedTask;
        }

        public Task<string?> CaptureStorageStateAsync(Guid conversationId, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task SetStorageStateAsync(Guid conversationId, string storageStateJson, CancellationToken ct) =>
            Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task BrowserGranularTools_Map_Actions_And_Return_Screenshot_Payload()
    {
        var browser = new FakeBrowserService();
        var services = new ServiceCollection()
            .AddSingleton<IBrowserService>(browser).BuildServiceProvider();
        var navigate = await BuiltinParityTools.BrowserNavigate().Handler(Ctx(services),
            Args(new { url = "https://example.com" }), CancellationToken.None);
        navigate.IsError.ShouldBeFalse();
        browser.LastRequest!.Action.ShouldBe(BrowserActionKind.Goto);
        navigate.Observation.GetProperty("screenshot_base64").GetString().ShouldBe("aGk=");

        await BuiltinParityTools.BrowserAct().Handler(Ctx(services),
            Args(new { action = "click", selector = "#go" }), CancellationToken.None);
        browser.LastRequest!.Action.ShouldBe(BrowserActionKind.Click);
        browser.LastRequest.Selector.ShouldBe("#go");

        var screenshot = await BuiltinParityTools.BrowserScreenshot().Handler(Ctx(services),
            Args(new { }), CancellationToken.None);
        screenshot.Observation.GetProperty("screenshot_base64").GetString().ShouldBe("aGk=");

        var close = await BuiltinParityTools.BrowserClose().Handler(Ctx(services),
            Args(new { }), CancellationToken.None);
        close.Observation.GetProperty("closed").GetBoolean().ShouldBeTrue();
        browser.Closed.ShouldBeTrue();
    }

    [Fact]
    public async Task BrowserAct_Rejects_Navigation_Or_Observation_Actions()
    {
        var services = new ServiceCollection()
            .AddSingleton<IBrowserService>(new FakeBrowserService()).BuildServiceProvider();
        var result = await BuiltinParityTools.BrowserAct().Handler(Ctx(services),
            Args(new { action = "goto", url = "https://example.com" }), CancellationToken.None);
        result.IsError.ShouldBeTrue();
        result.Observation.GetProperty("error").GetString()!.ShouldContain("unsupported");
    }

    private sealed class FakeCompactionService : ICompactionService
    {
        public bool Called { get; private set; }
        public string? Instructions { get; private set; }
        public Task<bool> CompactStoredAsync(
            Guid conversationId, CompactionTrigger trigger, string? instructions, CancellationToken ct)
        {
            Called = true;
            Instructions = instructions;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task ContextCompact_Calls_Compaction_Service_With_Manual_Trigger()
    {
        var compaction = new FakeCompactionService();
        var services = new ServiceCollection()
            .AddSingleton<ICompactionService>(compaction).BuildServiceProvider();
        var result = await BuiltinParityTools.ContextCompact().Handler(Ctx(services),
            Args(new { instructions = "preserve decisions" }), CancellationToken.None);
        result.IsError.ShouldBeFalse();
        result.Observation.GetProperty("compacted").GetBoolean().ShouldBeTrue();
        compaction.Called.ShouldBeTrue();
        compaction.Instructions.ShouldBe("preserve decisions");
    }

    [Fact]
    public async Task ContextRecovery_And_ContextUsage_Read_Event_Stream()
    {
        var events = new FakeEventService();
        var conversationId = Guid.NewGuid();
        await events.AppendAsync(conversationId, new CondensationSummaryEvent
        {
            Summary = "durable summary",
        }, CancellationToken.None);
        await events.AppendAsync(conversationId, new ConversationStateUpdateEvent
        {
            Key = "context_state",
            Value = JsonSerializer.SerializeToElement(new { tokens_estimate = 123, window = 1000, compactions = 1, model = "m" }),
        }, CancellationToken.None);
        var services = new ServiceCollection().BuildServiceProvider();
        var ctx = Ctx(services, events: events, conversationId: conversationId);

        var recovery = await BuiltinParityTools.ContextRecovery().Handler(ctx,
            Args(new { }), CancellationToken.None);
        recovery.Observation.GetProperty("summary").GetString().ShouldBe("durable summary");

        var usage = await BuiltinParityTools.ContextUsage().Handler(ctx,
            Args(new { }), CancellationToken.None);
        usage.Observation.GetProperty("tokens_estimate").GetInt32().ShouldBe(123);
    }

    [Fact]
    public async Task ExtendContext_Is_Bounded_Per_Conversation()
    {
        var budget = new ExtendContextBudget();
        var services = new ServiceCollection()
            .AddSingleton(budget).BuildServiceProvider();
        var ctx = Ctx(services);
        var first = await BuiltinParityTools.ExtendContext().Handler(ctx, Args(new { reason = "details" }), CancellationToken.None);
        var second = await BuiltinParityTools.ExtendContext().Handler(ctx, Args(new { }), CancellationToken.None);
        var third = await BuiltinParityTools.ExtendContext().Handler(ctx, Args(new { }), CancellationToken.None);
        first.Observation.GetProperty("granted").GetBoolean().ShouldBeTrue();
        second.Observation.GetProperty("granted").GetBoolean().ShouldBeTrue();
        third.Observation.GetProperty("status").GetString().ShouldBe("at_ceiling");
    }

    [Fact]
    public async Task Reminder_Is_Stored_And_UnseenMessages_Surfaces_Due_Reminders()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var sc = new ServiceCollection();
        sc.AddDbContext<AppDbContext>(o => o.UseSqlite(conn).UseSnakeCaseNamingConvention());
        var services = sc.BuildServiceProvider();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        var conversationId = Guid.NewGuid();
        var ctx = Ctx(scope.ServiceProvider, conversationId: conversationId);

        var reminder = await BuiltinParityTools.Reminder().Handler(ctx,
            Args(new { text = "check the build", delay_minutes = 1 }), CancellationToken.None);
        reminder.Observation.GetProperty("scheduled").GetBoolean().ShouldBeTrue();

        var row = await db.ConversationMemories.SingleAsync(
            m => m.ConversationId == conversationId && m.Key.StartsWith("reminder:"));
        row.Value = JsonSerializer.Serialize(new
        {
            text = "check the build",
            due = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        await ctx.Events.AppendAsync(conversationId, new MessageEvent
        {
            Source = EventSource.User,
            LlmMessage = JsonSerializer.SerializeToElement(new
            {
                role = "user",
                content = "please also check tests",
            }),
        }, CancellationToken.None);
        var unseen = await BuiltinParityTools.UnseenMessages().Handler(ctx,
            Args(new { }), CancellationToken.None);
        unseen.Observation.GetProperty("count").GetInt32().ShouldBe(2);
        unseen.Observation.GetProperty("text").GetString()!.ShouldContain("please also check tests");
        unseen.Observation.GetProperty("text").GetString()!.ShouldContain("check the build");
    }


    private sealed class StaticMediaHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [Fact]
    public async Task GenerateImage_ConfiguredProvider_Writes_Media_In_Workspace()
    {
        var b64 = Convert.ToBase64String("png-bytes"u8.ToArray());
        using var client = new HttpClient(new StaticMediaHandler(
            System.Net.HttpStatusCode.OK,
            $$"""{"data":[{"b64_json":"{{b64}}"}]}"""));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Media:ImageGenerationEnabled"] = "true",
            ["Media:ImageProvider"] = "openai-compatible",
            ["Media:ImageEndpoint"] = "http://media.test/images",
            ["Media:ImageModel"] = "test-image",
        }).Build();
        var executor = new FakeSandboxExecutor();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(config)
            .AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(client))
            .BuildServiceProvider();
        var result = await BuiltinParityTools.GenerateImage().Handler(Ctx(services, executor),
            Args(new { prompt = "diagram" }), CancellationToken.None);
        result.IsError.ShouldBeFalse();
        var path = result.Observation.GetProperty("path").GetString()!;
        path.ShouldStartWith("/workspace/repo/generated/image-");
        executor.BinaryFiles[path].ShouldBe("png-bytes"u8.ToArray());
    }

    [Fact]
    public async Task PlanExit_And_ModeSwitch_Fail_Explicitly_Without_Conversation_Services()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var plan = await BuiltinParityTools.PlanExit().Handler(Ctx(services), Args(new { }), CancellationToken.None);
        plan.IsError.ShouldBeTrue();
        var mode = await BuiltinParityTools.ModeSwitch().Handler(Ctx(services), Args(new { mode = "plan" }), CancellationToken.None);
        mode.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task MediaTools_Fail_Explicitly_When_Provider_Is_Not_Configured()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var image = await BuiltinParityTools.GenerateImage().Handler(Ctx(services),
            Args(new { prompt = "diagram" }), CancellationToken.None);
        image.IsError.ShouldBeTrue();
        image.Observation.GetProperty("error").GetString()!.ShouldContain("unavailable");
        var video = await BuiltinParityTools.GenerateVideo().Handler(Ctx(services),
            Args(new { prompt = "demo" }), CancellationToken.None);
        video.IsError.ShouldBeTrue();
        video.Observation.GetProperty("error").GetString()!.ShouldContain("unavailable");
    }
}