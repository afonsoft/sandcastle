using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Events;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Conversations;
using Sandcastle.Domain.Events;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Sandbox;
using Sandcastle.Sandboxing;
using Sandcastle.UnitTests.Agent;

using Shouldly;

using Xunit;

using DomainSandbox = Sandcastle.Domain.Sandbox.Sandbox;

namespace Sandcastle.UnitTests.Conversations;

/// <summary>
/// SPEC-154: <see cref="ConversationSandboxGuard"/> — sandbox Missing/Error
/// reidrata: provisiona nova na mesma spec, reponta a conversa e registra
/// <c>sandbox_rehydrated</c> com <c>workspace_restored</c> do dir host-side.
/// </summary>
public sealed class ConversationSandboxGuardTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), $"sc-guard-{Guid.NewGuid():N}");

    public ConversationSandboxGuardTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    private AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);

    private ConversationSandboxGuard NewGuard(
        FakeEventService events, ISandboxService sandboxes)
    {
        var opts = Options.Create(new SandboxOptions
        {
            WorkspaceRoot = _workspace,
        });
        return new ConversationSandboxGuard(NewDb(), sandboxes, events,
            new WorkspaceProvisioner(opts,
                NullLogger<WorkspaceProvisioner>.Instance),
            NullLogger<ConversationSandboxGuard>.Instance);
    }

    private async Task<(Guid convId, Guid sandboxId)> SeedAsync()
    {
        var convId = Guid.NewGuid();
        var sandboxId = Guid.NewGuid();
        await using var db = NewDb();
        db.Conversations.Add(new ConversationRecord
        {
            Id = convId,
            SandboxId = sandboxId,
            ExecutionStatus = ConversationExecutionStatus.Paused.ToString(),
        });
        db.Sandboxes.Add(new SandboxRecord
        {
            Id = sandboxId,
            SpecId = Guid.Empty,
            ConversationId = convId,
            Status = SandboxStatus.Missing.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (convId, sandboxId);
    }

    [Fact]
    public async Task Missing_sandbox_rehydrates_and_repoints_conversation()
    {
        var (convId, oldSandboxId) = await SeedAsync();
        Directory.CreateDirectory(Path.Combine(_workspace,
            convId.ToString("N")));
        File.WriteAllText(Path.Combine(_workspace,
            convId.ToString("N"), "hello.txt"), "x");

        var events = new FakeEventService();
        var sandboxes = new StatusSandboxService(oldSandboxId,
            SandboxStatus.Missing);
        var res = await NewGuard(events, sandboxes)
            .EnsureRunningAsync(convId, CancellationToken.None);

        res.Rehydrated.ShouldBeTrue();
        res.WorkspaceRestored.ShouldBeTrue();
        res.Sandbox.ShouldNotBeNull();
        res.Sandbox!.Id.ShouldBe(sandboxes.FreshId);
        sandboxes.StartCalls.ShouldBe(1);

        await using var db = NewDb();
        (await db.Conversations.FindAsync([convId]))!.SandboxId
            .ShouldBe(sandboxes.FreshId);

        var ev = events.Events.OfType<ConversationStateUpdateEvent>()
            .SingleOrDefault(e => e.Key == "sandbox_rehydrated");
        ev.ShouldNotBeNull();
        ev!.Value.ShouldNotBeNull();
        ev.Value!.Value.GetProperty("workspace_restored").GetBoolean()
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Missing_sandbox_empty_workspace_marks_not_restored()
    {
        var (convId, oldSandboxId) = await SeedAsync();
        var events = new FakeEventService();
        var sandboxes = new StatusSandboxService(oldSandboxId,
            SandboxStatus.Missing);
        var res = await NewGuard(events, sandboxes)
            .EnsureRunningAsync(convId, CancellationToken.None);

        res.Rehydrated.ShouldBeTrue();
        res.WorkspaceRestored.ShouldBeFalse();
    }

    [Fact]
    public async Task Running_sandbox_short_circuits()
    {
        var (convId, sandboxId) = await SeedAsync();
        var events = new FakeEventService();
        var sandboxes = new StatusSandboxService(sandboxId,
            SandboxStatus.Running);
        var res = await NewGuard(events, sandboxes)
            .EnsureRunningAsync(convId, CancellationToken.None);

        res.Rehydrated.ShouldBeFalse();
        res.Sandbox!.Id.ShouldBe(sandboxId);
        sandboxes.StartCalls.ShouldBe(0);
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Sandbox service que devolve status configurável e cria
    /// sandbox nova em <see cref="StartAsync"/>.</summary>
    private sealed class StatusSandboxService(Guid deadId,
        SandboxStatus deadStatus) : ISandboxService
    {
        public Guid FreshId { get; } = Guid.NewGuid();
        public int StartCalls { get; private set; }

        private DomainSandbox Make(Guid id, SandboxStatus status) => new(
            id, Guid.Empty, null, "c", status, null,
            DateTimeOffset.UtcNow, null, null);

        public Task<DomainSandbox> StartAsync(Guid specId,
            Guid? conversationId, CancellationToken ct)
        {
            StartCalls++;
            return Task.FromResult(Make(FreshId, SandboxStatus.Running));
        }

        public Task<DomainSandbox> PauseAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Make(id, SandboxStatus.Paused));
        public Task<DomainSandbox> StopAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Make(id, SandboxStatus.Stopped));
        public Task<DomainSandbox> ResumeAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Make(id, SandboxStatus.Running));
        public Task DeleteAsync(Guid id, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<DomainSandbox?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<DomainSandbox?>(
                id == deadId ? Make(id, deadStatus) : null);
        public Task<IReadOnlyList<DomainSandbox>> SearchAsync(
            SandboxStatus? status, Guid? conversationId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DomainSandbox>>([]);
        public Task<SandboxSpec> UpsertSpecAsync(SandboxSpec spec,
            CancellationToken ct) => Task.FromResult(spec);
        public Task<SandboxSpec?> GetSpecAsync(Guid specId,
            CancellationToken ct) => Task.FromResult<SandboxSpec?>(null);
        public Task SweepOrphansAsync(CancellationToken ct) =>
            Task.CompletedTask;
    }
}