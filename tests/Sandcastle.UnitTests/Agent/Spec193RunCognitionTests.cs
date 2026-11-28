using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Events;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Agent.Tools;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Knowledge;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.UnitTests.Agent;

/// <summary>SPEC-193: run cognition tools, phase ordering and advisor severity.</summary>
public sealed class Spec193RunCognitionTests
{
    [Fact]
    public void Registry_exposes_cognition_tools()
    {
        var services = new ServiceCollection()
            .AddSingleton<ITaskTrackerService, InMemoryTaskTrackerService>()
            .AddSingleton(Options.Create(new AgentLlmOptions()))
            .AddSingleton<IToolRegistry, ToolRegistry>()
            .BuildServiceProvider();
        var registry = services.GetRequiredService<IToolRegistry>();

        registry.Find("goal").ShouldNotBeNull().Risky.ShouldBeFalse();
        registry.Find("wait").ShouldNotBeNull().Risky.ShouldBeFalse();
        registry.Find("memory_reflect").ShouldNotBeNull().Risky.ShouldBeFalse();
    }

    [Theory]
    [InlineData("severity: blocker\nDo not proceed", "blocker")]
    [InlineData("severity: nit\nOK", "nit")]
    [InlineData("[concern] risky direction", "concern")]
    [InlineData("ordinary risk note", "concern")]
    [InlineData("There is an opportunity to simplify", "concern")]
    public void Advisor_severity_is_normalized(string input, string expected) =>
        AdvisorSeverityParser.Normalize(input).ShouldBe(expected);

    [Fact]
    public async Task Analyzing_is_emitted_exactly_once_before_generating()
    {
        var events = new FakeEventService();
        var tracker = new RunActivityTracker();
        var conversationId = Guid.NewGuid();

        await tracker.EnsureAnalyzingAsync(events, conversationId, CancellationToken.None);
        await tracker.NoteGeneratingAsync(events, conversationId, CancellationToken.None);
        await tracker.EnsureAnalyzingAsync(events, conversationId, CancellationToken.None);

        var phases = events.Events.OfType<RunProgressEvent>().Select(e => e.Phase).ToList();
        phases.Count(p => p == "analyzing").ShouldBe(1);
        phases.Count(p => p == "generating").ShouldBe(1);
        phases.IndexOf("analyzing").ShouldBeLessThan(phases.IndexOf("generating"));
    }

    [Fact]
    public void Run_goal_and_advisor_serialize_machine_readable_contract()
    {
        var goalJson = JsonSerializer.Serialize<EventBase>(new RunGoalEvent
        {
            Objective = "Ship parity",
            ToolCallId = "call-1",
        }, EventJson.Options);
        goalJson.ShouldContain("\"kind\":\"run_goal\"");
        goalJson.ShouldContain("\"objective\":\"Ship parity\"");
        goalJson.ShouldContain("\"tool_call_id\":\"call-1\"");

        var advisorJson = JsonSerializer.Serialize<EventBase>(new AdvisorEvent
        {
            Question = "q",
            Answer = "severity: blocker\nstop",
            Severity = "blocker",
        }, EventJson.Options);
        advisorJson.ShouldContain("\"kind\":\"advisor\"");
        advisorJson.ShouldContain("\"severity\":\"blocker\"");
    }

    [Fact]
    public async Task Goal_emits_durable_run_goal_event()
    {
        var events = new FakeEventService();
        var ctx = Context(events, new ServiceCollection().BuildServiceProvider());

        var result = await BuiltinCognitionTools.Goal().Handler(ctx,
            JsonSerializer.SerializeToElement(new { objective = "Implement SPEC-193" }),
            CancellationToken.None);

        result.IsError.ShouldBeFalse();
        var goal = events.Events.OfType<RunGoalEvent>().ShouldHaveSingleItem();
        goal.Objective.ShouldBe("Implement SPEC-193");
        goal.ToolCallId.ShouldBe(ctx.ToolCallId);
    }

    [Fact]
    public async Task Wait_is_bounded_and_emits_waiting_progress()
    {
        var events = new FakeEventService();
        var ctx = Context(events, new ServiceCollection().BuildServiceProvider());

        var result = await BuiltinCognitionTools.Wait().Handler(ctx,
            JsonSerializer.SerializeToElement(new { seconds = 0 }),
            CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Observation.GetProperty("waited_seconds").GetInt32().ShouldBe(1);
        events.Events.OfType<ToolProgressEvent>()
            .ShouldContain(e => e.Phase == "waiting");
    }

    [Fact]
    public async Task Memory_reflect_returns_bounded_digest_with_provenance()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn).UseSnakeCaseNamingConvention().Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var conversationId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString("N");
        db.Conversations.Add(new ConversationRecord
        {
            Id = conversationId,
            OrgId = orgId,
            CreatedByUserId = userId,
            ExecutionStatus = "ready",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.ConversationMemories.Add(new ConversationMemoryRecord
        {
            ConversationId = conversationId,
            Key = "decision",
            Value = "Use one canonical analyzing phase",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.MemoryRecords.Add(new MemoryRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Key = "phase-rule",
            Text = "canonical analyzing comes before generating",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var collectionId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        db.KnowledgeCollections.Add(new KnowledgeCollectionRecord
        {
            Id = collectionId,
            OrgId = orgId,
            OwnerUserId = userId,
            Name = "Runbook",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.KnowledgeDocuments.Add(new KnowledgeDocumentRecord
        {
            Id = documentId,
            CollectionId = collectionId,
            Title = "Lifecycle",
            SourceUri = "memory://lifecycle",
            ContentHash = "hash",
            ChunkCount = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.KnowledgeChunks.Add(new KnowledgeChunkRecord
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            Ordinal = 0,
            Text = "canonical lifecycle guidance",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var events = new FakeEventService();
        var ctx = Context(events, services, conversationId);

        var result = await BuiltinCognitionTools.MemoryReflect().Handler(ctx,
            JsonSerializer.SerializeToElement(new { query = "canonical", limit = 5 }),
            CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Observation.GetProperty("count").GetInt32().ShouldBe(3);
        var entry = result.Observation.GetProperty("entries")[0];
        entry.GetProperty("source").GetString().ShouldBe("conversation_memory");
        entry.GetProperty("provenance").GetString()!.ShouldContain("memory/decision");
        result.Observation.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("source").GetString())
            .ShouldBe(["conversation_memory", "org_memory", "knowledge_chunk"], ignoreOrder: true);
    }

    private static ToolCallContext Context(
        FakeEventService events, IServiceProvider services, Guid? conversationId = null) =>
        new(conversationId ?? Guid.NewGuid(),
            new Sandbox(Guid.NewGuid(), Guid.Empty, null, "fake", SandboxStatus.Running,
                null, DateTimeOffset.UtcNow, null, null),
            new FakeSandboxExecutor(), events, services,
            TimeSpan.FromSeconds(40));
}