using System.Net.Http;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Automations;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Events;
using Sandcastle.Application.Identity;
using Sandcastle.Domain.Conversations;
using Sandcastle.Infrastructure.Automations;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.UnitTests.Agent;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests;

/// <summary>
/// SPEC-161: robustez do scheduler — classificador transitório/
/// permanente, jitter determinístico ±5%, retry, circuit breaker com
/// audit+notificação, misfire skip/run-once e dispatch paralelo
/// limitado por <c>Automation:MaxConcurrent</c>.
/// </summary>
public sealed class Spec161AutomationRobustnessTests
{
    private sealed class FakeConversationService : IAppConversationService
    {
        private int _created;
        private int _attempts;

        public int Created => _created;
        public int Attempts => _attempts;
        public int CurrentInflight;
        public int MaxInflight;
        public Queue<Exception> ThrowQueue { get; } = new();
        public Exception? ThrowAlways { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public int? ParallelBarrier { get; set; }
        private int _barrierArrived;
        private readonly TaskCompletionSource _barrierRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid ConversationId { get; } = Guid.NewGuid();

        public async Task<AppConversationStartTask> CreateForUserAsync(
            Guid userId, Guid? orgId, CreateConversationRequest request,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            var inflight = Interlocked.Increment(ref CurrentInflight);
            var observed = MaxInflight;
            while (inflight > observed)
            {
                observed = Interlocked.CompareExchange(
                    ref MaxInflight, inflight, observed);
            }

            try
            {
                if (ParallelBarrier is { } barrier)
                {
                    if (Interlocked.Increment(ref _barrierArrived) >= barrier)
                    {
                        _barrierRelease.TrySetResult();
                    }
                    await _barrierRelease.Task.WaitAsync(
                        TimeSpan.FromSeconds(5), ct);
                }
                else if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, ct);
                }

                if (ThrowQueue.Count > 0)
                {
                    throw ThrowQueue.Dequeue();
                }

                if (ThrowAlways is not null)
                {
                    throw ThrowAlways;
                }

                Interlocked.Increment(ref _created);
                return new AppConversationStartTask(
                    Guid.NewGuid(), StartTaskStatus.Working,
                    ConversationId, null, null,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            }
            finally
            {
                Interlocked.Decrement(ref CurrentInflight);
            }
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
            IReadOnlyList<Application.Agent.AgentPermissionRule>? rules,
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
        public List<string> Actions { get; } = [];
        public Task RecordAsync(string action, string? actorUserId = null,
            Guid? orgId = null, string? targetType = null,
            string? targetId = null, string? ip = null,
            string? userAgent = null, object? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
        public Task<(IReadOnlyList<AuditEntry> Items, int Total)> QueryAsync(
            string? userId, string? action, DateTimeOffset? from,
            DateTimeOffset? to, int page, int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<AuditEntry>, int)>(([], 0));
    }

    private sealed class FakeNotifications : IUserNotificationService
    {
        public List<(Guid UserId, Guid AutomationId, string Name, int Failures)>
            Disabled
        { get; } = [];
        public Task NotifyRunStateAsync(Guid userId, Guid conversationId,
            string eventKind, string? title, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<(bool Ok, string? Error)> ValidateUrlAsync(
            string webhookUrl, CancellationToken ct) =>
            Task.FromResult((true, (string?)null));
        public Task<(bool Ok, string? Error)> SendTestAsync(
            string webhookUrl, CancellationToken ct) =>
            Task.FromResult((true, (string?)null));
        public Task NotifyAutomationDisabledAsync(
            Guid userId, Guid automationId, string automationName,
            int consecutiveFailures, CancellationToken ct)
        {
            Disabled.Add((userId, automationId, automationName,
                consecutiveFailures));
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string DbPath { get; init; }
        public required SqliteConnection Conn { get; init; }
        public required AppDbContext Db { get; init; }
        public required FakeConversationService Convs { get; init; }
        public required FakeEventService Events { get; init; }
        public required RecordingAudit Audit { get; init; }
        public required FakeNotifications Notify { get; init; }
        public AutomationOptions Opts { get; init; } = new();

        public AutomationService CreateService() => new(
            Db, Convs, Events, Audit, options:
                Microsoft.Extensions.Options.Options.Create(Opts),
            notifications: Notify);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Conn.DisposeAsync();
            foreach (var path in new[] { DbPath, $"{DbPath}-wal", $"{DbPath}-shm" })
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }
    }

    private static async Task<Fixture> NewAsync(
        Action<AutomationOptions>? configure = null)
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"sc-spec161-{Guid.NewGuid():N}.db");
        var conn = new SqliteConnection(
            $"Data Source={dbPath};Default Timeout=10");
        await conn.OpenAsync();
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(conn).UseSnakeCaseNamingConvention().Options);
        await db.Database.EnsureCreatedAsync();
        var options = new AutomationOptions();
        configure?.Invoke(options);
        return new Fixture
        {
            DbPath = dbPath,
            Conn = conn,
            Db = db,
            Convs = new FakeConversationService(),
            Events = new FakeEventService(),
            Audit = new RecordingAudit(),
            Notify = new FakeNotifications(),
            Opts = options,
        };
    }

    private static readonly Guid Owner = Guid.NewGuid();

    private static AutomationUpsert Upsert(
        string kind = "interval", int? minutes = 15,
        DateTimeOffset? next = null, string? misfirePolicy = null) => new(
        "review diária", "rode a review", null, "owner/repo",
        kind, null, minutes, null, true, next, MisfirePolicy: misfirePolicy);

    [Fact]
    public void Classifier_Separa_Transitorio_De_Permanente()
    {
        AutomationFailureClassifier.IsTransient(
            new HttpRequestException("boom")).ShouldBeTrue();
        AutomationFailureClassifier.IsTransient(
            new TaskCanceledException()).ShouldBeTrue();
        AutomationFailureClassifier.IsTransient(
            new TimeoutException()).ShouldBeTrue();
        AutomationFailureClassifier.IsTransient(
            new IOException("disk")).ShouldBeTrue();
        AutomationFailureClassifier.IsTransient(
            new InvalidOperationException("bad")).ShouldBeFalse();
        // quota é permanente — nunca retry duplo de LLM.
        AutomationFailureClassifier.IsTransient(
            new PlanLimitExceededException(
                new PlanLimitError("conversations_per_day", 3, 3)))
            .ShouldBeFalse();
        // inner exception transitória conta.
        AutomationFailureClassifier.IsTransient(
            new InvalidOperationException("wrap",
                new HttpRequestException("inner"))).ShouldBeTrue();
    }

    [Fact]
    public void Jitter_Deterministico_Dentro_de_5pct_E_Once_Ineutro()
    {
        var rec = new AutomationRecord
        {
            Id = Guid.NewGuid(),
            OrgId = null,
            CreatedByUserId = Owner.ToString(),
            Name = "a",
            Prompt = "p",
            Kind = "task",
            ScheduleKind = AutomationScheduleKinds.Interval,
            IntervalMinutes = 60,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var now = DateTimeOffset.UtcNow;
        var next = now.AddHours(1);
        var j1 = AutomationService.ApplyScheduleJitter(rec, next, now);
        var j2 = AutomationService.ApplyScheduleJitter(rec, next, now);
        j1.ShouldBe(j2); // determinístico
        var delta = Math.Abs((j1 - next).TotalSeconds);
        delta.ShouldBeLessThanOrEqualTo(3600 * 0.05 + 1);

        rec.ScheduleKind = AutomationScheduleKinds.Once;
        AutomationService.ApplyScheduleJitter(rec, next, now)
            .ShouldBe(next);
    }

    [Fact]
    public void Validate_Rejeita_MisfirePolicy_Desconhecida()
    {
        AutomationService.Validate("n", "p", "interval", null, 15, null,
            misfirePolicy: "bogus").ShouldBe("misfire_policy must be skip|run-once");
        AutomationService.Validate("n", "p", "interval", null, 15, null,
            misfirePolicy: "skip").ShouldBeNull();
    }

    [Fact]
    public async Task Retry_Transitorio_Sucesso_Na_Segunda_Tentativa()
    {
        await using var fx = await NewAsync(o =>
        {
            o.MaxTransientAttempts = 3;
            o.RetryBaseDelayMs = 1;
        });
        var svc = fx.CreateService();
        var (info, _) = await svc.CreateAsync(
            Upsert(), Owner, null, CancellationToken.None);
        fx.Convs.ThrowQueue.Enqueue(new HttpRequestException("503"));

        var (run, fired) = await svc.RunNowAsync(
            info!.Id, Owner, CancellationToken.None);

        fired.ShouldBeTrue();
        run!.Status.ShouldBe("running");
        fx.Convs.Attempts.ShouldBe(2);
        fx.Convs.Created.ShouldBe(1);
    }

    [Fact]
    public async Task Retry_Permanente_Falha_Sem_Tentar_De_Novo()
    {
        await using var fx = await NewAsync(o => o.MaxTransientAttempts = 3);
        var svc = fx.CreateService();
        var (info, _) = await svc.CreateAsync(
            Upsert(), Owner, null, CancellationToken.None);
        fx.Convs.ThrowAlways = new PlanLimitExceededException(
            new PlanLimitError("conversations_per_day", 3, 3));

        var (run, _) = await svc.RunNowAsync(
            info!.Id, Owner, CancellationToken.None);

        run!.Status.ShouldBe("failed");
        run.Error.ShouldStartWith("quota:");
        fx.Convs.Attempts.ShouldBe(1); // quota nunca retenta
    }

    [Fact]
    public async Task Retry_Transitorio_Esgota_Tentativas()
    {
        await using var fx = await NewAsync(o =>
        {
            o.MaxTransientAttempts = 3;
            o.RetryBaseDelayMs = 1;
        });
        var svc = fx.CreateService();
        var (info, _) = await svc.CreateAsync(
            Upsert(), Owner, null, CancellationToken.None);
        fx.Convs.ThrowAlways = new HttpRequestException("down");

        var (run, _) = await svc.RunNowAsync(
            info!.Id, Owner, CancellationToken.None);

        run!.Status.ShouldBe("failed");
        run.Error!.ShouldContain("down");
        fx.Convs.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task CircuitBreaker_Desabilita_No_Limiar_Com_Audit_E_Notify()
    {
        await using var fx = await NewAsync(o =>
        {
            o.CircuitBreakerThreshold = 2;
            o.MaxTransientAttempts = 1;
        });
        var svc = fx.CreateService();
        var (info, _) = await svc.CreateAsync(
            Upsert(minutes: 5), Owner, null, CancellationToken.None);
        var automationId = info!.Id;
        fx.Convs.ThrowAlways = new InvalidOperationException("bad config");

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 2; i++)
        {
            // força a automation a ficar devida dentro da graça.
            var rec = await fx.Db.Automations.FindAsync(automationId);
            rec!.NextRunAt = now.AddMinutes(-1);
            rec.NextRunAtTicks = rec.NextRunAt!.Value.UtcTicks;
            await fx.Db.SaveChangesAsync();
            await svc.TickAsync(now, CancellationToken.None);
        }

        var after = await fx.Db.Automations.FindAsync(automationId);
        after!.Enabled.ShouldBeFalse();
        after.NextRunAt.ShouldBeNull();
        after.ConsecutiveFailures.ShouldBe(2);
        fx.Audit.Actions.ShouldContain("automation_circuit_open");
        fx.Notify.Disabled.ShouldHaveSingleItem().UserId.ShouldBe(Owner);

        // DTO expõe circuit_open derivado.
        var dto = await svc.GetAsync(automationId, Owner, CancellationToken.None);
        dto!.CircuitOpen.ShouldBeTrue();
        dto.ConsecutiveFailures.ShouldBe(2);
        dto.MisfirePolicy.ShouldBe("run-once");
    }

    [Fact]
    public async Task Misfire_Skip_Grava_Run_Skipped_Missed_Sem_Disparar()
    {
        await using var fx = await NewAsync();
        var svc = fx.CreateService();
        var (info, _) = await svc.CreateAsync(
            Upsert(minutes: 5, misfirePolicy: "skip"),
            Owner, null, CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        var rec = await fx.Db.Automations.FindAsync(info!.Id);
        rec!.NextRunAt = now.AddMinutes(-10); // além da graça (300s)
        rec.NextRunAtTicks = rec.NextRunAt.Value.UtcTicks;
        await fx.Db.SaveChangesAsync();

        await svc.TickAsync(now, CancellationToken.None);

        fx.Convs.Created.ShouldBe(0);
        var run = await fx.Db.AutomationRuns
            .SingleAsync(r => r.AutomationId == info.Id);
        run.Status.ShouldBe("skipped");
        run.Missed.ShouldBeTrue();
        var dto = await svc.ListRunsAsync(
            info.Id, Owner, 10, CancellationToken.None);
        dto.Single().Missed.ShouldBeTrue();
        // reagendada do now — não fica devida para o próximo tick.
        var after = await fx.Db.Automations.FindAsync(info.Id);
        after!.NextRunAt.ShouldNotBeNull();
        after.NextRunAt!.Value.ShouldBeGreaterThan(now.AddMinutes(-1));
    }

    [Fact]
    public async Task Misfire_RunOnce_Dispara_Uma_Vez_Com_Missed()
    {
        await using var fx = await NewAsync();
        var svc = fx.CreateService();
        var (info, _) = await svc.CreateAsync(
            Upsert(minutes: 5), Owner, null, CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        var rec = await fx.Db.Automations.FindAsync(info!.Id);
        rec!.NextRunAt = now.AddMinutes(-10);
        rec.NextRunAtTicks = rec.NextRunAt.Value.UtcTicks;
        await fx.Db.SaveChangesAsync();

        await svc.TickAsync(now, CancellationToken.None);

        fx.Convs.Created.ShouldBe(1);
        var run = await fx.Db.AutomationRuns
            .SingleAsync(r => r.AutomationId == info.Id);
        run.Missed.ShouldBeTrue();
        run.Status.ShouldBe("running");
    }

    [Fact]
    public async Task Tick_Paralelo_Respeita_MaxConcurrent()
    {
        await using var fx = await NewAsync(o =>
        {
            o.MaxConcurrent = 2;
        });
        fx.Convs.ParallelBarrier = 2;

        // ServiceProvider real: o dispatch paralelo resolve cada
        // automation no próprio scope (DbContext isolado).
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o
            .UseSqlite($"Data Source={fx.DbPath};Default Timeout=10")
            .UseSnakeCaseNamingConvention());
        services.AddSingleton<IAppConversationService>(fx.Convs);
        services.AddSingleton<IEventService>(fx.Events);
        services.AddSingleton<IAuditService>(fx.Audit);
        services.AddSingleton<IUserNotificationService>(fx.Notify);
        services.AddSingleton<IOptions<AutomationOptions>>(
            Microsoft.Extensions.Options.Options.Create(fx.Opts));
        services.AddScoped<IAutomationService, AutomationService>();
        await using var provider = services.BuildServiceProvider();

        var svc = fx.CreateService();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 6; i++)
        {
            var (info, _) = await svc.CreateAsync(
                Upsert(minutes: 5), Owner, null, CancellationToken.None);
            var rec = await fx.Db.Automations.FindAsync(info!.Id);
            rec!.NextRunAt = now.AddMinutes(-1);
            rec.NextRunAtTicks = rec.NextRunAt.Value.UtcTicks;
            await fx.Db.SaveChangesAsync();
        }

        var scoped = provider.GetRequiredService<IAutomationService>();
        await scoped.TickAsync(now, CancellationToken.None);

        fx.Convs.Created.ShouldBe(6);
        fx.Convs.MaxInflight.ShouldBe(2);
    }
}