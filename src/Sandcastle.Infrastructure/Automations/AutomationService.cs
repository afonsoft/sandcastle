using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Automations;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Events;
using Sandcastle.Application.Identity;
using Sandcastle.Domain.Conversations;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Automations;

/// <summary>
/// SPEC-47: automations — uma execução = start-task de conversa
/// (<see cref="IAppConversationService.CreateForUserAsync"/>) com as
/// credenciais do owner; runs <c>running</c> reconciliam contra o
/// ExecutionStatus da conversa linkada.
/// </summary>
public sealed class AutomationService(
    AppDbContext db,
    IAppConversationService conversations,
    IEventService events,
    IAuditService? audit = null,
    ILogger<AutomationService>? logger = null,
    // SPEC-143: kind=scan dispara IScanService (não conversa solta).
    Application.Scans.IScanService? scans = null,
    // SPEC-161: limites do scheduler + dispatch paralelo + notificação
    // do circuit breaker (todos opcionais — testes constroem direto).
    IOptions<AutomationOptions>? options = null,
    IServiceScopeFactory? scopes = null,
    IUserNotificationService? notifications = null) : IAutomationService
{
    private readonly AutomationOptions _options =
        options?.Value ?? new AutomationOptions();
    /// <summary>Tamanho máximo do prompt (SPEC-47 RF-003).</summary>
    public const int MaxPromptChars = 8192;

    /// <summary>Intervalo mínimo aceito (minutos).</summary>
    public const int MinIntervalMinutes = 5;

    /// <summary>Status de run falho (constante p/ Sonar S1192).</summary>
    private const string RunStatusFailed = "failed";

    /// <summary>targetType de audit das automations (S1192).</summary>
    private const string AuditTargetAutomation = "automation";

    private AutomationInfo ToInfo(AutomationRecord r) => new(
        r.Id, r.OrgId, r.CreatedByUserId, r.Name, r.Prompt, r.LlmModel,
        r.SelectedRepository, r.ScheduleKind, r.ScheduleTimeUtc,
        r.IntervalMinutes, r.Weekday, r.Enabled, r.NextRunAt, r.LastRunAt,
        r.CreatedAt, r.UpdatedAt, r.Kind, r.ScanConfigJson,
        // SPEC-161: circuit_open é derivado (desabilitado + limiar).
        r.ConsecutiveFailures,
        CircuitOpen: !r.Enabled
            && r.ConsecutiveFailures >= _options.CircuitBreakerThreshold,
        r.MisfirePolicy,
        // SPEC-162: cron + timezone + approval preset.
        r.TimeZoneId, r.CronExpression, r.ApprovalPreset);

    private static AutomationRunInfo ToRunInfo(AutomationRunRecord r) => new(
        r.Id, r.AutomationId, r.StartedAt, r.FinishedAt, r.Status,
        r.ConversationId, r.Error, r.Missed);

    /// <summary>Validação de upsert (SPEC-47 RF-003 + SPEC-143 kind).</summary>
    public static string? Validate(
        string? name, string? prompt, string? scheduleKind,
        string? scheduleTimeUtc, int? intervalMinutes,
        DateTimeOffset? nextRunAt, string? kind = null,
        string? misfirePolicy = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "name required";
        }

        if (kind is not null && !AutomationKinds.All.Contains(kind))
        {
            return "kind must be task|scan";
        }

        if (misfirePolicy is not null
            && !AutomationMisfirePolicies.All.Contains(misfirePolicy))
        {
            return "misfire_policy must be skip|run-once";
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "prompt required";
        }

        if (prompt.Length > MaxPromptChars)
        {
            return $"prompt exceeds {MaxPromptChars} chars";
        }

        if (!AutomationScheduleKinds.All.Contains(scheduleKind ?? ""))
        {
            return "schedule_kind must be interval|daily|weekly|once|cron";
        }

        switch (scheduleKind)
        {
            case AutomationScheduleKinds.Interval
                when (intervalMinutes ?? 0) < MinIntervalMinutes:
                return $"interval_minutes must be >= {MinIntervalMinutes}";
            case AutomationScheduleKinds.Daily
                when string.IsNullOrWhiteSpace(scheduleTimeUtc):
                return "schedule_time_utc (HH:mm) required for daily";
            case AutomationScheduleKinds.Weekly
                when string.IsNullOrWhiteSpace(scheduleTimeUtc):
                return "schedule_time_utc (HH:mm) required for weekly";
            case AutomationScheduleKinds.Once when nextRunAt is null:
                return "next_run_at required for once";
        }

        return null;
    }

    /// <summary>
    /// SPEC-162 RF-001/RF-004: valida os campos avançados do schedule —
    /// cron obrigatório e parseável para kind=cron; approval preset no
    /// conjunto fechado. Timezone IANA inválido é tolerado (fallback UTC
    /// no cálculo — guardrail do tick).
    /// </summary>
    private static string? ValidateScheduleAdvanced(
        string scheduleKind, string? cronExpression,
        string? approvalPreset)
    {
        if (scheduleKind == AutomationScheduleKinds.Cron
            && string.IsNullOrWhiteSpace(cronExpression))
        {
            return "cron_expression required for cron schedule";
        }

        if (scheduleKind == AutomationScheduleKinds.Cron
            && !AutomationSchedule.IsValidCronExpression(cronExpression))
        {
            return "cron_expression is not a valid 5-field cron";
        }

        if (approvalPreset is not null
            && !AutomationApprovalPresets.All.Contains(approvalPreset))
        {
            return "approval_preset must be allow|allow-readonly|plan";
        }

        return null;
    }

    /// <summary>Tools de escrita que viram ask no preset allow-readonly.</summary>
    private static readonly IReadOnlyList<AgentPermissionRule>
        ReadOnlyWriteRules =
    [
        new("file_write", PermissionDecision.Ask),
        new("file_edit", PermissionDecision.Ask),
        new("git_commit", PermissionDecision.Ask),
    ];

    /// <summary>
    /// SPEC-162 RF-004: mapeia o approval preset para modo/permissões da
    /// conversa — só restringe (nunca amplia além do default do usuário).
    /// </summary>
    private static (string? Mode,
        IReadOnlyList<AgentPermissionRule>? Permissions)
        MapApprovalPreset(string? preset) => preset switch
        {
            AutomationApprovalPresets.AllowReadonly =>
                (null, ReadOnlyWriteRules),
            AutomationApprovalPresets.Plan => ("plan", null),
            _ => (null, null),
        };

    /// <summary>
    /// SPEC-143: config persistida de uma automation kind=scan — repos +
    /// profile + effort (goal = <see cref="AutomationRecord.Prompt"/>).
    /// </summary>
    public sealed record AutomationScanConfig(
        IReadOnlyList<string> Repositories,
        string? Profile,
        string? Effort)
    {
        /// <summary>Parse defensivo do JSON persistido.</summary>
        public static AutomationScanConfig? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var repos = root.TryGetProperty("repositories", out var r)
                    && r.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? r.EnumerateArray()
                        .Where(e => e.ValueKind
                            == System.Text.Json.JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToList()
                    : [];
                return new AutomationScanConfig(repos,
                    root.TryGetProperty("profile", out var p)
                        ? p.GetString() : null,
                    root.TryGetProperty("effort", out var e2)
                        ? e2.GetString() : null);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>Serializa para a coluna scan_config_json.</summary>
        public string ToJson() => System.Text.Json.JsonSerializer
            .Serialize(new
            {
                repositories = Repositories,
                profile = Profile,
                effort = Effort,
            });
    }

    /// <summary>
    /// Monta o <c>scan_config_json</c> do upsert: kind=scan exige ≥1 repo
    /// (Repositories ou SelectedRepository) e valida profile/effort.
    /// Retorna (json, null) ou (null, erro).
    /// </summary>
    internal static (string? Json, string? Error) BuildScanConfig(
        string kind, AutomationUpsert req, AutomationRecord? existing = null)
    {
        if (kind != AutomationKinds.Scan)
        {
            return (null, null);
        }

        var single = req.SelectedRepository
            ?? existing?.SelectedRepository;
        var repos = req.Repositories
            ?? AutomationScanConfig.Parse(existing?.ScanConfigJson)
                ?.Repositories
            ?? (single is { Length: > 0 }
                ? [single]
                : (IReadOnlyList<string>?)null);
        if (repos is not { Count: > 0 })
        {
            return (null, "scan automation requires repositories[] "
                + "or selected_repository");
        }

        var profile = req.ScanProfile
            ?? AutomationScanConfig.Parse(existing?.ScanConfigJson)?.Profile;
        var effort = req.ScanEffort
            ?? AutomationScanConfig.Parse(existing?.ScanConfigJson)?.Effort;
        if (Application.Scans.ScanProfiles.Normalize(profile) is null)
        {
            return (null, $"invalid scan profile '{profile}'");
        }
        if (Application.Scans.ScanEfforts.Normalize(effort) is null)
        {
            return (null, $"invalid scan effort '{effort}'");
        }

        return (new AutomationScanConfig(
            repos.Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim()).ToList(),
            profile, effort).ToJson(), null);
    }

    /// <summary>
    /// SPEC-161 RF-005: jitter de ±5% no próximo disparo recorrente
    /// (interval/daily/weekly) para evitar thundering herd. Determinístico
    /// — seed = automation id + ticks do disparo calculado — então testes
    /// e reagendamentos do mesmo instante são estáveis. <c>once</c> nunca
    /// recebe jitter (o horário é escolha explícita do usuário).
    /// </summary>
    internal static DateTimeOffset ApplyScheduleJitter(
        AutomationRecord a, DateTimeOffset next, DateTimeOffset now)
    {
        if (a.ScheduleKind == AutomationScheduleKinds.Once)
        {
            return next;
        }

        // ±5% do período até o disparo (não dos ticks absolutos — senão
        // o jitter seria de anos). Determinístico por (id, next).
        var periodSeconds = (next - now).TotalSeconds;
        if (periodSeconds <= 0)
        {
            return next;
        }

        // SHA-256 do seed → double determinístico em [0,1). Evita
        // System.Random (S2245) e mantém o jitter estável por (id,
        // next) — testes e reagendamentos do mesmo instante coincidem.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{a.Id:N}:{next.UtcTicks}"));
        var unit = BitConverter.ToUInt64(hash, 0)
            / (double)ulong.MaxValue;
        var deltaSeconds = periodSeconds * 0.05 * (unit * 2.0 - 1.0);
        return next.AddSeconds(deltaSeconds);
    }

    private static void Reschedule(AutomationRecord a, DateTimeOffset now)
    {
        var next = a.Enabled
            ? AutomationSchedule.ComputeNextRun(
                a.ScheduleKind, a.ScheduleTimeUtc, a.IntervalMinutes,
                a.Weekday, a.NextRunAt, now,
                a.CronExpression, a.TimeZoneId)
            : null;
        a.NextRunAt = next is { } n
            ? ApplyScheduleJitter(a, n, now)
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AutomationInfo>> ListAsync(
        Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var rows = await db.Automations.AsNoTracking()
            .Where(a => a.CreatedByUserId == owner)
            .OrderBy(a => a.Name).ThenBy(a => a.Id)
            .ToListAsync(ct);
        var result = new List<AutomationInfo>(rows.Count);
        foreach (var r in rows)
        {
            // SPEC-163 RF-002: health na lista (derivado dos runs).
            var info = ToInfo(r);
            result.Add(info with
            {
                Health = await HealthAsync(r, ct),
            });
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<AutomationInfo?> GetAsync(
        Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var r = await db.Automations.AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.Id == id && a.CreatedByUserId == owner, ct);
        if (r is null)
        {
            return null;
        }

        var info = ToInfo(r);
        return info with { Health = await HealthAsync(r, ct) };
    }

    /// <summary>
    /// SPEC-163 RF-002: saúde derivada — taxa de sucesso nos últimos 20
    /// runs (exclui skipped) + falhas consecutivas + circuit-open.
    /// </summary>
    private async Task<string> HealthAsync(
        AutomationRecord r, CancellationToken ct)
    {
        var statuses = await db.AutomationRuns.AsNoTracking()
            .Where(x => x.AutomationId == r.Id && x.Status != "skipped")
            .OrderByDescending(x => x.StartedAtTicks)
            .Take(20)
            .Select(x => x.Status)
            .ToListAsync(ct);
        double? rate = statuses.Count == 0
            ? null
            : statuses.Count(s => s == "succeeded") / (double)statuses.Count;
        return AutomationHealth.Level(
            rate, r.ConsecutiveFailures, CircuitOpen(r));
    }

    /// <summary>SPEC-161: circuit-open derivado (desabilitado no limiar).</summary>
    private bool CircuitOpen(AutomationRecord r) =>
        !r.Enabled
        && r.ConsecutiveFailures >= _options.CircuitBreakerThreshold;

    /// <inheritdoc />
    public async Task<AutomationStats?> GetStatsAsync(
        Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var r = await db.Automations.AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.Id == id && a.CreatedByUserId == owner, ct);
        if (r is null)
        {
            return null;
        }

        var automationRuns = db.AutomationRuns.AsNoTracking()
            .Where(x => x.AutomationId == id);
        var ordered = automationRuns
            .OrderByDescending(x => x.StartedAtTicks);
        var total = await automationRuns.LongCountAsync(ct);
        var succeeded = await automationRuns
            .LongCountAsync(x => x.Status == "succeeded", ct);
        var failed = await automationRuns
            .LongCountAsync(x => x.Status == "failed", ct);
        var skipped = await automationRuns
            .LongCountAsync(x => x.Status == "skipped", ct);
        var last20 = await automationRuns
            .Where(x => x.Status != "skipped")
            .OrderByDescending(x => x.StartedAtTicks)
            .Take(20)
            .Select(x => x.Status)
            .ToListAsync(ct);
        var lastError = await ordered
            .Where(x => x.Status == "failed")
            .Select(x => x.Error)
            .FirstOrDefaultAsync(ct);
        var lastRuns = await ordered.Take(10).ToListAsync(ct);
        double? rate = last20.Count == 0
            ? null
            : last20.Count(x => x == "succeeded")
                / (double)last20.Count;
        var circuitOpen = CircuitOpen(r);
        return new AutomationStats(
            r.Id,
            total,
            succeeded,
            failed,
            skipped,
            rate,
            lastError,
            r.ConsecutiveFailures,
            AutomationHealth.Level(rate, r.ConsecutiveFailures, circuitOpen),
            lastRuns.Select(ToRunInfo).ToList());
    }

    /// <inheritdoc />
    public async Task<(AutomationInfo? Info, string? Error)> CreateAsync(
        AutomationUpsert req, Guid ownerUserId, Guid? orgId,
        CancellationToken ct)
    {
        var kind = req.ScheduleKind?.Trim().ToLowerInvariant() ?? "";
        var autoKind = req.Kind?.Trim().ToLowerInvariant()
            ?? AutomationKinds.Task;
        var error = Validate(req.Name, req.Prompt, kind,
            req.ScheduleTimeUtc, req.IntervalMinutes, req.NextRunAt,
            autoKind, req.MisfirePolicy?.Trim().ToLowerInvariant());
        if (error is not null)
        {
            return (null, error);
        }

        var (scanConfig, scanErr) = BuildScanConfig(autoKind, req);
        if (scanErr is not null)
        {
            return (null, scanErr);
        }

        var misfirePolicy = req.MisfirePolicy?.Trim().ToLowerInvariant()
            ?? AutomationMisfirePolicies.RunOnce;
        // SPEC-162: cron/timezone/approval preset.
        var cronExpression = kind == AutomationScheduleKinds.Cron
            ? req.CronExpression?.Trim()
            : null;
        var timeZoneId = string.IsNullOrWhiteSpace(req.TimeZoneId)
            ? "UTC"
            : req.TimeZoneId.Trim();
        var approvalPreset = req.ApprovalPreset?.Trim().ToLowerInvariant();
        var advancedError = ValidateScheduleAdvanced(
            kind, cronExpression, approvalPreset);
        if (advancedError is not null)
        {
            return (null, advancedError);
        }

        var now = DateTimeOffset.UtcNow;
        var rec = new AutomationRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            CreatedByUserId = ownerUserId.ToString(),
            Name = req.Name!.Trim(),
            Prompt = req.Prompt!,
            LlmModel = req.LlmModel,
            LlmProfileId = req.LlmProfileId,
            AgentProfileId = req.AgentProfileId,
            SelectedRepository = req.SelectedRepository,
            Kind = autoKind,
            ScanConfigJson = scanConfig,
            MisfirePolicy = misfirePolicy,
            TimeZoneId = timeZoneId,
            CronExpression = cronExpression,
            ApprovalPreset = approvalPreset,
            ScheduleKind = kind,
            ScheduleTimeUtc = req.ScheduleTimeUtc,
            IntervalMinutes = req.IntervalMinutes ?? 0,
            Weekday = req.Weekday,
            Enabled = req.Enabled ?? true,
            NextRunAt = req.NextRunAt,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Reschedule(rec, now);
        db.Automations.Add(rec);
        await db.SaveChangesAsync(ct);
        return (ToInfo(rec), null);
    }

    /// <inheritdoc />
    public async Task<(AutomationInfo? Info, string? Error)> UpdateAsync(
        Guid id, AutomationUpsert req, Guid ownerUserId,
        CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var rec = await db.Automations.FirstOrDefaultAsync(
            a => a.Id == id && a.CreatedByUserId == owner, ct);
        if (rec is null)
        {
            return (null, "not found");
        }

        var kind = req.ScheduleKind?.Trim().ToLowerInvariant() ?? rec.ScheduleKind;
        var autoKind = req.Kind?.Trim().ToLowerInvariant() ?? rec.Kind;
        var time = req.ScheduleTimeUtc ?? rec.ScheduleTimeUtc;
        var interval = req.IntervalMinutes ?? rec.IntervalMinutes;
        var next = req.NextRunAt ?? rec.NextRunAt;
        var error = Validate(
            req.Name ?? rec.Name, req.Prompt ?? rec.Prompt, kind, time,
            interval, next, autoKind,
            req.MisfirePolicy?.Trim().ToLowerInvariant());
        if (error is not null)
        {
            return (null, error);
        }

        // SPEC-162: cron/timezone/approval preset (merge com o gravado).
        var cronExpression = kind == AutomationScheduleKinds.Cron
            ? (req.CronExpression ?? rec.CronExpression)?.Trim()
            : null;
        var timeZoneId = req.TimeZoneId is { } tz && !string.IsNullOrWhiteSpace(tz)
            ? tz.Trim()
            : rec.TimeZoneId;
        var approvalPreset = req.ApprovalPreset is not null
            ? req.ApprovalPreset.Trim().ToLowerInvariant()
            : rec.ApprovalPreset;
        var advancedError = ValidateScheduleAdvanced(
            kind, cronExpression, approvalPreset);
        if (advancedError is not null)
        {
            return (null, advancedError);
        }

        if (req.Name is not null) { rec.Name = req.Name.Trim(); }
        if (req.Prompt is not null) { rec.Prompt = req.Prompt; }
        if (req.LlmModel is not null) { rec.LlmModel = req.LlmModel; }
        if (req.LlmProfileId.HasValue) { rec.LlmProfileId = req.LlmProfileId; }
        if (req.AgentProfileId.HasValue) { rec.AgentProfileId = req.AgentProfileId; }
        if (req.SelectedRepository is not null)
        {
            rec.SelectedRepository = req.SelectedRepository;
        }

        rec.Kind = autoKind;
        if (autoKind == AutomationKinds.Scan)
        {
            var (scanConfig, scanErr) = BuildScanConfig(
                autoKind, req, rec);
            if (scanErr is not null)
            {
                return (null, scanErr);
            }

            rec.ScanConfigJson = scanConfig;
        }

        rec.ScheduleKind = kind;
        rec.ScheduleTimeUtc = time;
        rec.IntervalMinutes = interval;
        rec.TimeZoneId = timeZoneId;
        rec.CronExpression = cronExpression;
        rec.ApprovalPreset = approvalPreset;
        if (req.Weekday is not null) { rec.Weekday = req.Weekday; }
        if (req.NextRunAt is not null) { rec.NextRunAt = req.NextRunAt; }
        if (req.MisfirePolicy is not null)
        {
            rec.MisfirePolicy = req.MisfirePolicy.Trim().ToLowerInvariant();
        }

        // SPEC-161 RF-004: re-enable manual zera o circuit breaker.
        if (req.Enabled == true && !rec.Enabled)
        {
            rec.ConsecutiveFailures = 0;
        }

        rec.Enabled = req.Enabled ?? rec.Enabled;
        rec.UpdatedAt = DateTimeOffset.UtcNow;
        Reschedule(rec, rec.UpdatedAt);
        await db.SaveChangesAsync(ct);
        return (ToInfo(rec), null);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var rec = await db.Automations.FirstOrDefaultAsync(
            a => a.Id == id && a.CreatedByUserId == owner, ct);
        if (rec is null)
        {
            return false;
        }

        db.Automations.Remove(rec);
        await db.AutomationRuns
            .Where(r => r.AutomationId == id).ExecuteDeleteAsync(ct);
        await db.AutomationHooks
            .Where(h => h.AutomationId == id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<(AutomationRunInfo? Run, bool Fired)> RunNowAsync(
        Guid id, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var rec = await db.Automations.FirstOrDefaultAsync(
            a => a.Id == id && a.CreatedByUserId == owner, ct);
        if (rec is null)
        {
            return (null, false);
        }

        // SPEC-161 RF-004: disparo manual zera o circuit breaker.
        rec.ConsecutiveFailures = 0;
        var run = await FireAsync(rec, actor: "user", ip: null, ct);
        return (ToRunInfo(run), run.Status != "skipped");
    }

    /// <summary>
    /// Núcleo do disparo: guard de concorrência + start-task como owner
    /// + run row + reagenda + audit (SPEC-47 RF-002/RF-006). SPEC-161:
    /// retry de falhas transitórias (RF-003) e circuit breaker (RF-004)
    /// acontecem antes do save final — <paramref name="missed"/> marca
    /// runs recuperados de misfire (RF-002).
    /// </summary>
    internal async Task<AutomationRunRecord> FireAsync(
        AutomationRecord rec, string actor, string? ip,
        CancellationToken ct, bool missed = false,
        AutomationHookDeliveryRecord? hookDelivery = null)
    {
        var now = DateTimeOffset.UtcNow;

        // Concurrency guard: nunca 2 runs simultâneas da mesma automation.
        var hasRunning = await db.AutomationRuns.AnyAsync(
            r => r.AutomationId == rec.Id && r.Status == "running", ct);
        if (hasRunning)
        {
            var skipped = new AutomationRunRecord
            {
                Id = Guid.NewGuid(),
                AutomationId = rec.Id,
                StartedAt = now,
                StartedAtTicks = now.UtcTicks,
                FinishedAt = now,
                Status = "skipped",
                Error = "previous run still running",
            };
            db.AutomationRuns.Add(skipped);
            await db.SaveChangesAsync(ct);
            logger?.LogInformation(
                "automation {Id} skipped — previous run still running", rec.Id);
            return skipped;
        }

        var run = new AutomationRunRecord
        {
            Id = Guid.NewGuid(),
            AutomationId = rec.Id,
            StartedAt = now,
            StartedAtTicks = now.UtcTicks,
            Status = "running",
            Missed = missed,
        };
        db.AutomationRuns.Add(run);
        if (hookDelivery is not null)
        {
            hookDelivery.RunId = run.Id;
        }

        // Persiste running antes da chamada externa: o concurrency guard
        // passa a valer também para disparos simultâneos e um crash não
        // perde o rastro do run.
        await db.SaveChangesAsync(ct);

        // SPEC-161 RF-003: retry só para falhas transitórias; erro
        // permanente (quota/validação) falha na hora — nunca retry duplo
        // de LLM. O run é criado uma vez; as tentativas acontecem aqui.
        var maxAttempts = Math.Max(1, _options.MaxTransientAttempts);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await FireCoreAsync(rec, run, ct);
                break;
            }
            catch (Exception ex) when (!AutomationFailureClassifier
                .IsHostCancellation(ex, ct))
            {
                if (attempt >= maxAttempts
                    || !AutomationFailureClassifier.IsTransient(ex))
                {
                    run.Status = RunStatusFailed;
                    run.FinishedAt = DateTimeOffset.UtcNow;
                    // RF-006: quota negada → error=quota prefixado.
                    run.Error = ex is PlanLimitExceededException
                        ? $"quota: {ex.Message}"
                        : ex.Message;
                    logger?.LogWarning(ex,
                        "automation {Id} fire failed (attempt {Attempt})",
                        rec.Id, attempt);
                    break;
                }

                logger?.LogInformation(ex,
                    "automation {Id} transient failure (attempt {Attempt}) — retrying",
                    rec.Id, attempt);
                await Task.Delay(RetryDelay(attempt), ct);
            }
        }

        // SPEC-161 RF-004: breaker conta o desfecho imediato (falha de
        // criação ou sucesso de scan); runs task ficam running e o
        // reconcile aplica o desfecho final da conversa.
        var circuitOpened = ApplyBreakerOutcome(rec, run.Status);

        rec.LastRunAt = now;
        // once → esgota após o primeiro disparo.
        if (rec.ScheduleKind == AutomationScheduleKinds.Once)
        {
            rec.Enabled = false;
            rec.NextRunAt = null;
        }
        else
        {
            Reschedule(rec, now);
        }
        rec.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (audit is not null)
        {
            await audit.RecordAsync("automation_fired",
                actorUserId: actor == "user" ? rec.CreatedByUserId : null,
                orgId: rec.OrgId, targetType: AuditTargetAutomation,
                targetId: rec.Id.ToString(), ip: ip,
                metadata: new
                {
                    run_id = run.Id,
                    run.Status,
                    run.ConversationId,
                    actor,
                }, cancellationToken: CancellationToken.None);
            await audit.RecordAsync("automation_run_created",
                actorUserId: rec.CreatedByUserId, orgId: rec.OrgId,
                targetType: "automation_run", targetId: run.Id.ToString(),
                ip: ip, metadata: new
                {
                    rec.Name,
                    run.Status,
                    run.ConversationId,
                    actor,
                }, cancellationToken: CancellationToken.None);
        }

        // SPEC-161 RF-004: circuit aberto → audit + notificação ao dono.
        if (circuitOpened && audit is not null)
        {
            await audit.RecordAsync("automation_circuit_open",
                actorUserId: rec.CreatedByUserId, orgId: rec.OrgId,
                targetType: AuditTargetAutomation, targetId: rec.Id.ToString(),
                ip: ip, metadata: new
                {
                    rec.Name,
                    rec.ConsecutiveFailures,
                    threshold = _options.CircuitBreakerThreshold,
                }, cancellationToken: CancellationToken.None);
        }

        if (circuitOpened && notifications is not null
            && Guid.TryParse(rec.CreatedByUserId, out var ownerId))
        {
            await notifications.NotifyAutomationDisabledAsync(
                ownerId, rec.Id, rec.Name, rec.ConsecutiveFailures,
                CancellationToken.None);
        }

        return run;
    }

    /// <summary>
    /// Tentativa única de disparo (scan ou conversa start-task) — lança
    /// em falha; o retry é decidido pelo caller (RF-003).
    /// </summary>
    private async Task FireCoreAsync(
        AutomationRecord rec, AutomationRunRecord run, CancellationToken ct)
    {
        var ownerId = Guid.Parse(rec.CreatedByUserId);
        if (rec.Kind == AutomationKinds.Scan && scans is not null)
        {
            // SPEC-143: kind=scan → POST /scans equivalente. O run
            // resolve imediato (succeeded) — o scan tem lifecycle
            // próprio (queued → running → completed).
            var cfg = AutomationScanConfig.Parse(rec.ScanConfigJson)
                ?? throw new InvalidOperationException(
                    "scan automation missing scan_config_json");
            var (created, scanErr) = await scans.CreateAsync(
                new Application.Scans.CreateScanRequest(
                    cfg.Repositories, rec.Prompt,
                    cfg.Profile, cfg.Effort),
                ownerId, rec.OrgId,
                Application.Scans.ScanTriggers.Automation, ct);
            if (created is null)
            {
                throw new InvalidOperationException(
                    scanErr ?? "scan creation failed");
            }

            run.ConversationId = created.CoordinatorConversationId;
            run.Status = "succeeded";
            run.FinishedAt = DateTimeOffset.UtcNow;
            return;
        }

        // SPEC-162 RF-004: approval preset → permissões/modo da conversa.
        var (mode, permissions) = MapApprovalPreset(rec.ApprovalPreset);
        var task = await conversations.CreateForUserAsync(
            ownerId, rec.OrgId,
            new CreateConversationRequest(
                new InitialMessage("user", [rec.Prompt], Run: true),
                rec.SelectedRepository, null, rec.LlmModel, null,
                Mode: mode, Permissions: permissions,
                LlmProfileId: rec.LlmProfileId,
                AgentProfileId: rec.AgentProfileId),
            ct);
        run.ConversationId = task.AppConversationId;
        run.FinishedAt = DateTimeOffset.UtcNow;

        // SPEC-47 RF-006: origem automation:{id} no stream da conversa.
        if (run.ConversationId is { } convId)
        {
            await events.AppendAsync(convId, new AgentDiagnosticEvent
            {
                Severity = "info",
                Code = "automation",
                Message = $"fired by automation:{rec.Id} ({rec.Name})",
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// SPEC-161 RF-003: backoff exponencial + jitter full —
    /// <c>base × 2^(attempt-1)</c> com teto, randomizado em [0, teto].
    /// </summary>
    private TimeSpan RetryDelay(int attempt)
    {
        var capMs = Math.Min(
            _options.RetryBaseDelayMs * Math.Pow(2, attempt - 1),
            _options.RetryMaxDelayMs);
        return TimeSpan.FromMilliseconds(
            Random.Shared.NextDouble() * capMs);
    }

    /// <summary>
    /// SPEC-161 RF-004: aplica o desfecho ao contador do breaker.
    /// Retorna <see langword="true"/> quando o circuito abre nesta
    /// transição (threshold atingido e automation ainda habilitada).
    /// </summary>
    private bool ApplyBreakerOutcome(AutomationRecord rec, string outcome)
    {
        if (outcome == "succeeded")
        {
            rec.ConsecutiveFailures = 0;
            return false;
        }

        if (outcome != RunStatusFailed)
        {
            return false;
        }

        rec.ConsecutiveFailures++;
        if (rec.Enabled
            && rec.ConsecutiveFailures >= _options.CircuitBreakerThreshold)
        {
            rec.Enabled = false;
            rec.NextRunAt = null;
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AutomationRunInfo>> ListRunsAsync(
        Guid id, Guid ownerUserId, int take, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var owned = await db.Automations.AsNoTracking().AnyAsync(
            a => a.Id == id && a.CreatedByUserId == owner, ct);
        if (!owned)
        {
            return [];
        }

        var rows = await db.AutomationRuns.AsNoTracking()
            .Where(r => r.AutomationId == id)
            .OrderByDescending(r => r.StartedAtTicks)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);
        return rows.Select(ToRunInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AutomationRunInfo>> ListAllRunsAsync(
        Guid ownerUserId, DateTimeOffset? from, DateTimeOffset? to,
        int take, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var ownedIds = db.Automations.AsNoTracking()
            .Where(a => a.CreatedByUserId == owner)
            .Select(a => a.Id);
        var q = db.AutomationRuns.AsNoTracking()
            .Where(r => ownedIds.Contains(r.AutomationId));
        // SQLite não traduz comparação de DateTimeOffset — filtra pela
        // coluna espelho em ticks (StartedAtTicks = StartedAt.UtcTicks).
        if (from is not null)
        {
            var fromTicks = from.Value.UtcTicks;
            q = q.Where(r => r.StartedAtTicks >= fromTicks);
        }
        if (to is not null)
        {
            var toTicks = to.Value.UtcTicks;
            q = q.Where(r => r.StartedAtTicks <= toTicks);
        }
        var rows = await q.OrderByDescending(r => r.StartedAtTicks)
            .Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return rows.Select(ToRunInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<(AutomationHookInfo? Hook, string? Token)> CreateHookAsync(
        Guid automationId, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var owned = await db.Automations.AnyAsync(
            a => a.Id == automationId && a.CreatedByUserId == owner, ct);
        if (!owned)
        {
            return (null, null);
        }

        // Prefixo identificável p/ secret-scanning; hash cobre a string
        // completa apresentada no trigger.
        var token = "sc_hook_" + Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var rec = new AutomationHookRecord
        {
            Id = Guid.NewGuid(),
            AutomationId = automationId,
            TokenHash = HashToken(token),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.AutomationHooks.Add(rec);
        await db.SaveChangesAsync(ct);
        return (new AutomationHookInfo(
            rec.Id, rec.AutomationId, rec.CreatedAt, rec.LastUsedAt), token);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteHookAsync(
        Guid hookId, Guid ownerUserId, CancellationToken ct)
    {
        var owner = ownerUserId.ToString();
        var hook = await db.AutomationHooks.FirstOrDefaultAsync(
            h => h.Id == hookId, ct);
        if (hook is null)
        {
            return false;
        }

        var owned = await db.Automations.AnyAsync(
            a => a.Id == hook.AutomationId && a.CreatedByUserId == owner, ct);
        if (!owned)
        {
            return false;
        }

        db.AutomationHooks.Remove(hook);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<AutomationHookFireResult?> FireByTokenAsync(
        string token, string? ip, string? idempotencyKey,
        string? payloadHash, CancellationToken ct)
    {
        var hook = await db.AutomationHooks.FirstOrDefaultAsync(
            h => h.TokenHash == HashToken(token), ct);
        if (hook is null)
        {
            return null;
        }

        var rec = await db.Automations.FirstOrDefaultAsync(
            a => a.Id == hook.AutomationId, ct);
        if (rec is null)
        {
            return null;
        }

        // SPEC-163 RF-004: dedup — chave = header Idempotency-Key ou
        // hash do payload; janela de 24h (expiradas são removidas antes,
        // o que libera a chave única para reuso após a janela).
        var key = string.IsNullOrWhiteSpace(idempotencyKey)
            ? payloadHash
            : idempotencyKey.Trim();
        if (key is { Length: > 128 })
        {
            key = key[..128];
        }

        if (!string.IsNullOrEmpty(key))
        {
            var now = DateTimeOffset.UtcNow;
            var windowStart = now.AddHours(-24);
            // Limpa expiradas em SQL (sem carregar histórico) — o índice
            // único (hook, key) precisa da linha antiga fora antes do claim.
            await db.AutomationHookDeliveries
                .Where(d => d.HookId == hook.Id
                    && d.ReceivedAtTicks < windowStart.UtcTicks)
                .ExecuteDeleteAsync(ct);

            var dup = await db.AutomationHookDeliveries
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    d => d.HookId == hook.Id && d.IdempotencyKey == key
                        && d.ReceivedAtTicks >= windowStart.UtcTicks, ct);
            if (dup is not null)
            {
                return new AutomationHookFireResult(null, true, dup.RunId);
            }

            // Claim antes do disparo: retries concorrentes do mesmo webhook
            // enxergam a entrega mesmo enquanto a primeira ainda executa.
            var delivery = new AutomationHookDeliveryRecord
            {
                Id = Guid.NewGuid(),
                HookId = hook.Id,
                IdempotencyKey = key,
                PayloadHash = payloadHash ?? string.Empty,
                ReceivedAt = now,
                ReceivedAtTicks = now.UtcTicks,
            };
            db.AutomationHookDeliveries.Add(delivery);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.Entry(delivery).State = EntityState.Detached;
                dup = await db.AutomationHookDeliveries
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        d => d.HookId == hook.Id && d.IdempotencyKey == key
                            && d.ReceivedAtTicks >= windowStart.UtcTicks, ct);
                if (dup is not null)
                {
                    return new AutomationHookFireResult(
                        null, true, dup.RunId);
                }

                throw;
            }

            hook.LastUsedAt = now;
            var fired = await FireAsync(rec, actor: "hook", ip: ip, ct,
                hookDelivery: delivery);
            await db.SaveChangesAsync(ct);
            return new AutomationHookFireResult(
                ToRunInfo(fired), false, fired.Id);
        }

        hook.LastUsedAt = DateTimeOffset.UtcNow;
        var run = await FireAsync(rec, actor: "hook", ip: ip, ct);
        await db.SaveChangesAsync(ct);
        return new AutomationHookFireResult(ToRunInfo(run), false, run.Id);
    }

    /// <inheritdoc />
    public async Task FireScheduledAsync(
        Guid automationId, DateTimeOffset now, CancellationToken ct)
    {
        AutomationRecord? rec = null;
        try
        {
            rec = await db.Automations.FirstOrDefaultAsync(
                a => a.Id == automationId && a.Enabled, ct);
            if (rec is null)
            {
                return;
            }

            // SPEC-161 RF-002: misfire — NextRunAt além da graça.
            var graceTicks = TimeSpan
                .FromSeconds(_options.MisfireGraceSec).Ticks;
            var isMissed = rec.NextRunAtTicks is { } due
                && now.UtcTicks - due > graceTicks;
            if (isMissed
                && rec.MisfirePolicy == AutomationMisfirePolicies.Skip)
            {
                // Política skip: registra run skipped/missed sem disparar
                // conversa e reagenda do now.
                var skipped = new AutomationRunRecord
                {
                    Id = Guid.NewGuid(),
                    AutomationId = rec.Id,
                    StartedAt = now,
                    StartedAtTicks = now.UtcTicks,
                    FinishedAt = now,
                    Status = "skipped",
                    Missed = true,
                    Error = "missed schedule (misfire_policy=skip)",
                };
                db.AutomationRuns.Add(skipped);
                Reschedule(rec, now);
                rec.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                logger?.LogInformation(
                    "automation {Id} misfire skipped (policy=skip)", rec.Id);
                return;
            }

            await FireAsync(rec, actor: "schedule", ip: null, ct,
                missed: isMissed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Falha numa automation não bloqueia as demais (RF-005 NFR) —
            // reagenda do now para não entrar em hot-loop no próximo tick.
            logger?.LogWarning(ex,
                "automation {Id} tick fire failed", automationId);
            try
            {
                if (rec is not null)
                {
                    Reschedule(rec, now);
                    rec.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception inner)
                when (inner is not OperationCanceledException)
            {
                logger?.LogWarning(inner,
                    "automation {Id} reschedule failed", automationId);
            }
        }
    }

    /// <inheritdoc />
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var ticks = now.UtcTicks;
        var dueIds = await db.Automations.AsNoTracking()
            .Where(a => a.Enabled && a.NextRunAtTicks != null
                && a.NextRunAtTicks <= ticks)
            .Select(a => a.Id)
            .ToListAsync(ct);

        // SPEC-161 RF-001: dispatch paralelo limitado — cada automação
        // roda no próprio scope/DbContext (EF não é thread-safe). Sem
        // scope factory (testes unitários) cai no caminho sequencial.
        if (scopes is null || dueIds.Count <= 1)
        {
            foreach (var id in dueIds)
            {
                await FireScheduledAsync(id, now, ct);
            }
        }
        else
        {
            var maxConcurrent = Math.Max(1, _options.MaxConcurrent);
            await Parallel.ForEachAsync(dueIds,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxConcurrent,
                    CancellationToken = ct,
                },
                async (id, token) =>
                    await FireScheduledInScopeAsync(id, now, token));
        }

        await ReconcileRunningAsync(ct);
    }

    /// <summary>
    /// SPEC-161 RF-001: dispara uma devida em scope próprio (DbContext
    /// isolado por automação — EF Core não permite uso concorrente do
    /// mesmo contexto).
    /// </summary>
    private async Task FireScheduledInScopeAsync(
        Guid automationId, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes!.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAutomationService>();
        await svc.FireScheduledAsync(automationId, now, ct);
    }

    // Reconcile: runs running → status da conversa linkada.
    private async Task ReconcileRunningAsync(CancellationToken ct)
    {
        var running = await db.AutomationRuns
            .Where(r => r.Status == "running").ToListAsync(ct);
        if (running.Count == 0)
        {
            return;
        }

        var convIds = running.Where(r => r.ConversationId != null)
            .Select(r => r.ConversationId!.Value).ToList();
        var statuses = await db.Conversations.AsNoTracking()
            .Where(c => convIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ExecutionStatus })
            .ToDictionaryAsync(x => x.Id, x => x.ExecutionStatus, ct);
        var changed = false;
        // SPEC-161 RF-004: desfecho final da conversa alimenta o breaker —
        // no máximo um run running por automation (overlap guard), então
        // um outcome por automation por tick.
        var outcomes = new Dictionary<Guid, string>();
        foreach (var r in running)
        {
            if (r.ConversationId is not { } cid
                || !statuses.TryGetValue(cid, out var st))
            {
                continue;
            }

            var done = st switch
            {
                nameof(ConversationExecutionStatus.Finished) => "succeeded",
                nameof(ConversationExecutionStatus.Error)
                    or nameof(ConversationExecutionStatus.Archived) =>
                    RunStatusFailed,
                _ => null,
            };
            if (done is null)
            {
                continue;
            }

            r.Status = done;
            r.FinishedAt ??= DateTimeOffset.UtcNow;
            if (done == RunStatusFailed && r.Error is null)
            {
                r.Error = $"conversation {st.ToLowerInvariant()}";
            }

            outcomes[r.AutomationId] = done;
            changed = true;
        }
        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }

        if (outcomes.Count > 0)
        {
            await ApplyReconciledOutcomesAsync(outcomes, ct);
        }
    }

    /// <summary>
    /// SPEC-161 RF-004: aplica os desfechos reconciliados ao breaker de
    /// cada automation — sucesso zera, falha incrementa e, no limiar,
    /// desabilita + audit <c>automation_circuit_open</c> + notificação.
    /// </summary>
    private async Task ApplyReconciledOutcomesAsync(
        IReadOnlyDictionary<Guid, string> outcomes, CancellationToken ct)
    {
        var automations = await db.Automations
            .Where(a => outcomes.Keys.Contains(a.Id))
            .ToListAsync(ct);
        var opened = new List<AutomationRecord>();
        var dirty = false;
        foreach (var a in automations)
        {
            if (!outcomes.TryGetValue(a.Id, out var outcome))
            {
                continue;
            }

            if (ApplyBreakerOutcome(a, outcome))
            {
                opened.Add(a);
            }

            dirty = true;
        }

        if (!dirty)
        {
            return;
        }

        foreach (var a in automations)
        {
            a.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        foreach (var a in opened)
        {
            if (audit is not null)
            {
                await audit.RecordAsync("automation_circuit_open",
                    actorUserId: a.CreatedByUserId, orgId: a.OrgId,
                    targetType: AuditTargetAutomation, targetId: a.Id.ToString(),
                    metadata: new
                    {
                        a.Name,
                        a.ConsecutiveFailures,
                        threshold = _options.CircuitBreakerThreshold,
                        source = "reconcile",
                    }, cancellationToken: CancellationToken.None);
            }

            if (notifications is not null
                && Guid.TryParse(a.CreatedByUserId, out var ownerId))
            {
                await notifications.NotifyAutomationDisabledAsync(
                    ownerId, a.Id, a.Name, a.ConsecutiveFailures,
                    CancellationToken.None);
            }
        }
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();
}