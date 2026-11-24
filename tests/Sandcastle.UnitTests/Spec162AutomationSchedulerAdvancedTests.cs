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
/// SPEC-162: agendamentos avançados — kind=cron (Cronos), timezone IANA
/// em daily/weekly/cron, approval preset na conversa disparada.
/// </summary>
public sealed class Spec162AutomationSchedulerAdvancedTests
{
    // ---------- AutomationSchedule (puro) ----------

    private static readonly DateTimeOffset Now =
        new(2026, 10, 17, 10, 30, 0, TimeSpan.Zero); // sábado

    [Fact]
    public void Cron_Proximo_Ocorrencia_No_Fuso()
    {
        // "0 9 * * 1-5" em America/Sao_Paulo (UTC-3, sem DST desde 2019).
        var next = AutomationSchedule.ComputeNextRun(
            "cron", null, null, null, null, Now,
            cronExpression: "0 9 * * 1-5",
            timeZoneId: "America/Sao_Paulo");
        next.ShouldNotBeNull();
        // 9h em São Paulo = 12h UTC; sábado não é dia útil → segunda.
        next!.Value.UtcDateTime.ShouldBe(
            new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Cron_Expessao_Invalida_Retorna_Null()
    {
        AutomationSchedule.IsValidCronExpression("0 9 * * 1-5")
            .ShouldBeTrue();
        AutomationSchedule.IsValidCronExpression("não é cron")
            .ShouldBeFalse();
        AutomationSchedule.IsValidCronExpression(null).ShouldBeFalse();
        AutomationSchedule.ComputeNextRun(
            "cron", null, null, null, null, Now,
            cronExpression: "não é cron",
            timeZoneId: "UTC").ShouldBeNull();
    }

    [Fact]
    public void Daily_Timezone_Converte_Para_Utc()
    {
        // 09:00 em America/Sao_Paulo = 12:00 UTC (sem DST).
        var next = AutomationSchedule.ComputeNextRun(
            "daily", "09:00", null, null, null, Now,
            timeZoneId: "America/Sao_Paulo");
        next.ShouldBe(new DateTimeOffset(
            2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Daily_Dst_Horario_De_Verao()
    {
        // America/New_York: 09:00 local em julho = 13:00 UTC (EDT, UTC-4);
        // em janeiro = 14:00 UTC (EST, UTC-5). O cálculo acompanha o DST.
        var jul = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        var jan = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        AutomationSchedule.ComputeNextRun(
            "daily", "09:00", null, null, null, jul,
            timeZoneId: "America/New_York")
            .ShouldBe(new DateTimeOffset(
                2026, 7, 15, 13, 0, 0, TimeSpan.Zero));
        AutomationSchedule.ComputeNextRun(
            "daily", "09:00", null, null, null, jan,
            timeZoneId: "America/New_York")
            .ShouldBe(new DateTimeOffset(
                2026, 1, 15, 14, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Timezone_Invalida_Fallback_Utc()
    {
        AutomationSchedule.ResolveTimeZone("America/Sao_Paulo")
            .Id.ShouldBe("America/Sao_Paulo");
        AutomationSchedule.ResolveTimeZone("bogus/tz").ShouldBe(TimeZoneInfo.Utc);
        AutomationSchedule.ResolveTimeZone(null).ShouldBe(TimeZoneInfo.Utc);
        // daily com tz inválida comporta-se como UTC.
        AutomationSchedule.ComputeNextRun(
            "daily", "11:00", null, null, null, Now,
            timeZoneId: "bogus/tz")
            .ShouldBe(new DateTimeOffset(
                2026, 10, 17, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Interval_Ignora_Timezone()
    {
        AutomationSchedule.ComputeNextRun(
            "interval", null, 30, null, null, Now,
            timeZoneId: "America/Sao_Paulo")
            .ShouldBe(Now.AddMinutes(30));
    }

    [Fact]
    public void Weekly_Timezone_Proximo_Dia_Util()
    {
        // sábado 10:30 UTC → domingo 09:00 em São Paulo = 12:00 UTC.
        var next = AutomationSchedule.ComputeNextRun(
            "weekly", "09:00", null, 0, null, Now,
            timeZoneId: "America/Sao_Paulo");
        next.ShouldBe(new DateTimeOffset(
            2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
    }

    // ---------- AutomationService (validação + preset) ----------

    private sealed class RecordingConversationService : IAppConversationService
    {
        public CreateConversationRequest? LastRequest { get; private set; }
        public Task<AppConversationStartTask> CreateForUserAsync(
            Guid userId, Guid? orgId, CreateConversationRequest request,
            CancellationToken ct)
        {
            LastRequest = request;
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
        public Task<bool> ArchiveAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UnarchiveAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConversationPage> ListArchivedAsync(ConversationQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> ArchivedCountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<long> CountAsync(
            ConversationQuery query, CancellationToken ct) =>
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

    private sealed class RecordingAudit : IAuditService
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
        AutomationService Svc, RecordingConversationService Convs)>
        NewAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(conn).UseSnakeCaseNamingConvention().Options);
        await db.Database.EnsureCreatedAsync();
        var convs = new RecordingConversationService();
        return (conn, db, new AutomationService(
            db, convs, new FakeEventService(), new RecordingAudit()), convs);
    }

    private static readonly Guid Owner = Guid.NewGuid();

    [Fact]
    public async Task Create_Cron_Valido_Persiste_Expressao_E_Fuso()
    {
        var (conn, db, svc, _) = await NewAsync();
        await using var _ = conn;
        var (info, error) = await svc.CreateAsync(new AutomationUpsert(
            "cron job", "prompt", null, null,
            "cron", null, null, null, true, null,
            CronExpression: "0 9 * * 1-5",
            TimeZoneId: "America/Sao_Paulo"),
            Owner, null, CancellationToken.None);
        error.ShouldBeNull();
        info!.ScheduleKind.ShouldBe("cron");
        info.CronExpression.ShouldBe("0 9 * * 1-5");
        info.TimeZoneId.ShouldBe("America/Sao_Paulo");
        info.NextRunAt.ShouldNotBeNull();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Create_Cron_Sem_Expressao_Falha()
    {
        var (conn, db, svc, _) = await NewAsync();
        await using var _ = conn;
        var (info, error) = await svc.CreateAsync(new AutomationUpsert(
            "cron job", "prompt", null, null,
            "cron", null, null, null, true, null),
            Owner, null, CancellationToken.None);
        info.ShouldBeNull();
        error.ShouldBe("cron_expression required for cron schedule");
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Create_Cron_Expressao_Invalida_Falha()
    {
        var (conn, db, svc, _) = await NewAsync();
        await using var _ = conn;
        var (info, error) = await svc.CreateAsync(new AutomationUpsert(
            "cron job", "prompt", null, null,
            "cron", null, null, null, true, null,
            CronExpression: "não é cron"),
            Owner, null, CancellationToken.None);
        info.ShouldBeNull();
        error.ShouldBe("cron_expression is not a valid 5-field cron");
        await db.DisposeAsync();
    }

    [Fact]
    public async Task Create_ApprovalPreset_Invalido_Falha()
    {
        var (conn, db, svc, _) = await NewAsync();
        await using var _ = conn;
        var (info, error) = await svc.CreateAsync(new AutomationUpsert(
            "auto", "prompt", null, null,
            "interval", null, 30, null, true, null,
            ApprovalPreset: "yolo"),
            Owner, null, CancellationToken.None);
        info.ShouldBeNull();
        error.ShouldBe("approval_preset must be allow|allow-readonly|plan");
        await db.DisposeAsync();
    }

    [Fact]
    public async Task ApprovalPreset_Plan_Dispara_Conversa_Em_Plan_Mode()
    {
        var (conn, db, svc, convs) = await NewAsync();
        await using var _ = conn;
        var (info, _) = await svc.CreateAsync(new AutomationUpsert(
            "auto", "prompt", null, null,
            "interval", null, 30, null, true, null,
            ApprovalPreset: "plan"),
            Owner, null, CancellationToken.None);
        await svc.RunNowAsync(info!.Id, Owner, CancellationToken.None);
        convs.LastRequest!.Mode.ShouldBe("plan");
        convs.LastRequest.Permissions.ShouldBeNull();
        await db.DisposeAsync();
    }

    [Fact]
    public async Task ApprovalPreset_AllowReadonly_Pede_Ask_Nas_Writes()
    {
        var (conn, db, svc, convs) = await NewAsync();
        await using var _ = conn;
        var (info, _) = await svc.CreateAsync(new AutomationUpsert(
            "auto", "prompt", null, null,
            "interval", null, 30, null, true, null,
            ApprovalPreset: "allow-readonly"),
            Owner, null, CancellationToken.None);
        await svc.RunNowAsync(info!.Id, Owner, CancellationToken.None);
        convs.LastRequest!.Mode.ShouldBeNull();
        var rules = convs.LastRequest.Permissions!;
        rules.ShouldContain(r => r.Pattern == "file_write"
            && r.Decision == PermissionDecision.Ask);
        rules.ShouldContain(r => r.Pattern == "git_commit"
            && r.Decision == PermissionDecision.Ask);
        // nunca amplia: nenhuma regra allow.
        rules.ShouldAllBe(r => r.Decision == PermissionDecision.Ask);
        await db.DisposeAsync();
    }

    [Fact]
    public async Task ApprovalPreset_Null_Mantem_Default()
    {
        var (conn, db, svc, convs) = await NewAsync();
        await using var _ = conn;
        var (info, _) = await svc.CreateAsync(new AutomationUpsert(
            "auto", "prompt", null, null,
            "interval", null, 30, null, true, null),
            Owner, null, CancellationToken.None);
        await svc.RunNowAsync(info!.Id, Owner, CancellationToken.None);
        convs.LastRequest!.Mode.ShouldBeNull();
        convs.LastRequest.Permissions.ShouldBeNull();
        await db.DisposeAsync();
    }
}