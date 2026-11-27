using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Sandcastle.Domain.Conversations;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Insights;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Insights;

public class SessionInsightServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDbContext _db;
    private readonly SessionInsightService _service;

    public SessionInsightServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sc-insights-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        _db.Database.EnsureCreated();

        _service = new SessionInsightService(_db, NullLogger<SessionInsightService>.Instance, null!);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task AnalyzeSessionAsync_Generates_Insight_For_Completed_Conversation()
    {
        var convId = Guid.NewGuid();
        var conv = new ConversationRecord
        {
            Id = convId,
            Title = "Implement feature X",
            ExecutionStatus = "COMPLETED",
            LlmModel = "claude-3-7-sonnet",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _db.Conversations.AddAsync(conv);
        await _db.SaveChangesAsync();

        var insight = await _service.AnalyzeSessionAsync(convId);

        insight.ShouldNotBeNull();
        insight.ConversationId.ShouldBe(convId);
        insight.Outcome.ShouldBe("success");
        insight.PhaseCosts.ShouldNotBeEmpty();
        insight.Learnings.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetStatsAsync_Aggregates_Session_Metrics()
    {
        var convId = Guid.NewGuid();
        var conv = new ConversationRecord
        {
            Id = convId,
            Title = "Task with error",
            ExecutionStatus = "FAILED",
            LlmModel = "gpt-4o",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _db.Conversations.AddAsync(conv);
        await _db.SaveChangesAsync();

        await _service.AnalyzeSessionAsync(convId);
        var stats = await _service.GetStatsAsync();

        stats.TotalSessionsAnalyzed.ShouldBe(1);
        stats.FailedCount.ShouldBe(1);
        stats.SuccessRate.ShouldBe(0.0);
    }
    [Fact]
    public async Task AnalyzeSessionAsync_ComProviderFake_Persiste_L2_E_Stats()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sc-insights166-{Guid.NewGuid():N}.db");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").UseSnakeCaseNamingConvention().Options);
        await db.Database.MigrateAsync();
        var convId = Guid.NewGuid();
        db.Conversations.Add(new ConversationRecord
        {
            Id = convId,
            Title = "Implement insights",
            ExecutionStatus = "COMPLETED",
            LlmModel = "fake",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var llm = new StaticLlmProvider("""{"category":"feature","size_tier":"S","summary":" concise summary ","action_items":["add coverage"],"timeline":{"analysis":"one pass"}}""");
        var service = new SessionInsightService(db,
            NullLogger<SessionInsightService>.Instance, null!, llm);

        var insight = await service.AnalyzeSessionAsync(convId, force: true);
        insight.Category.ShouldBe("feature");
        insight.SizeTier.ShouldBe("S");
        insight.ActionItems!.ShouldContain("add coverage");
        insight.LlmSummary.ShouldBe("concise summary");
        insight.LlmAnalysisFailed.ShouldBeFalse();

        var stats = await service.GetStatsAsync();
        stats.L2CoverageRate.ShouldBe(1.0);
        stats.SizeDistribution!.ShouldContainKey("S");
        stats.Categories!.ShouldContainKey("feature");
        File.Delete(dbPath);
    }

    [Fact]
    public async Task AnalyzeSessionAsync_ForceSemProvider_Falha_Sem_Sobrescrever()
    {
        var convId = Guid.NewGuid();
        _db.Conversations.Add(new ConversationRecord
        {
            Id = convId,
            Title = "No provider",
            ExecutionStatus = "COMPLETED",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        await Should.ThrowAsync<Sandcastle.Infrastructure.Insights.InsightLlmAnalysisException>(
            () => _service.AnalyzeSessionAsync(convId, force: true));
    }

    private sealed class StaticLlmProvider(string response)
        : Sandcastle.Application.Agent.IAgentLlmProvider
    {
        public string ModelId => "fake";
        public Task<Sandcastle.Application.Agent.ModelInfo> GetModelInfoAsync(CancellationToken ct) =>
            Task.FromResult(new Sandcastle.Application.Agent.ModelInfo(ModelId, "fake"));

        public async IAsyncEnumerable<Sandcastle.Application.Agent.ILlmStreamChunk> ChatStreamAsync(
            IReadOnlyList<Sandcastle.Application.Agent.LlmMessage> messages,
            IReadOnlyList<Sandcastle.Application.Agent.ToolSchema> tools,
            Sandcastle.Application.Agent.LlmOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new Sandcastle.Application.Agent.TextDelta(response);
            await Task.CompletedTask;
        }
    }

}
public sealed class Spec166SessionInsightsV2Tests
{
    [Fact]
    public void ParseL2_Aceita_Json_Valido_E_Rejeita_Invalido()
    {
        var parsed = SessionInsightService.ParseL2(
            """{"category":"feature","size_tier":"M","summary":"Implemented safely.","action_items":["Add tests"],"timeline":{"implementation":"core flow"}}""");
        parsed.Category.ShouldBe("feature");
        parsed.SizeTier.ShouldBe("M");
        parsed.ActionItems.ShouldContain("Add tests");
        parsed.Timeline.ShouldContainKey("implementation");

        Should.Throw<Sandcastle.Infrastructure.Insights.InsightLlmAnalysisException>(
            () => SessionInsightService.ParseL2("not json"));
    }
}
