using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Events;
using Sandcastle.Application.Sandbox;
using Sandcastle.Application.Scm;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Agent.Tools;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Scm;

using Shouldly;

namespace Sandcastle.UnitTests.Agent;

/// <summary>SPEC-191: agent GitHub PR tools — scoping, bounds, redaction and risk.</summary>
public sealed class Spec191GitHubPrToolsTests
{
    [Fact]
    public void Registry_exposes_tools_and_comment_is_risky() // RF-004/RF-005
    {
        var services = new ServiceCollection()
            .AddSingleton<ITaskTrackerService, InMemoryTaskTrackerService>()
            .AddSingleton(Options.Create(new AgentLlmOptions()))
            .AddSingleton<IToolRegistry, ToolRegistry>()
            .BuildServiceProvider();
        var registry = services.GetRequiredService<IToolRegistry>();

        registry.Find("github_list_pull_requests")!.Risky.ShouldBeFalse();
        registry.Find("github_view_pull_request")!.Risky.ShouldBeFalse();
        registry.Find("github_read_pull_request_diff")!.Risky.ShouldBeFalse();
        registry.Find("github_comment_pull_request")!.Risky.ShouldBeTrue();
    }

    [Fact]
    public async Task List_scopes_to_selected_repository_and_redacts_output() // RF-001/RF-006
    {
        await using var f = new Fixture();
        f.Provider.ListItems =
        [
            new ScmPullRequestListItem("https://github.test/pr/1", 1, "open",
                "fix sk-abcdef1234567890abcdefgh", "octo", false, "feature", "main",
                DateTimeOffset.UtcNow),
        ];

        var result = await InvokeAsync(f, BuiltinGitHubPrTools.ListPullRequests(),
            new { limit = 10 });

        result.IsError.ShouldBeFalse();
        result.Observation.GetProperty("repository").GetString().ShouldBe("owner/repo");
        var text = result.Observation.GetRawText();
        text.ShouldNotContain("sk-abcdef1234567890abcdefgh");
        f.Provider.LastRepository.ShouldBe("owner/repo");
    }

    [Fact]
    public async Task View_returns_bounded_details_with_checks_and_reviews() // RF-002
    {
        await using var f = new Fixture();
        f.Provider.Details = new ScmPullRequestDetails(
            "https://github.test/pr/7", 7, "open", false, "title", "octo",
            false, "sha", "feature", "main", "body excerpt", 3, 10, 2,
            4, 2, 1, 1, 1, 1, DateTimeOffset.UtcNow);

        var result = await InvokeAsync(f, BuiltinGitHubPrTools.ViewPullRequest(),
            new { number = 7 });

        result.IsError.ShouldBeFalse();
        result.Observation.GetProperty("body_excerpt").GetString().ShouldBe("body excerpt");
        result.Observation.GetProperty("checks").GetProperty("total").GetInt32().ShouldBe(4);
        result.Observation.GetProperty("reviews").GetProperty("approved").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Diff_filters_paths_marks_binary_and_truncates_patch() // RF-003/RF-006
    {
        await using var f = new Fixture();
        var longPatch = string.Concat(Enumerable.Repeat("+line\n", 500));
        f.Provider.Files =
        [
            new ScmPullRequestFile("src/a.cs", null, "modified", 500, 0, longPatch),
            new ScmPullRequestFile("assets/logo.png", null, "added", 0, 0, null),
            new ScmPullRequestFile("docs/readme.md", null, "modified", 1, 1, "+x"),
        ];

        var result = await InvokeAsync(f, BuiltinGitHubPrTools.ReadPullRequestDiff(),
            new { number = 9, path_filter = "src", max_patch_chars = 200 });

        result.IsError.ShouldBeFalse();
        result.Observation.GetProperty("files_matched").GetInt32().ShouldBe(1);
        var file = result.Observation.GetProperty("files")[0];
        file.GetProperty("patch_truncated").GetBoolean().ShouldBeTrue();
        file.GetProperty("patch").GetString()!.Length.ShouldBeLessThan(260);

        var binary = await InvokeAsync(f, BuiltinGitHubPrTools.ReadPullRequestDiff(),
            new { number = 9, path_filter = "*.png" });
        binary.Observation.GetProperty("files")[0]
            .GetProperty("binary").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Repository_outside_conversation_selection_is_rejected() // RF-006
    {
        await using var f = new Fixture();

        var result = await InvokeAsync(f, BuiltinGitHubPrTools.ViewPullRequest(),
            new { number = 7, repository = "other/repo" });

        result.IsError.ShouldBeTrue();
        result.Observation.GetProperty("error").GetString().ShouldBe("repository_not_selected");
    }

    [Fact]
    public async Task Comment_redacts_body_and_returns_idempotency_metadata() // RF-004/RF-006
    {
        await using var f = new Fixture();

        var result = await InvokeAsync(f, BuiltinGitHubPrTools.CommentPullRequest(),
            new { number = 7, body = "token sk-abcdef1234567890abcdefgh", idempotency_key = "k-1" });

        result.IsError.ShouldBeFalse();
        result.Observation.GetProperty("comment_id").GetInt64().ShouldBe(44);
        result.Observation.GetProperty("idempotency_key").GetString().ShouldBe("k-1");
        f.Provider.LastCommentBody.ShouldNotBeNull();
        f.Provider.LastCommentBody.ShouldNotContain("sk-abcdef1234567890abcdefgh");
    }

    private static async Task<ToolResult> InvokeAsync(
        Fixture f, AgentTool tool, object args)
    {
        var services = new ServiceCollection()
            .AddSingleton(f.Service)
            .BuildServiceProvider();
        var ctx = new ToolCallContext(f.ConversationId,
            new Sandbox(Guid.NewGuid(), Guid.Empty, null, "fake", SandboxStatus.Running,
                null, DateTimeOffset.UtcNow, null, null),
            new FakeSandboxExecutor(), new FakeEventService(), services,
            TimeSpan.FromSeconds(10));
        return await tool.Handler(ctx,
            JsonSerializer.SerializeToElement(args), CancellationToken.None);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _conn;
        public Guid ConversationId { get; } = Guid.NewGuid();
        public FakeProvider Provider { get; } = new();
        public ScmAgentPullRequestService Service { get; }

        public Fixture()
        {
            _conn = new SqliteConnection("Data Source=:memory:");
            _conn.Open();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options;
            var db = new AppDbContext(options);
            db.Database.EnsureCreated();
            db.Conversations.Add(new ConversationRecord
            {
                Id = ConversationId,
                OrgId = Guid.NewGuid(),
                SelectedRepository = "owner/repo",
                ExecutionStatus = "ready",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
            var connection = new ScmConnection(Guid.NewGuid(), null,
                ScmKind.GitHub, null, null, null, ["owner/repo"],
                null, null, null, DateTimeOffset.UtcNow);
            Service = new ScmAgentPullRequestService(db,
                new FakeConnectionService(connection),
                new FakeRegistry(Provider));
        }

        public async ValueTask DisposeAsync()
        {
            await _conn.DisposeAsync();
        }
    }

    private sealed class FakeConnectionService(ScmConnection connection) : IScmConnectionService
    {
        public Task<ScmConnection?> FindForRepositoryAsync(
            Guid orgId, ScmKind kind, string repository, CancellationToken ct) =>
            Task.FromResult<ScmConnection?>(repository == "owner/repo" ? connection : null);
        public object BuildGitHubAppManifest(string redirectUri, string appName) => throw new NotSupportedException();
        public Task<ScmConnection?> CompleteGitHubManifestFlowAsync(Guid orgId, string code, string? createdByUserId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ScmConnection> CreateAsync(Guid orgId, ScmKind kind, string? installationId, string? pemSecretRef, string? webhookSecretRef, IReadOnlyList<string>? repos, string? appId, string? appSlug, string? createdByUserId, CancellationToken ct, string? apiUrl = null, string? authKind = null, string? metaJson = null) => throw new NotSupportedException();
        public Task<ScmConnectionDiagnostic> TestAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ScmConnection>> ListAsync(Guid orgId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ScmConnection?> GetAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ScmConnection?> UpdateReviewSettingsAsync(Guid orgId, Guid id, ScmConnectionReviewUpdate update, CancellationToken ct) => throw new NotSupportedException();
        public Task<ScmConnection?> UpdateAutofixSettingsAsync(Guid orgId, Guid id, ScmConnectionAutofixUpdate update, CancellationToken ct) => throw new NotSupportedException();
        public Task<ScmConnection?> UpdateQaSettingsAsync(Guid orgId, Guid id, ScmConnectionQaUpdate update, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid orgId, Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeRegistry(IScmProvider provider) : IScmProviderRegistry
    {
        public IScmProvider? Get(ScmKind kind) => provider;
    }

    private sealed class FakeProvider : IScmProvider
    {
        public ScmKind Kind => ScmKind.GitHub;
        public IReadOnlyList<ScmPullRequestListItem> ListItems { get; set; } = [];
        public ScmPullRequestDetails? Details { get; set; }
        public IReadOnlyList<ScmPullRequestFile> Files { get; set; } = [];
        public string? LastRepository { get; private set; }
        public string? LastCommentBody { get; private set; }

        public Task<IReadOnlyList<ScmPullRequestListItem>> ListPullRequestsAsync(
            ScmConnection connection, string repository, string state, int limit, CancellationToken ct)
        {
            LastRepository = repository;
            return Task.FromResult(ListItems);
        }

        public Task<ScmPullRequestDetails?> GetPullRequestDetailsAsync(
            ScmConnection connection, string repository, int number, CancellationToken ct) =>
            Task.FromResult(Details);

        public Task<IReadOnlyList<ScmPullRequestFile>> GetPullRequestFilesAsync(
            ScmConnection connection, string repository, int pullRequestNumber, CancellationToken ct) =>
            Task.FromResult(Files);

        public Task<long> CreateIssueCommentAsync(
            ScmConnection connection, string repository, int issueNumber,
            string body, CancellationToken ct)
        {
            LastCommentBody = body;
            return Task.FromResult(44L);
        }

        public Task<ScmPullRequest> CreatePullRequestAsync(ScmConnection connection, string repository, ScmPrCreateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ScmPullRequest?> GetPullRequestAsync(ScmConnection connection, string repository, int number, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> ReplyToReviewCommentAsync(ScmConnection connection, string repository, int pullRequestNumber, long inReplyToCommentId, string body, CancellationToken ct) => throw new NotSupportedException();
        public Task SetCommitStatusAsync(ScmConnection connection, string repository, string sha, string state, string context, string? targetUrl, string? description, CancellationToken ct) => throw new NotSupportedException();
        public Task<ScmConnectionDiagnostic> TestConnectionAsync(ScmConnection connection, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetFileContentAsync(ScmConnection connection, string repository, string path, string? reference, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListDirectoryAsync(ScmConnection connection, string repository, string directoryPath, string? reference, CancellationToken ct) => throw new NotSupportedException();
        public Task<ScmReviewResult?> CreateReviewAsync(ScmConnection connection, string repository, int pullRequestNumber, string headSha, string body, IReadOnlyList<ScmReviewComment> comments, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ScmCheckRun>> GetCheckRunsAsync(ScmConnection connection, string repository, string reference, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetCheckRunLogsAsync(ScmConnection connection, string repository, long checkRunId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetWorkflowRunLogsAsync(ScmConnection connection, string repository, long workflowRunId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ScmCommitPullRequest>> GetPullRequestsForCommitAsync(ScmConnection connection, string repository, string sha, CancellationToken ct) => throw new NotSupportedException();
    }
}