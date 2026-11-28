using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Workflows;
using Sandcastle.Domain.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Workflows;

using Shouldly;

namespace Sandcastle.UnitTests.Workflows;

/// <summary>SPEC-183: factory grouping, typed workflow validation and hard-gated run orchestration.</summary>
public sealed class Spec183WorkflowTests
{
    private sealed class FakeConversations : IAppConversationService
    {
        public readonly Dictionary<Guid, ConversationExecutionStatus> Statuses = [];
        public int Created;
        public string? LastPrompt;

        public Task<AppConversationStartTask> CreateForUserAsync(
            Guid userId, Guid? orgId, CreateConversationRequest request,
            CancellationToken ct)
        {
            Created++;
            LastPrompt = request.InitialMessage!.Content[0].ToString();
            var id = Guid.NewGuid();
            Statuses[id] = ConversationExecutionStatus.Running;
            return Task.FromResult(new AppConversationStartTask(
                Guid.NewGuid(), StartTaskStatus.Working, id, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public Task<AppConversation?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<AppConversation?>(Statuses.TryGetValue(id, out var status)
                ? new AppConversation(id, "workflow", null, null, null, null,
                    status, [], false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                : null);

        public Task<AppConversationStartTask> CreateAsync(CreateConversationRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConversationPage> SearchAsync(ConversationQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> CountAsync(ConversationQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ArchiveAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UnarchiveAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConversationPage> ListArchivedAsync(ConversationQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> ArchivedCountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AppConversation>> GetBatchAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => throw new NotSupportedException();
        public Task<AppConversation?> UpdateAsync(Guid id, string? title, IReadOnlyList<string>? tags, bool? pinned, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<SendMessageError?> ValidateSendMessageAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<SwitchResult> SwitchProfileAsync(Guid id, string? profile, CancellationToken ct) => throw new NotSupportedException();
        public Task<SwitchResult> SwitchModelAsync(Guid id, string model, CancellationToken ct) => throw new NotSupportedException();
        public Task<AppConversation?> SetPermissionsAsync(Guid id, IReadOnlyList<AgentPermissionRule>? rules, CancellationToken ct) => throw new NotSupportedException();
        public Task<AppConversation?> SetModeAsync(Guid id, string mode, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<AttentionDismissResult> DismissAttentionAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<AppConversation?> UpdateAsync(Guid id, string? title, IReadOnlyList<string>? tags, bool? pinned, Guid? outpostId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static async Task<(SqliteConnection Conn, AppDbContext Db, WorkflowService Workflows,
        FactoryService Factories, FakeConversations Conversations)> NewAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn).UseSnakeCaseNamingConvention().Options);
        await db.Database.EnsureCreatedAsync();
        var conversations = new FakeConversations();
        return (conn, db, new WorkflowService(db, conversations),
            new FactoryService(db), conversations);
    }

    [Fact]
    public async Task Factory_Crud_Groups_And_Delete_Unassigns_Members()
    {
        var (conn, db, workflows, factories, _) = await NewAsync();
        await using var _ = conn;
        var owner = Guid.NewGuid();
        var (factory, error) = await factories.CreateAsync(
            new FactoryUpsert("Platform", "delivery"), owner, null, CancellationToken.None);
        error.ShouldBeNull();
        var (workflow, workflowError) = await workflows.CreateAsync(new WorkflowUpsert(
            "Ship", null, factory!.Id, null, true), owner, null, CancellationToken.None);
        workflowError.ShouldBeNull();
        var counts = await factories.GetAsync(factory.Id, owner, CancellationToken.None);
        counts!.WorkflowCount.ShouldBe(1);

        (await factories.DeleteAsync(factory.Id, owner, CancellationToken.None)).ShouldBeTrue();
        (await db.Workflows.AsNoTracking().SingleAsync()).FactoryId.ShouldBeNull();
        (await factories.ListAsync(owner, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Workflow_Validation_Rejects_Invalid_Definition_And_Typed_Input_Error()
    {
        var (conn, _, workflows, _, _) = await NewAsync();
        await using var _ = conn;
        var owner = Guid.NewGuid();
        var bad = await workflows.CreateAsync(new WorkflowUpsert(
            "bad", null, null, "{\"steps\":[{\"name\":\"x\",\"type\":\"wat\"}]}", false),
            owner, null, CancellationToken.None);
        bad.Error.ShouldBe("invalid workflow definition");

        const string definition = """
        {"inputs":[{"name":"repository","type":"string","required":true}],
         "steps":[{"name":"approve","type":"approval"}]}
        """;
        var created = await workflows.CreateAsync(new WorkflowUpsert(
            "typed", null, null, definition, false), owner, null, CancellationToken.None);
        created.Error.ShouldBeNull();
        var started = await workflows.StartRunAsync(created.Info!.Id,
            new WorkflowRunStart(new Dictionary<string, JsonElement>
            {
                ["repository"] = JsonSerializer.SerializeToElement(42),
            }), owner, CancellationToken.None);
        started.Error.ShouldBe("input 'repository' must be string");
    }

    [Fact]
    public async Task Workflow_Approval_Gate_Then_Agent_Step_Advances_To_Success()
    {
        var (conn, _, workflows, _, conversations) = await NewAsync();
        await using var _ = conn;
        var owner = Guid.NewGuid();
        const string definition = """
        {"inputs":[{"name":"repository","type":"string","required":true}],
         "steps":[
          {"name":"human_gate","type":"approval","prompt":"approve"},
          {"name":"deliver","type":"agent","prompt":"deliver {{repository}}"}],
         "retry":{"max_attempts":2,"max_steps":8}}
        """;
        var created = await workflows.CreateAsync(new WorkflowUpsert(
            "delivery", null, null, definition, false), owner, null, CancellationToken.None);
        var inputs = new Dictionary<string, JsonElement>
        {
            ["repository"] = JsonSerializer.SerializeToElement("owner/repo"),
        };
        var started = await workflows.StartRunAsync(created.Info!.Id,
            new WorkflowRunStart(inputs), owner, CancellationToken.None);
        started.Error.ShouldBeNull();
        started.Run!.Status.ShouldBe("waiting_approval");
        conversations.Created.ShouldBe(0);

        var approved = await workflows.ApproveRunAsync(started.Run.Id,
            new WorkflowApprovalRequest(true, "go"), owner, CancellationToken.None);
        approved.Error.ShouldBeNull();
        approved.Run!.Status.ShouldBe("waiting_agent");
        conversations.Created.ShouldBe(1);
        conversations.LastPrompt!.ShouldContain("deliver owner/repo");
        conversations.LastPrompt!.ShouldContain("do not merge");

        conversations.Statuses[approved.Run.ConversationId!.Value] = ConversationExecutionStatus.Finished;
        var refreshed = await workflows.GetRunAsync(started.Run.Id, owner, CancellationToken.None);
        refreshed!.Status.ShouldBe("succeeded");
        refreshed.Audit.GetArrayLength().ShouldBeGreaterThanOrEqualTo(4);

        var duplicate = await workflows.StartRunAsync(created.Info.Id,
            new WorkflowRunStart(inputs), owner, CancellationToken.None);
        duplicate.Error.ShouldBeNull(); // terminal anterior não bloqueia novo run
    }

    [Fact]
    public async Task Workflow_Active_Run_Is_Rejected_And_Rejection_Is_Audited()
    {
        var (conn, _, workflows, _, _) = await NewAsync();
        await using var _ = conn;
        var owner = Guid.NewGuid();
        const string definition = """
        {"steps":[{"name":"human_gate","type":"approval"}]}
        """;
        var created = await workflows.CreateAsync(new WorkflowUpsert(
            "delivery", null, null, definition, false), owner, null, CancellationToken.None);
        var started = await workflows.StartRunAsync(created.Info!.Id,
            new WorkflowRunStart(null), owner, CancellationToken.None);
        var duplicate = await workflows.StartRunAsync(created.Info.Id,
            new WorkflowRunStart(null), owner, CancellationToken.None);
        duplicate.Error.ShouldBe("workflow run already in progress");
        var rejected = await workflows.ApproveRunAsync(started.Run!.Id,
            new WorkflowApprovalRequest(false, "stop"), owner, CancellationToken.None);
        rejected.Run!.Status.ShouldBe("rejected");
        rejected.Run.Audit.ToString().ShouldContain("approval_rejected");
    }

    [Fact]
    public async Task Autonomous_Default_Contains_Hard_Gates_And_Templates_Are_Recommended()
    {
        var (conn, _, workflows, _, _) = await NewAsync();
        await using var _ = conn;
        var owner = Guid.NewGuid();
        var created = await workflows.CreateAsync(new WorkflowUpsert(
            "autonomous", null, null, null, true), owner, null, CancellationToken.None);
        created.Error.ShouldBeNull();
        var run = await workflows.StartRunAsync(created.Info!.Id,
            new WorkflowRunStart(new Dictionary<string, JsonElement>
            {
                ["repository"] = JsonSerializer.SerializeToElement("owner/repo"),
                ["task"] = JsonSerializer.SerializeToElement("fix flaky test"),
            }), owner, CancellationToken.None);
        run.Error.ShouldBeNull();
        run.Run!.Status.ShouldBe("waiting_agent");
        var templates = workflows.ListTemplates();
        templates.ShouldContain(t => t.Id == "autonomous-delivery");
        templates.Count.ShouldBeGreaterThanOrEqualTo(4);
    }
}