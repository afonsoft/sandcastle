using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Identity;
using Sandcastle.Application.Observability;
using Sandcastle.Domain.Identity;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Eventing;
using Sandcastle.Infrastructure.Identity;
using Sandcastle.Infrastructure.Observability;
using Sandcastle.Infrastructure.Persistence;

using Shouldly;

namespace Sandcastle.UnitTests.Observability;

public sealed class Spec185ObservabilityTests
{
    private sealed class FakePlans : IPlanEnforcementService
    {
        public Task<EffectivePlan> GetEffectivePlanAsync(Guid? orgId, CancellationToken cancellationToken) =>
            Task.FromResult(new EffectivePlan(null, "test", new PlanLimits(MaxTokensPerMonth: 100)));
        public Task<EffectivePlan> GetEffectiveLimitsAsync(Guid? orgId, Guid? userId, CancellationToken cancellationToken) => GetEffectivePlanAsync(orgId, cancellationToken);
        public Task<PlanLimitError?> CheckConversationCreateAsync(Guid? orgId, Guid? userId, CancellationToken cancellationToken) => Task.FromResult<PlanLimitError?>(null);
        public Task<PlanLimitError?> CheckRunStartAsync(Guid? orgId, Guid? userId, CancellationToken cancellationToken) => Task.FromResult<PlanLimitError?>(null);
    }

    private static async Task<(SqliteConnection Conn, AppDbContext Db, ObservabilityAnalyticsService Svc, Guid ConversationId, Guid UserId)> NewAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).UseSnakeCaseNamingConvention().Options);
        await db.Database.EnsureCreatedAsync();
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        db.Conversations.Add(new ConversationRecord
        {
            Id = conversationId,
            Title = "run",
            CreatedByUserId = userId.ToString(),
            ExecutionStatus = "Running",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Events.Add(new EventRecord
        {
            Id = "e1",
            ConversationId = conversationId,
            Kind = "TokenEvent",
            Source = "agent",
            TimestampTicks = DateTimeOffset.UtcNow.UtcTicks,
            Payload = """{"kind":"TokenEvent","model":"gpt-5","prompt_tokens":10,"completion_tokens":5,"cost_usd":0.01,"latency_ms":120}""",
        });
        db.Events.Add(new EventRecord
        {
            Id = "e2",
            ConversationId = conversationId,
            Kind = "ActionEvent",
            Source = "agent",
            TimestampTicks = DateTimeOffset.UtcNow.UtcTicks,
            Payload = """{"kind":"ActionEvent","tool_name":"shell","tool_call_id":"call1"}""",
        });
        await db.SaveChangesAsync();
        var svc = new ObservabilityAnalyticsService(db, new FakePlans(),
            Options.Create(new ObservabilityOptions()), Options.Create(new BillingOptions { EnforcementEnabled = true }));
        return (conn, db, svc, conversationId, userId);
    }

    [Fact]
    public async Task Analytics_Aggregates_Tokens_Cost_Latency_Runs_And_Csv()
    {
        var (conn, _, svc, _, _) = await NewAsync();
        var analytics = await svc.GetAnalyticsAsync(new AnalyticsQuery(30), CancellationToken.None);
        analytics.TotalTokens.ShouldBe(15);
        analytics.TotalCostUsd.ShouldBe(0.01m);
        analytics.TotalRuns.ShouldBe(1);
        analytics.Models.Single().AverageLatencyMs.ShouldBe(120);
        var csv = await svc.ExportCsvAsync(new AnalyticsQuery(30), CancellationToken.None);
        csv.ShouldContain("tokens,cost_usd");
        conn.Dispose();
    }

    [Fact]
    public async Task RunTrace_Returns_Waterfall_Only_For_Owner_Or_Admin()
    {
        var (conn, _, svc, conversationId, userId) = await NewAsync();
        var trace = await svc.GetRunTraceAsync(conversationId, userId, false, CancellationToken.None);
        trace.ShouldNotBeNull();
        trace.TotalTokens.ShouldBe(15);
        trace.LlmCalls.ShouldBe(1);
        trace.ToolCalls.ShouldBe(1);
        (await svc.GetRunTraceAsync(conversationId, Guid.NewGuid(), false, CancellationToken.None)).ShouldBeNull();
        conn.Dispose();
    }

    [Fact]
    public async Task Budgets_Report_Warning_And_Reconcile_Counts()
    {
        var (conn, db, svc, _, _) = await NewAsync();
        var orgId = Guid.NewGuid();
        db.Organizations.Add(new OrganizationRecord { Id = orgId, Slug = "org", Name = "Org" });
        db.UsageRecords.Add(new UsageRecord { Id = Guid.NewGuid(), OrgId = orgId, Period = DateTimeOffset.UtcNow.ToString("yyyy-MM"), TokensUsed = 90, CostUsd = 2m, RecordedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var rows = await svc.GetBudgetsAsync(CancellationToken.None);
        rows.Single(r => r.OrgId == orgId).Level.ShouldBe("warning");
        var result = await svc.ReconcileBudgetsAsync("admin", false, CancellationToken.None);
        result.Warning.ShouldBe(1);
        conn.Dispose();
    }
}