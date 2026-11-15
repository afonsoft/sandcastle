using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Outpost;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Outpost;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Sandbox;
using Sandcastle.Sandboxing;

namespace Sandcastle.UnitTests.Outpost;

/// <summary>
/// SPEC-153 RF-002: <see cref="OutpostSandboxService"/> — routing of the
/// sandbox lifecycle to a connected outpost (pin/policy), fallback para o
/// engine local e falha dura em <c>outpost-only</c> sem outpost online.
/// </summary>
public sealed class OutpostSandboxServiceTests
{
    private static readonly Guid OutpostId =
        Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static AppDbContext NewDb()
    {
        var o = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;
        var db = new AppDbContext(o);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    private static SandboxSpecRecord SeedSpec(AppDbContext db)
    {
        var spec = SandboxSpecRecord.From(new SandboxSpec(
            Guid.NewGuid(), "t", "runtime:latest", 512, 1.0, 30,
            new Dictionary<string, string> { ["A"] = "b" },
            MountWorkspace: false, PidsLimit: 256));
        db.SandboxSpecs.Add(spec);
        db.SaveChanges();
        return spec;
    }

    private static OutpostSandboxService NewSvc(
        AppDbContext db, OutpostRouteDecision decision,
        FakeOutpostDispatcher dispatch)
    {
        var inner = new SandboxService(
            db, new FakeEngine(),
            Options.Create(new SandboxOptions()),
            NullLogger<SandboxService>.Instance);
        return new OutpostSandboxService(
            inner, new FakeRouter(decision), dispatch, db,
            NullLogger<OutpostSandboxService>.Instance);
    }

    [Fact]
    public async Task OutpostOnlyWithoutOnlineOutpost_FailsHard()
    {
        var db = NewDb();
        var spec = SeedSpec(db);
        var dispatch = new FakeOutpostDispatcher();
        var svc = NewSvc(db,
            new OutpostRouteDecision(null, "policy:outpost-only", true),
            dispatch);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StartAsync(spec.Id, null, default));
        Assert.Contains("outpost", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(dispatch.Sent);
        Assert.Empty(db.Sandboxes);
    }

    [Fact]
    public async Task OutpostPicked_ProvisionsOverChannel()
    {
        var db = NewDb();
        var spec = SeedSpec(db);
        var dispatch = new FakeOutpostDispatcher
        {
            Next = new OutpostTaskEvent
            {
                Status = OutpostTaskStatus.Completed,
                Data = System.Text.Json.JsonDocument
                    .Parse("""{"container_id":"opc-123"}""").RootElement,
            },
        };
        var svc = NewSvc(db,
            new OutpostRouteDecision(OutpostId, "pin", true), dispatch);

        var sb = await svc.StartAsync(spec.Id, null, default);

        var (outpostId, payload) = Assert.Single(dispatch.Sent);
        Assert.Equal(OutpostId, outpostId);
        Assert.Equal(OutpostTaskOps.Provision, payload.Op);
        Assert.Equal("runtime:latest", payload.Image);
        Assert.Equal(512L * 1024 * 1024, payload.MemoryBytes);
        Assert.Equal("opc-123", sb.ContainerId);
        Assert.Equal(SandboxStatus.Running, sb.Status);
        Assert.Equal("outpost", sb.Pool);
        Assert.Contains(dispatch.Tracked,
            t => t.Item1 == OutpostId && t.Item2 == "opc-123");
    }

    [Fact]
    public async Task ProvisionFailure_MarksSandboxError()
    {
        var db = NewDb();
        var spec = SeedSpec(db);
        var dispatch = new FakeOutpostDispatcher
        {
            Next = new OutpostTaskEvent
            {
                Status = OutpostTaskStatus.Failed,
                Error = "boom",
            },
        };
        var svc = NewSvc(db,
            new OutpostRouteDecision(OutpostId, "pin", true), dispatch);

        var sb = await svc.StartAsync(spec.Id, null, default);

        Assert.Equal(SandboxStatus.Error, sb.Status);
        Assert.Equal("boom", sb.LastError);
        Assert.Empty(dispatch.Tracked);
    }

    [Fact]
    public async Task Pause_OnOutpostContainer_RoutesRemote()
    {
        var db = NewDb();
        var dispatch = new FakeOutpostDispatcher
        {
            Next = new OutpostTaskEvent { Status = OutpostTaskStatus.Completed },
        };
        var record = new SandboxRecord
        {
            Id = Guid.NewGuid(),
            SpecId = Guid.NewGuid(),
            ContainerId = "opc-9",
            Status = SandboxStatus.Running.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sandboxes.Add(record);
        db.SaveChanges();
        dispatch.TrackContainer(OutpostId, "opc-9");
        var svc = NewSvc(db, new OutpostRouteDecision(null, "default", false), dispatch);

        var sb = await svc.PauseAsync(record.Id, default);

        var (outpostId, payload) = Assert.Single(dispatch.Sent);
        Assert.Equal(OutpostTaskOps.Pause, payload.Op);
        Assert.Equal("opc-9", payload.ContainerId);
        Assert.Equal(SandboxStatus.Paused, sb.Status);
    }

    [Fact]
    public async Task Delete_OnOutpostContainer_DestroysAndUntracks()
    {
        var db = NewDb();
        var dispatch = new FakeOutpostDispatcher
        {
            Next = new OutpostTaskEvent { Status = OutpostTaskStatus.Completed },
        };
        var record = new SandboxRecord
        {
            Id = Guid.NewGuid(),
            SpecId = Guid.NewGuid(),
            ContainerId = "opc-10",
            Status = SandboxStatus.Running.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sandboxes.Add(record);
        db.SaveChanges();
        dispatch.TrackContainer(OutpostId, "opc-10");
        var svc = NewSvc(db, new OutpostRouteDecision(null, "default", false), dispatch);

        await svc.DeleteAsync(record.Id, default);

        var (_, payload) = Assert.Single(dispatch.Sent);
        Assert.Equal(OutpostTaskOps.Destroy, payload.Op);
        Assert.Contains("opc-10", dispatch.Untracked);
        Assert.Empty(db.Sandboxes);
    }

    private sealed class FakeRouter(OutpostRouteDecision decision) : IOutpostRouter
    {
        public Task<OutpostRouteDecision> ResolveAsync(
            Guid? conversationId, CancellationToken ct) =>
            Task.FromResult(decision);

        public Task<Guid?> PickOnlineAsync(
            Guid? orgId, CancellationToken ct) =>
            Task.FromResult<Guid?>(null);
    }

    private sealed class FakeOutpostDispatcher : IOutpostTaskDispatcher
    {
        public List<(Guid, OutpostTaskPayload)> Sent { get; } = [];
        public List<(Guid, string)> Tracked { get; } = [];
        public List<string> Untracked { get; } = [];

        public OutpostTaskEvent Next { get; init; } =
            new() { Status = OutpostTaskStatus.Completed };

        private readonly Dictionary<string, Guid> _owners = [];

        public bool IsOnline(Guid outpostId) => true;

        public IReadOnlyList<Guid> OnlineOutposts(Guid? orgId) => [];

        public Task<OutpostTaskEvent> SendTaskAsync(
            Guid outpostId, OutpostTaskPayload task,
            TimeSpan timeout, CancellationToken ct)
        {
            Sent.Add((outpostId, task));
            return Task.FromResult(Next);
        }

        public Task DrainAsync(Guid outpostId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task KillAndDisconnectAsync(
            Guid outpostId, CancellationToken ct) =>
            Task.CompletedTask;

        public Guid? ContainerOwner(string containerId) =>
            _owners.TryGetValue(containerId, out var o) ? o : null;

        public void TrackContainer(Guid outpostId, string containerId)
        {
            Tracked.Add((outpostId, containerId));
            _owners[containerId] = outpostId;
        }

        public void UntrackContainer(string containerId)
        {
            Untracked.Add(containerId);
            _owners.Remove(containerId);
        }

        public int RunningTasks(Guid outpostId) => 0;
    }

    private sealed class FakeEngine : ISandboxEngine
    {
        public bool IsRemote => false;
        public bool SupportsPause => true;

        public Task<EngineSandboxInfo> CreateAndStartAsync(
            EngineCreateRequest request, CancellationToken ct) =>
            Task.FromResult(new EngineSandboxInfo(
                "local-1", "running", null,
                new Dictionary<string, string>(), null));

        public Task<EngineSandboxInfo?> InspectAsync(
            string containerId, CancellationToken ct) =>
            Task.FromResult<EngineSandboxInfo?>(null);

        public Task<IReadOnlyList<EngineSandboxInfo>> ListManagedAsync(
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EngineSandboxInfo>>([]);

        public Task<IReadOnlyList<EngineSandboxInfo>> ListPoolAsync(
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EngineSandboxInfo>>([]);

        public Task<EngineWarmClaim?> ClaimWarmAsync(
            WarmClaimRequest request, CancellationToken ct) =>
            Task.FromResult<EngineWarmClaim?>(null);

        public Task PauseAsync(string containerId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task UnpauseAsync(string containerId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task StartAsync(string containerId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task StopAsync(string containerId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task RemoveAsync(
            string containerId, CancellationToken ct,
            Guid? workspaceConversationId = null) => Task.CompletedTask;
    }
}