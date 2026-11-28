using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Runtime;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Runtime;
using Sandcastle.Infrastructure.Sandbox;
using Sandcastle.Sandboxing;

using Shouldly;

namespace Sandcastle.UnitTests.Runtime;

public sealed class Spec184RuntimeControlTests
{
    private sealed class FakeEngine : ISandboxEngine
    {
        public List<EngineSandboxInfo> Pool = [];
        public bool IsRemote => false;
        public bool SupportsPause => true;
        public Task<EngineSandboxInfo> CreateAndStartAsync(EngineCreateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<EngineSandboxInfo?> InspectAsync(string containerId, CancellationToken ct) => Task.FromResult<EngineSandboxInfo?>(null);
        public Task<IReadOnlyList<EngineSandboxInfo>> ListManagedAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<EngineSandboxInfo>>([]);
        public Task<IReadOnlyList<EngineSandboxInfo>> ListPoolAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<EngineSandboxInfo>>(Pool);
        public Task<EngineWarmClaim?> ClaimWarmAsync(WarmClaimRequest request, CancellationToken ct) => Task.FromResult<EngineWarmClaim?>(null);
        public Task PauseAsync(string containerId, CancellationToken ct) => Task.CompletedTask;
        public Task UnpauseAsync(string containerId, CancellationToken ct) => Task.CompletedTask;
        public Task StartAsync(string containerId, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(string containerId, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveAsync(string containerId, CancellationToken ct, Guid? workspaceConversationId = null) => Task.CompletedTask;
    }

    private sealed class FakeSandboxes : ISandboxService
    {
        public readonly List<Guid> Deleted = [];
        public Task<Sandcastle.Domain.Sandbox.Sandbox> StartAsync(Guid specId, Guid? conversationId, CancellationToken ct) => throw new NotSupportedException();
        public Task<Sandcastle.Domain.Sandbox.Sandbox> PauseAsync(Guid sandboxId, CancellationToken ct) => throw new NotSupportedException();
        public Task<Sandcastle.Domain.Sandbox.Sandbox> StopAsync(Guid sandboxId, CancellationToken ct) => throw new NotSupportedException();
        public Task<Sandcastle.Domain.Sandbox.Sandbox> ResumeAsync(Guid sandboxId, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(Guid sandboxId, CancellationToken ct) { Deleted.Add(sandboxId); return Task.CompletedTask; }
        public Task<Sandcastle.Domain.Sandbox.Sandbox?> GetAsync(Guid sandboxId, CancellationToken ct) => Task.FromResult<Sandcastle.Domain.Sandbox.Sandbox?>(null);
        public Task<IReadOnlyList<Sandcastle.Domain.Sandbox.Sandbox>> SearchAsync(SandboxStatus? status, Guid? conversationId, CancellationToken ct) => Task.FromResult<IReadOnlyList<Sandcastle.Domain.Sandbox.Sandbox>>([]);
        public Task<SandboxSpec> UpsertSpecAsync(SandboxSpec spec, CancellationToken ct) => Task.FromResult(spec);
        public Task<SandboxSpec?> GetSpecAsync(Guid specId, CancellationToken ct) => Task.FromResult<SandboxSpec?>(null);
        public Task SweepOrphansAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static async Task<(SqliteConnection Conn, AppDbContext Db, RuntimeControlService Svc,
        IRuntimePoolOverrides Overrides, FakeSandboxes Sandboxes)> NewAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(conn).UseSnakeCaseNamingConvention().Options);
        await db.Database.EnsureCreatedAsync();
        var overrides = new RuntimePoolOverrides();
        var engine = new FakeEngine();
        engine.Pool.Add(new EngineSandboxInfo("pool-1", "running", null,
            new Dictionary<string, string> { [EngineLabels.PoolKey] = "agent:latest" }, null,
            DateTimeOffset.UtcNow.AddMinutes(-3)));
        var sandboxes = new FakeSandboxes();
        var svc = new RuntimeControlService(db, engine, sandboxes,
            Options.Create(new SandboxOptions { DefaultImage = "agent:latest", WarmPoolSize = 2 }), overrides);
        return (conn, db, svc, overrides, sandboxes);
    }

    [Fact]
    public async Task Fleet_Reports_Capacity_Pool_Alert_And_Image_Status()
    {
        var (conn, db, svc, _, _) = await NewAsync();
        var specId = Guid.NewGuid();
        db.SandboxSpecs.Add(new SandboxSpecRecord { Id = specId, Name = "agent", Image = "agent:latest", MemoryMb = 512, CpuLimit = 1.5, TimeoutMinutes = 30 });
        db.Sandboxes.Add(new SandboxRecord { Id = Guid.NewGuid(), SpecId = specId, Status = "running", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2), UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1), CreatedAtTicks = DateTimeOffset.UtcNow.AddMinutes(-2).Ticks, UpdatedAtTicks = DateTimeOffset.UtcNow.Ticks });
        await db.SaveChangesAsync();
        var fleet = await svc.GetFleetAsync(CancellationToken.None);
        fleet.Items.Single().Image.ShouldBe("agent:latest");
        fleet.Capacity.TotalSandboxes.ShouldBe(1);
        fleet.Capacity.Running.ShouldBe(1);
        fleet.Capacity.ReservedMemoryMb.ShouldBe(512);
        fleet.Alerts.ShouldContain(a => a.Contains("warm pool degraded"));
        var images = await svc.GetImagesAsync(CancellationToken.None);
        images.Single().Image.ShouldBe("agent:latest");
        images.Single().WarmAvailable.ShouldBe(1);
        conn.Dispose();
    }

    [Fact]
    public async Task Resize_Validates_Range_And_Overrides_Target()
    {
        var (conn, _, svc, overrides, _) = await NewAsync();
        (await svc.ResizePoolAsync(new RuntimePoolResizeRequest("agent:latest", -1), "admin", CancellationToken.None)).Changed.ShouldBeFalse();
        (await svc.ResizePoolAsync(new RuntimePoolResizeRequest("agent:latest", 3), "admin", CancellationToken.None)).Changed.ShouldBeTrue();
        overrides.Snapshot()["agent:latest"].ShouldBe(3);
        var pools = await svc.GetPoolsAsync(CancellationToken.None);
        pools.Single().Desired.ShouldBe(3);
        conn.Dispose();
    }

    [Fact]
    public async Task Terminate_And_Stale_Recycle_Use_Sandbox_Lifecycle()
    {
        var (conn, db, svc, _, sandboxes) = await NewAsync();
        var specId = Guid.NewGuid();
        db.SandboxSpecs.Add(new SandboxSpecRecord { Id = specId, Name = "agent", Image = "agent:latest", MemoryMb = 512, CpuLimit = 1, TimeoutMinutes = 30 });
        var sandboxId = Guid.NewGuid();
        db.Sandboxes.Add(new SandboxRecord { Id = sandboxId, SpecId = specId, Status = "created", CreatedAt = DateTimeOffset.UtcNow.AddHours(-1), UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1) });
        await db.SaveChangesAsync();
        (await svc.TerminateSandboxAsync(sandboxId, "admin", CancellationToken.None)).ShouldBeTrue();
        (await svc.RecycleSandboxAsync(sandboxId, "admin", CancellationToken.None)).ShouldBeTrue();
        sandboxes.Deleted.ShouldBe([sandboxId, sandboxId]);
        conn.Dispose();
    }
}