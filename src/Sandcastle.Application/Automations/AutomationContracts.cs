namespace Sandcastle.Application.Automations;

/// <summary>Schedule kinds aceitos (SPEC-47 RF-001).</summary>
public static class AutomationScheduleKinds
{
    /// <summary>A cada N minutos.</summary>
    public const string Interval = "interval";
    /// <summary>Diariamente no horário UTC.</summary>
    public const string Daily = "daily";
    /// <summary>Toda semana no weekday + horário UTC.</summary>
    public const string Weekly = "weekly";
    /// <summary>Execução única — desabilita após disparar.</summary>
    public const string Once = "once";
    /// <summary>Expressão cron de 5 campos + <c>TimeZoneId</c> (SPEC-162).</summary>
    public const string Cron = "cron";

    /// <summary>Todos os kinds válidos.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Interval, Daily, Weekly, Once, Cron };
}

/// <summary>Automation kinds aceitos (SPEC-143 RF-004).</summary>
public static class AutomationKinds
{
    /// <summary>Dispara uma conversa com o prompt (default).</summary>
    public const string Task = "task";

    /// <summary>Dispara um code scan (POST /scans equivalente).</summary>
    public const string Scan = "scan";

    /// <summary>Todos os kinds válidos.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Task, Scan };
}

/// <summary>Approval presets da conversa disparada (SPEC-162 RF-004).</summary>
public static class AutomationApprovalPresets
{
    /// <summary>Default do usuário (sem regras extras).</summary>
    public const string Allow = "allow";
    /// <summary>Ask em tools de escrita (file_write/file_edit/git_commit).</summary>
    public const string AllowReadonly = "allow-readonly";
    /// <summary>Conversa criada em plan mode.</summary>
    public const string Plan = "plan";

    /// <summary>Todos os presets válidos.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        { Allow, AllowReadonly, Plan };
}

/// <summary>Misfire policies aceitas (SPEC-161 RF-002).</summary>
public static class AutomationMisfirePolicies
{
    /// <summary>Dispara uma vez mesmo atrasada e reagenda do now.</summary>
    public const string RunOnce = "run-once";

    /// <summary>Pula o disparo atrasado (run skipped + missed=true).</summary>
    public const string Skip = "skip";

    /// <summary>Todos os policies válidos.</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { RunOnce, Skip };
}

/// <summary>Automation DTO (SPEC-47 RF-003).</summary>
public sealed record AutomationInfo(
    Guid Id, Guid? OrgId, string? CreatedByUserId, string Name,
    string Prompt, string? LlmModel, string? SelectedRepository,
    string ScheduleKind, string? ScheduleTimeUtc, int IntervalMinutes,
    int? Weekday, bool Enabled, DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // SPEC-143: task|scan + config do scan (null para task).
    string? Kind = null, string? ScanConfigJson = null,
    // SPEC-161: circuit breaker + misfire policy.
    int ConsecutiveFailures = 0, bool CircuitOpen = false,
    string? MisfirePolicy = null,
    // SPEC-162: cron + timezone + approval preset.
    string? TimeZoneId = null, string? CronExpression = null,
    string? ApprovalPreset = null,
    // SPEC-163 RF-002: saúde derivada (green|yellow|red).
    string? Health = null);

/// <summary>Run de uma automation (SPEC-47 RF-002/RF-003).</summary>
public sealed record AutomationRunInfo(
    Guid Id, Guid AutomationId, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, string Status, Guid? ConversationId,
    string? Error,
    // SPEC-161 RF-002: disparo recuperado de misfire (fora da graça).
    bool Missed = false);

/// <summary>Níveis de saúde de uma automation (SPEC-163 RF-002).</summary>
public static class AutomationHealth
{
    /// <summary>Taxa de sucesso ≥90% e sem falhas consecutivas.</summary>
    public const string Green = "green";

    /// <summary>Taxa 70–90% ou 1–2 falhas consecutivas.</summary>
    public const string Yellow = "yellow";

    /// <summary>Taxa &lt;70%, ≥3 falhas consecutivas ou circuit-open.</summary>
    public const string Red = "red";

    /// <summary>
    /// Classifica a saúde: <paramref name="successRate"/> é a fração
    /// (0..1) de sucesso na janela recente; circuit-open força red.
    /// </summary>
    public static string Level(
        double? successRate, int consecutiveFailures, bool circuitOpen)
    {
        if (circuitOpen || consecutiveFailures >= 3)
        {
            return Red;
        }

        if (consecutiveFailures is 1 or 2)
        {
            return Yellow;
        }

        if (successRate is null)
        {
            return Green;
        }

        return successRate >= 0.90 ? Green
            : successRate >= 0.70 ? Yellow
            : Red;
    }
}

/// <summary>Stats agregadas de uma automation (SPEC-163 RF-001).</summary>
public sealed record AutomationStats(
    Guid AutomationId, long Total, long Succeeded, long Failed,
    long Skipped, double? SuccessRateLast20, string? LastError,
    int ConsecutiveFailures, string Health,
    IReadOnlyList<AutomationRunInfo> LastRuns);

/// <summary>Resultado do trigger por webhook com idempotência (SPEC-163 RF-004).</summary>
public sealed record AutomationHookFireResult(
    AutomationRunInfo? Run, bool Deduplicated, Guid? RunId);

/// <summary>Hook de trigger externo (token exibido uma única vez).</summary>
public sealed record AutomationHookInfo(
    Guid Id, Guid AutomationId, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt);

/// <summary>Payload de criação/edição (SPEC-47 RF-003).</summary>
public sealed record AutomationUpsert(
    string? Name, string? Prompt, string? LlmModel,
    string? SelectedRepository, string? ScheduleKind,
    string? ScheduleTimeUtc, int? IntervalMinutes, int? Weekday,
    bool? Enabled, DateTimeOffset? NextRunAt,
    // SPEC-143: kind=scan — prompt vira o goal; repos/profile/effort
    // alimentam ScanConfigJson (Repositories cai p/ SelectedRepository
    // quando omitido).
    string? Kind = null, IReadOnlyList<string>? Repositories = null,
    string? ScanProfile = null, string? ScanEffort = null,
    // SPEC-161 RF-002: skip|run-once (default run-once).
    string? MisfirePolicy = null,
    // SPEC-162: cron + timezone IANA + approval preset.
    string? TimeZoneId = null, string? CronExpression = null,
    string? ApprovalPreset = null);

/// <summary>
/// Cálculo do próximo disparo (UTC) — puro e testável
/// (SPEC-47 RF-002; port do open-webui <c>AutomationSchedule</c>, MIT).
/// </summary>
public static class AutomationSchedule
{
    /// <summary>
    /// Próximo disparo a partir de <paramref name="now"/>, ou
    /// <see langword="null"/> quando a agenda está inválida/esgotada
    /// (<c>once</c> já disparada ou kind desconhecido).
    /// </summary>
    public static DateTimeOffset? ComputeNextRun(
        string scheduleKind, string? scheduleTimeUtc,
        int? intervalMinutes, int? weekday,
        DateTimeOffset? storedNextRunAt, DateTimeOffset now,
        string? cronExpression = null, string? timeZoneId = null)
    {
        var utc = now.ToUniversalTime();
        if (scheduleKind == AutomationScheduleKinds.Cron)
        {
            return NextCron(utc, cronExpression, timeZoneId);
        }

        // SPEC-162 RF-003: daily/weekly calculam no fuso da automation
        // (interval ignora timezone — já é relativo a UTC).
        var zone = scheduleKind is AutomationScheduleKinds.Daily
            or AutomationScheduleKinds.Weekly
            ? ResolveTimeZone(timeZoneId)
            : null;
        return scheduleKind switch
        {
            AutomationScheduleKinds.Interval =>
                utc.AddMinutes(Math.Max(1, intervalMinutes ?? 0)),
            AutomationScheduleKinds.Daily =>
                NextDaily(utc, scheduleTimeUtc, zone),
            AutomationScheduleKinds.Weekly =>
                NextWeekly(utc, weekday ?? 0, scheduleTimeUtc, zone),
            // Execução única: mantém o horário já armazenado enquanto
            // futuro; depois de disparar devolve null e nunca reagenda.
            AutomationScheduleKinds.Once =>
                storedNextRunAt is { } at && at > utc ? at : null,
            _ => null,
        };
    }

    /// <summary>
    /// SPEC-162 RF-001: valida a expressão cron (5 campos, Cronos).
    /// </summary>
    public static bool IsValidCronExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        try
        {
            Cronos.CronExpression.Parse(expression.Trim());
            return true;
        }
        catch (Cronos.CronFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// SPEC-162: resolve o <paramref name="timeZoneId"/> IANA; inválido
    /// ou vazio cai em UTC (guardrail — nunca derruba o tick).
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                timeZoneId.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException
            or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTimeOffset? NextCron(
        DateTimeOffset nowUtc, string? expression, string? timeZoneId)
    {
        if (!IsValidCronExpression(expression))
        {
            return null;
        }

        var cron = Cronos.CronExpression.Parse(expression!.Trim());
        return cron.GetNextOccurrence(
            nowUtc, ResolveTimeZone(timeZoneId));
    }

    private static DateTimeOffset? NextDaily(
        DateTimeOffset now, string? timeOfDay, TimeZoneInfo? zone)
    {
        if (!TryParseTime(timeOfDay, out var time))
        {
            return null;
        }

        var tz = zone ?? TimeZoneInfo.Utc;
        var localNow = TimeZoneInfo.ConvertTime(now, tz);
        var candidateLocal = localNow.Date + time.ToTimeSpan();
        var candidate = TimeZoneInfo.ConvertTimeToUtc(
            candidateLocal, tz);
        return candidate <= now
            ? TimeZoneInfo.ConvertTimeToUtc(
                candidateLocal.AddDays(1), tz)
            : candidate;
    }

    private static DateTimeOffset? NextWeekly(
        DateTimeOffset now, int weekday, string? timeOfDay,
        TimeZoneInfo? zone)
    {
        if (!TryParseTime(timeOfDay, out var time))
        {
            return null;
        }

        var tz = zone ?? TimeZoneInfo.Utc;
        var localNow = TimeZoneInfo.ConvertTime(now, tz);
        var day = (DayOfWeek)Math.Clamp(weekday, 0, 6);
        var candidateLocal = localNow.Date + time.ToTimeSpan();
        candidateLocal = candidateLocal.AddDays(
            ((int)day - (int)localNow.DayOfWeek + 7) % 7);
        var candidate = TimeZoneInfo.ConvertTimeToUtc(
            candidateLocal, tz);
        return candidate <= now
            ? TimeZoneInfo.ConvertTimeToUtc(
                candidateLocal.AddDays(7), tz)
            : candidate;
    }

    private static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out time);

    /// <summary>
    /// SPEC-160 RF-001: projeta todos os disparos previstos da agenda em
    /// [<paramref name="from"/>, <paramref name="to"/>] (UTC), iterando
    /// <see cref="ComputeNextRun"/> — sem recomputar cron no client.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> NextRuns(
        string scheduleKind, string? scheduleTimeUtc,
        int? intervalMinutes, int? weekday,
        DateTimeOffset? storedNextRunAt,
        DateTimeOffset from, DateTimeOffset to, int max = 200,
        string? cronExpression = null, string? timeZoneId = null)
    {
        var runs = new List<DateTimeOffset>();
        var cursor = from.ToUniversalTime();
        var until = to.ToUniversalTime();
        while (runs.Count < max
            && ComputeNextRun(scheduleKind, scheduleTimeUtc,
                intervalMinutes, weekday, storedNextRunAt, cursor,
                cronExpression, timeZoneId)
                is { } next)
        {
            if (next > until) { break; }
            if (runs.Count > 0 && next <= runs[^1]) { break; }
            runs.Add(next);
            cursor = next.AddTicks(1);
        }
        return runs;
    }
}

/// <summary>
/// Automation service (SPEC-47): CRUD owner-scoped, disparo (start-task
/// de conversa), histórico de runs e hooks de trigger.
/// </summary>
public interface IAutomationService
{
    /// <summary>Lista as automations do owner.</summary>
    Task<IReadOnlyList<AutomationInfo>> ListAsync(
        Guid ownerUserId, CancellationToken ct);

    /// <summary>Detalhe (owner-scoped).</summary>
    Task<AutomationInfo?> GetAsync(
        Guid id, Guid ownerUserId, CancellationToken ct);

    /// <summary>
    /// Cria (valida kind/horários/intervalo — o segundo elemento do
    /// tuple carrega a mensagem de validação quando inválido).
    /// </summary>
    Task<(AutomationInfo? Info, string? Error)> CreateAsync(
        AutomationUpsert req, Guid ownerUserId, Guid? orgId,
        CancellationToken ct);

    /// <summary>Atualiza campos + toggle Enabled (recomputa NextRunAt).</summary>
    Task<(AutomationInfo? Info, string? Error)> UpdateAsync(
        Guid id, AutomationUpsert req, Guid ownerUserId,
        CancellationToken ct);

    /// <summary>Remove a automation (runs/hooks em cascata).</summary>
    Task<bool> DeleteAsync(
        Guid id, Guid ownerUserId, CancellationToken ct);

    /// <summary>
    /// Dispara agora — cria a conversa (start-task) e grava o run.
    /// Concurrency guard: retorna <c>(null, false)</c> quando já há run
    /// <c>running</c> da mesma automation.
    /// </summary>
    Task<(AutomationRunInfo? Run, bool Fired)> RunNowAsync(
        Guid id, Guid ownerUserId, CancellationToken ct);

    /// <summary>Runs de uma automation (mais recentes primeiro).</summary>
    Task<IReadOnlyList<AutomationRunInfo>> ListRunsAsync(
        Guid id, Guid ownerUserId, int take, CancellationToken ct);

    /// <summary>
    /// SPEC-163 RF-001: stats agregadas + saúde + últimos 10 runs
    /// (owner-scoped; <see langword="null"/> = 404).
    /// </summary>
    Task<AutomationStats?> GetStatsAsync(
        Guid id, Guid ownerUserId, CancellationToken ct);

    /// <summary>Histórico global de runs do owner (janela from/to).</summary>
    Task<IReadOnlyList<AutomationRunInfo>> ListAllRunsAsync(
        Guid ownerUserId, DateTimeOffset? from, DateTimeOffset? to,
        int take, CancellationToken ct);

    /// <summary>Cria hook — retorna o token UMA vez (sha256 no banco).</summary>
    Task<(AutomationHookInfo? Hook, string? Token)> CreateHookAsync(
        Guid automationId, Guid ownerUserId, CancellationToken ct);

    /// <summary>Remove hook (owner-scoped).</summary>
    Task<bool> DeleteHookAsync(
        Guid hookId, Guid ownerUserId, CancellationToken ct);

    /// <summary>
    /// Trigger anônimo por token — resolve o hash e dispara a automation
    /// linkada (mesmo guard de concorrência). SPEC-163 RF-004:
    /// <paramref name="idempotencyKey"/> + <paramref name="payloadHash"/>
    /// deduplicam entregas em 24h. <see langword="null"/> no Run =
    /// token inválido (endpoint devolve 404, não 401 — não vazar
    /// existência).
    /// </summary>
    Task<AutomationHookFireResult?> FireByTokenAsync(
        string token, string? ip, string? idempotencyKey,
        string? payloadHash, CancellationToken ct);

    /// <summary>
    /// Dispara uma automation devida pelo tick do scheduler — aplica
    /// misfire policy (SPEC-161) e reagenda. Usado pelo dispatch
    /// paralelo do <see cref="TickAsync"/> (escopo próprio por chamada).
    /// </summary>
    Task FireScheduledAsync(
        Guid automationId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Tick do scheduler: dispara todas as automations devidas
    /// (<c>Enabled</c> e <c>NextRunAt &lt;= now</c>) — catch-up dispara uma
    /// vez e recomputa — e reconcilia runs <c>running</c> com o
    /// ExecutionStatus da conversa linkada.
    /// </summary>
    Task TickAsync(DateTimeOffset now, CancellationToken ct);
}