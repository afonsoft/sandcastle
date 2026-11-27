using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Automations;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Identity;
using Sandcastle.Domain.Conversations;
using Sandcastle.Infrastructure.Automations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.UnitTests.Agent;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests;

/// <summary>
/// SPEC-163: observabilidade — nível de saúde puro, stats agregadas e
/// idempotência de webhook (dedup 24h).
/// </summary>
public sealed class Spec163AutomationObservabilityTests
{
    // ---------- AutomationHealth (puro) ----------

    [Fact]
    public void Health_Verde_Com_Taxa_Alta_Sem_Falhas()
    {
        AutomationHealth.Level(0.95, 0, false)
            .ShouldBe(AutomationHealth.Green);
        AutomationHealth.Level(null, 0, false)
            .ShouldBe(AutomationHealth.Green);
    }

    [Fact]
    public void Health_Amarelo_Em_Faixa_Media_Ou_1_2_Falhas()
    {
        AutomationHealth.Level(0.80, 0, false)
            .ShouldBe(AutomationHealth.Yellow);
        AutomationHealth.Level(0.95, 1, false)
            .ShouldBe(AutomationHealth.Yellow);
        AutomationHealth.Level(0.95, 2, false)
            .ShouldBe(AutomationHealth.Yellow);
    }

    [Fact]
    public void Health_Vermelho_Abaixo_70_ou_3_Falhas_Ou_Circuit()
    {
        AutomationHealth.Level(0.60, 0, false)
            .ShouldBe(AutomationHealth.Red);
        AutomationHealth.Level(0.95, 3, false)
            .ShouldBe(AutomationHealth.Red);
        AutomationHealth.Level(1.0, 0, true)
            .ShouldBe(AutomationHealth.Red);
    }

    // ---------- Service: stats + dedup ----------

    private sealed class FakeConversations : IAppConversationService
    {
        public int Created;

        public Task<AppConversationStartTask> CreateForUserAsync(
            Guid userId, Guid? orgId, CreateConversationRequest request,
            CancellationToken ct)
        {
            Created++;
            return Task.FromResult(new AppConversationStartTask(
                Guid.NewGuid(), StartTaskStatus.Working,
                Guid.NewGuid(), null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public Task<AppConversationStartTask> CreateAsync(
            CreateConversationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ConversationPage> SearchAsync(
            ConversationQuery query, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<long> CountAsync(
            ConversationQuery query, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> ArchiveAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> UnarchiveAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ConversationPage> ListArchivedAsync(
            ConversationQuery query, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<long> ArchivedCountAsync(CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AppConversation>> GetBatchAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AppConversation?> GetAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AppConversation?> UpdateAsync(Guid id, string? title,
            IReadOnlyList<string>? tags, bool? pinned,
            CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SendMessageError?> ValidateSendMessageAsync(
            Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SwitchResult> SwitchProfileAsync(
            Guid id, string? profile, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SwitchResult> SwitchModelAsync(
            Guid id, string model, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AppConversation?> SetPermissionsAsync(Guid id,
            IReadOnlyList<AgentPermissionRule>? rules,
            CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AppConversation?> SetModeAsync(
            Guid id, string mode, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(
            int limit, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AttentionDismissResult> DismissAttentionAsync(
            Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AppConversation?> UpdateAsync(Guid id, string? title,
            IReadOnlyList<string>? tags, bool? pinned, Guid? outpostId,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class NoopAudit : IAuditService
    {
        public Task RecordAsync(string action, string? actorUserId = null,
            Guid? orgId = null, string? targetType = null,
            string? targetId = null, string? ip = null,
            string? userAgent = null, object? metadata = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<(IReadOnlyList<AuditEntry> Items, int Total)> QueryAsync(
            string? userId, string? action, DateTimeOffset? from,
            DateTimeOffset? to, int page, int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
    }

    private static async Task<(SqliteConnection Conn, AppDbContext Db,
        AutomationService Svc, FakeConversations Convs)>
        NewAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(conn).UseSnakeCaseNamingConvention().Options);
        await db.Database.EnsureCreatedAsync();
        var convs = new FakeConversations();
        return (conn, db, new AutomationService(
            db, convs, new FakeEventService(), new NoopAudit()), convs);
    }

    private static readonly Guid Owner = Guid.NewGuid();

    private static AutomationRunRecord Run(
        Guid automationId, string status, string? error = null,
        DateTimeOffset? at = null)
    {
        var t = at ?? DateTimeOffset.UtcNow;
        return new AutomationRunRecord
        {
            Id = Guid.NewGuid(),
            AutomationId = automationId,
            StartedAt = t,
            StartedAtTicks = t.UtcTicks,
            FinishedAt = t,
            Status = status,
            Error = error,
        };
    }

    [Fact]
    public async Task Stats_Agrega_Contadores_E_Saude()
    {
        var (conn, db, svc, _) = await NewAsync();
        await using var _ = conn;
        var (info, _) = await svc.CreateAsync(new AutomationUpsert(
            "auto", "prompt", null, null,
            "interval", null, 30, null, true, null),
            Owner, null, CancellationToken.None);
        var autoId = info!.Id;
        // 8 sucessos + 2 falhas recentes → 80% → yellow.
        for (var i = 0; i < 8; i++)
        {
            db.AutomationRuns.Add(Run(autoId, "succeeded",
                at: DateTimeOffset.UtcNow.AddMinutes(-i)));
        }

        db.AutomationRuns.Add(Run(autoId, "failed", "boom",
            at: DateTimeOffset.UtcNow.AddMinutes(-20)));
        db.AutomationRuns.Add(Run(autoId, "failed", "boom2",
            at: DateTimeOffset.UtcNow.AddMinutes(-21)));
        db.AutomationRuns.Add(Run(autoId, "skipped",
            at: DateTimeOffset.UtcNow.AddMinutes(-22)));
        await db.SaveChangesAsync();

        var stats = await svc.GetStatsAsync(
            autoId, Owner, CancellationToken.None);
        stats.ShouldNotBeNull();
        stats!.Total.ShouldBe(11);
        stats.Succeeded.ShouldBe(8);
        stats.Failed.ShouldBe(2);
        stats.Skipped.ShouldBe(1);
        stats.SuccessRateLast20!.Value.ShouldBe(0.8, 0.001);
        stats.LastError.ShouldBe("boom");
        stats.Health.ShouldBe(AutomationHealth.Yellow);
        stats.LastRuns.Count.ShouldBe(10);
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Stats_404_Para_Outro_Usuario()
    {
        var (conn, db, svc, _) = await NewAsync();
        await using var _ = conn;
        var (info, _) = await svc.CreateAsync(new AutomationUpsert(
            "auto", "prompt", null, null,
            "interval", null, 30, null, true, null),
            Owner, null, CancellationToken.None);
        (await svc.GetStatsAsync(
            info!.Id, Guid.NewGuid(), CancellationToken.None))
            .ShouldBeNull();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Lista_Expoe_Health()
    {
        var (conn, db, svc, _) = await NewAsync();
        await using var _ = conn;
        var (info, _) = await svc.CreateAsync(new AutomationUpsert(
            "auto", "prompt", null, null,
            "interval", null, 30, null, true, null),
            Owner, null, CancellationToken.None);
        (await svc.ListAsync(Owner, CancellationToken.None))
            .Single().Health.ShouldBe(AutomationHealth.Green);
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Hook_Dedup_Mesma_Chave_Nao_Cria_Segundo_Run()
    {
        var (conn, db, svc, convs) = await NewAsync();
        await using var _ = conn;
        var (info, _) = await svc.CreateAsync(new AutomationUpsert(
            "auto", "prompt", null, null,
            "interval", null, 30, null, true, null),
            Owner, null, CancellationToken.None);
        var (_, token) = await svc.CreateHookAsync(
            info!.Id, Owner, CancellationToken.None);

        var first = await svc.FireByTokenAsync(
            token!, null, "key-1", "hash-a", CancellationToken.None);
        first.ShouldNotBeNull();
        first!.Deduplicated.ShouldBeFalse();
        first.Run.ShouldNotBeNull();
        convs.Created.ShouldBe(1);

        var second = await svc.FireByTokenAsync(
            token!, null, "key-1", "hash-a", CancellationToken.None);
        second.ShouldNotBeNull();
        second!.Deduplicated.ShouldBeTrue();
        second.Run.ShouldBeNull();
        second.RunId.ShouldBe(first.Run!.Id);
        convs.Created.ShouldBe(1);

        // chave diferente → novo run (marca o anterior como succeeded
        // para liberar o guard de concorrência).
        await db.AutomationRuns.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, "succeeded"));
        var third = await svc.FireByTokenAsync(
            token!, null, "key-2", "hash-b", CancellationToken.None);
        third!.Deduplicated.ShouldBeFalse();
        convs.Created.ShouldBe(2);
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Hook_Sem_Header_Usa_Hash_Do_Payload()
    {
        var (conn, db, svc, convs) = await NewAsync();
        await using var _ = conn;
        var (info, _) = await svc.CreateAsync(new AutomationUpsert(
            "auto", "prompt", null, null,
            "interval", null, 30, null, true, null),
            Owner, null, CancellationToken.None);
        var (_, token) = await svc.CreateHookAsync(
            info!.Id, Owner, CancellationToken.None);
        await svc.FireByTokenAsync(
            token!, null, null, "hash-x", CancellationToken.None);
        var dup = await svc.FireByTokenAsync(
            token!, null, null, "hash-x", CancellationToken.None);
        dup!.Deduplicated.ShouldBeTrue();
        convs.Created.ShouldBe(1);
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Hook_Janela_24h_Expirada_Permite_Reuso()
    {
        var (conn, db, svc, convs) = await NewAsync();
        await using var _ = conn;
        var (info, _) = await svc.CreateAsync(new AutomationUpsert(
            "auto", "prompt", null, null,
            "interval", null, 30, null, true, null),
            Owner, null, CancellationToken.None);
        var (hook, token) = await svc.CreateHookAsync(
            info!.Id, Owner, CancellationToken.None);
        // entrega antiga (>24h) com a mesma chave.
        db.AutomationHookDeliveries.Add(new AutomationHookDeliveryRecord
        {
            Id = Guid.NewGuid(),
            HookId = hook!.Id,
            IdempotencyKey = "key-old",
            PayloadHash = "hash-old",
            ReceivedAt = DateTimeOffset.UtcNow.AddHours(-25),
            ReceivedAtTicks = DateTimeOffset.UtcNow.AddHours(-25).UtcTicks,
            RunId = null,
        });
        await db.SaveChangesAsync();

        var result = await svc.FireByTokenAsync(
            token!, null, "key-old", "hash-old", CancellationToken.None);
        result!.Deduplicated.ShouldBeFalse();
        convs.Created.ShouldBe(1);
        await db.DisposeAsync();
    }
}