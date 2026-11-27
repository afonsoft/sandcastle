using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Conversations;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.UnitTests.Conversations;

/// <summary>SPEC-169 RF-003: state machine of versioned plan artifacts.</summary>
public sealed class Spec169PlanArtifactTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"sc-plan-{Guid.NewGuid():N}.db");
    private AppDbContext _db = null!;
    private PlanArtifactService _svc = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .UseSnakeCaseNamingConvention().Options);
        await _db.Database.MigrateAsync();
        _svc = new PlanArtifactService(_db);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Put_Increments_Version_And_Decisions_Persist_Comments()
    {
        var id = Guid.NewGuid();
        var plan = await _svc.PutAsync(id, new PutPlanArtifactRequest([
            new PlanArtifactItemInput("one", ["a.cs"], "why")]),
            CancellationToken.None);
        plan.Version.ShouldBe(1);
        var item = plan.Items.ShouldHaveSingleItem();
        item.Status.ShouldBe("pending");

        plan = (await _svc.DecideAsync(id, item.Id, "reject", "change it",
            CancellationToken.None))!;
        plan.Items[0].Status.ShouldBe("rejected");
        plan.Items[0].Comments.ShouldBe(["change it"]);

        plan = await _svc.PutAsync(id, new PutPlanArtifactRequest([
            new PlanArtifactItemInput("two", null, null)]), CancellationToken.None);
        plan.Version.ShouldBe(2);
        plan.Items[0].Title.ShouldBe("two");
    }

    [Fact]
    public async Task Execute_Requires_All_Approved_And_Is_Terminal()
    {
        var id = Guid.NewGuid();
        var plan = await _svc.PutAsync(id, new PutPlanArtifactRequest([
            new PlanArtifactItemInput("one", null, null)]),
            CancellationToken.None);
        var ex = await Should.ThrowAsync<PlanArtifactStateException>(
            () => _svc.ExecuteAsync(id, CancellationToken.None));
        ex.Code.ShouldBe("plan_not_ready");

        await _svc.DecideAsync(id, plan.Items[0].Id, "approve", null,
            CancellationToken.None);
        var executed = await _svc.ExecuteAsync(id, CancellationToken.None);
        executed!.ExecutedAt.ShouldNotBeNull();

        var terminal = await Should.ThrowAsync<PlanArtifactStateException>(
            () => _svc.ExecuteAsync(id, CancellationToken.None));
        terminal.Code.ShouldBe("plan_already_executed");
    }
}