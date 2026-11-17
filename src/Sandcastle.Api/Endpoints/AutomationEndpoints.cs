using System.Collections.Concurrent;

using Sandcastle.Api.Policies;
using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Automations;
using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Automations;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// <c>/api/v1/automations</c> — CRUD owner-scoped + run-now + histórico +
/// hooks (SPEC-47 RF-003). <c>POST /api/v1/hooks/{token}</c> é o trigger
/// anônimo rate-limited (NFR: 404 em inválido, não 401).
/// </summary>
public static class AutomationEndpoints
{
    private const string AutomationNotFound = "automation not found";
    private const string AutomationsWrite = "automations:write";
    private const string AutomationsRead = "automations:read";
    /// <summary>Maps automation + hook-trigger endpoints.</summary>
    public static IEndpointRouteBuilder MapAutomationEndpoints(
        this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/automations").WithTags("Automations");

        g.MapGet("", ListAutomationsAsync)
            .RequireAuthorization()
            .RequireScope(AutomationsRead);

        g.MapPost("",
            CreateAutomationAsync).RequireAuthorization()
            .RequireScope(AutomationsWrite);

        g.MapGet("/{id:guid}", GetIdGuidAsync)
            .RequireAuthorization()
            .RequireScope(AutomationsRead);

        g.MapPatch("/{id:guid}",
            UpdateAutomationAsync).RequireAuthorization()
            .RequireScope(AutomationsWrite);

        g.MapDelete("/{id:guid}", DeleteIdGuidAsync)
            .RequireAuthorization()
            .RequireScope(AutomationsWrite);

        g.MapPost("/{id:guid}/run-now",
            RunNowAsync).RequireAuthorization()
            .RequireScope(AutomationsWrite);

        g.MapGet("/{id:guid}/runs", GetIdGuidRunsAsync)
            .RequireAuthorization()
            .RequireScope(AutomationsRead);

        // histórico global do owner — antes de /{id} para não colidir.
        g.MapGet("/runs", GetRunsAsync)
            .RequireAuthorization()
            .RequireScope(AutomationsRead);

        // SPEC-160 RF-001: agenda projetada + runs reais no intervalo.
        g.MapGet("/schedule", GetScheduleAsync)
            .RequireAuthorization()
            .RequireScope(AutomationsRead);

        g.MapPost("/{id:guid}/hooks", PostIdGuidHooksAsync).RequireAuthorization()
            .RequireScope(AutomationsWrite);

        g.MapDelete("/hooks/{hookId:guid}", DeleteHooksHookIdGuidAsync)
            .RequireAuthorization()
            .RequireScope(AutomationsWrite);

        // Trigger anônimo — rate-limit duplo (token 10/min + IP 60/min);
        // token inválido → 404 (não vaza existência). SPEC-47 RF-003/NFR.
        app.MapPost("/api/v1/hooks/{token}",
            FireHookAsync).AllowAnonymous().WithTags("Automations");

        return app;
    }

    private static async Task<IResult> CreateAutomationAsync(
        AutomationUpsertRequest body, HttpContext ctx,
        ICurrentUserAccessor current, IEffectiveOrgResolver resolver,
        IAutomationService svc, CancellationToken ct)
    {
        if (current.User is not { } u) { return Results.Unauthorized(); }
        var org = await resolver.ResolveAsync(u,
            ctx.Request.Headers["X-Org-Id"].ToString(), ct);
        var (info, error) = await svc.CreateAsync(
            ToUpsert(body), u.UserId, org?.OrgId, ct);
        return error is not null
            ? SandcastleProblemFactory.Create(ctx, 400, error)
            : Results.Created(
                $"/api/v1/automations/{info!.Id}", ToDto(info));
    }

    private static async Task<IResult> UpdateAutomationAsync(
        Guid id, AutomationUpsertRequest body, HttpContext ctx,
        ICurrentUserAccessor current, IAutomationService svc,
        CancellationToken ct)
    {
        if (current.User is not { } u) { return Results.Unauthorized(); }
        var (info, error) = await svc.UpdateAsync(
            id, ToUpsert(body), u.UserId, ct);
        return error switch
        {
            "not found" => SandcastleProblemFactory.Create(
                ctx, 404, AutomationNotFound),
            not null => SandcastleProblemFactory.Create(ctx, 400, error),
            _ => Results.Ok(ToDto(info!)),
        };
    }

    private static async Task<IResult> RunNowAsync(
        Guid id, HttpContext ctx, ICurrentUserAccessor current,
        IAutomationService svc, CancellationToken ct)
    {
        if (current.User is not { } u) { return Results.Unauthorized(); }
        var (run, fired) = await svc.RunNowAsync(id, u.UserId, ct);
        return run switch
        {
            null => SandcastleProblemFactory.Create(
                ctx, 404, AutomationNotFound),
            { Status: "skipped" } when !fired =>
                SandcastleProblemFactory.Create(
                    ctx, 409, "automation run already in progress"),
            _ => Results.Accepted(null, new
            {
                run_id = run.Id,
                conversation_id = run.ConversationId,
                status = run.Status,
            }),
        };
    }

    private static async Task<IResult> FireHookAsync(
        string token, HttpContext ctx, IAutomationService svc,
        CancellationToken ct)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        if (!HookRateLimiter.Allow(token)
            || !HookRateLimiter.AllowIp(ip))
        {
            return SandcastleProblemFactory.Create(
                ctx, 429, "rate limit exceeded");
        }

        var run = await svc.FireByTokenAsync(token, ip, ct);
        return run is null
            ? SandcastleProblemFactory.Create(ctx, 404, "not found")
            : Results.Accepted(null, new
            {
                run_id = run.Id,
                conversation_id = run.ConversationId,
                status = run.Status,
            });
    }


    /// <summary>
    /// Token bucket in-memory — janela fixa por chave (SPEC-47 NFR:
    /// 10/min por token, 60/min por IP). Single-instance é o default;
    /// multi-replica herda o lock do scheduler como próximo passo.
    /// </summary>
    public static class HookRateLimiter
    {
        private sealed class Bucket(int limit)
        {
            private readonly int _limit = limit;
            private int _used;
            private DateTimeOffset _windowStart = DateTimeOffset.UtcNow;

            public bool TryConsume()
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _windowStart >= TimeSpan.FromMinutes(1))
                {
                    _windowStart = now;
                    _used = 0;
                }
                return ++_used <= _limit;
            }
        }

        private static readonly ConcurrentDictionary<string, Bucket> Tokens = new();
        private static readonly ConcurrentDictionary<string, Bucket> Ips = new();

        /// <summary>10 disparos/minuto por token.</summary>
        public static bool Allow(string token) =>
            Tokens.GetOrAdd(token, _ => new Bucket(10)).TryConsume();

        /// <summary>60 disparos/minuto por IP.</summary>
        public static bool AllowIp(string? ip) =>
            Ips.GetOrAdd(ip ?? "unknown", _ => new Bucket(60)).TryConsume();

        /// <summary>Test hook: limpa os buckets.</summary>
        public static void Reset()
        {
            Tokens.Clear();
            Ips.Clear();
        }
    }

    private static AutomationUpsert ToUpsert(AutomationUpsertRequest r) =>
        new(r.Name, r.Prompt, r.LlmModel, r.SelectedRepository,
            r.ScheduleKind, r.ScheduleTimeUtc, r.IntervalMinutes,
            r.Weekday, r.Enabled, r.NextRunAt,
            // SPEC-143: kind=scan + scan config.
            r.Kind, r.Repositories, r.ScanProfile, r.ScanEffort,
            // SPEC-161: misfire policy.
            r.MisfirePolicy,
            // SPEC-162: cron + timezone + approval preset.
            r.TimeZoneId, r.CronExpression, r.ApprovalPreset);

    private static object ToDto(AutomationInfo a) => new
    {
        id = a.Id,
        org_id = a.OrgId,
        created_by_user_id = a.CreatedByUserId,
        name = a.Name,
        prompt = a.Prompt,
        llm_model = a.LlmModel,
        selected_repository = a.SelectedRepository,
        kind = a.Kind ?? "task",
        scan = AutomationService.AutomationScanConfig
            .Parse(a.ScanConfigJson) is { } cfg
            ? new
            {
                repositories = cfg.Repositories,
                profile = cfg.Profile,
                effort = cfg.Effort,
            }
            : null,
        schedule_kind = a.ScheduleKind,
        schedule_time_utc = a.ScheduleTimeUtc,
        interval_minutes = a.IntervalMinutes,
        weekday = a.Weekday,
        enabled = a.Enabled,
        next_run_at = a.NextRunAt,
        last_run_at = a.LastRunAt,
        created_at = a.CreatedAt,
        updated_at = a.UpdatedAt,
        // SPEC-161: circuit breaker + misfire policy.
        consecutive_failures = a.ConsecutiveFailures,
        circuit_open = a.CircuitOpen,
        misfire_policy = a.MisfirePolicy,
        // SPEC-162: cron + timezone + approval preset.
        time_zone_id = a.TimeZoneId,
        cron_expression = a.CronExpression,
        approval_preset = a.ApprovalPreset,
    };

    private static object ToRunDto(AutomationRunInfo r) => new
    {
        id = r.Id,
        automation_id = r.AutomationId,
        started_at = r.StartedAt,
        finished_at = r.FinishedAt,
        status = r.Status,
        conversation_id = r.ConversationId,
        error = r.Error,
        // SPEC-161 RF-002.
        missed = r.Missed,
    };


    private static async Task<IResult> GetIdGuidAsync(Guid id, HttpContext ctx, ICurrentUserAccessor current, IAutomationService svc, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        return await svc.GetAsync(id, u.UserId, ct) is { } a
            ? Results.Ok(ToDto(a))
            : SandcastleProblemFactory.Create(
                ctx, 404, AutomationNotFound);

    }

    private static async Task<IResult> DeleteIdGuidAsync(Guid id, ICurrentUserAccessor current, IAutomationService svc, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        return await svc.DeleteAsync(id, u.UserId, ct)
            ? Results.NoContent()
            : Results.NotFound();

    }

    private static async Task<IResult> GetIdGuidRunsAsync(Guid id, int? take, ICurrentUserAccessor current, IAutomationService svc, CancellationToken ct)
    {
        return current.User is not { } u
            ? Results.Unauthorized()
            : Results.Ok((await svc.ListRunsAsync(
                    id, u.UserId, take ?? 50, ct))
                .Select(ToRunDto).ToList());

    }

    private static async Task<IResult> GetRunsAsync(DateTimeOffset? from, DateTimeOffset? to, int? take, ICurrentUserAccessor current, IAutomationService svc, CancellationToken ct)
    {
        return current.User is not { } u
            ? Results.Unauthorized()
            : Results.Ok((await svc.ListAllRunsAsync(
                    u.UserId, from, to, take ?? 50, ct))
                .Select(ToRunDto).ToList());

    }

    /// <summary>
    /// SPEC-160: <c>GET /automations/schedule?from&amp;to</c> — por
    /// automação com agenda: <c>next_runs[]</c> (projetados server-side)
    /// + <c>runs[]</c> reais do intervalo. 400 se janela >62d ou
    /// from&gt;to; automação sem schedule não aparece.
    /// </summary>
    private static async Task<IResult> GetScheduleAsync(
        DateTimeOffset? from, DateTimeOffset? to, HttpContext ctx,
        ICurrentUserAccessor current, IAutomationService svc,
        CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        if (from is null || to is null)
        {
            return SandcastleProblemFactory.Create(
                ctx, 400, "from and to are required");
        }

        if (from > to)
        {
            return SandcastleProblemFactory.Create(
                ctx, 400, "from must be <= to");
        }

        if (to.Value - from.Value > TimeSpan.FromDays(62))
        {
            return SandcastleProblemFactory.Create(
                ctx, 400, "interval too large (max 62 days)");
        }

        var automations = await svc.ListAsync(u.UserId, ct);
        var runs = await svc.ListAllRunsAsync(
            u.UserId, from, to, 1000, ct);
        var runsByAutomation = runs
            .GroupBy(r => r.AutomationId)
            .ToDictionary(x => x.Key, x => x.ToList());

        var result = automations
            .Where(a => a.Enabled)
            .Select(a =>
            {
                var next = AutomationSchedule.NextRuns(
                    a.ScheduleKind, a.ScheduleTimeUtc,
                    a.IntervalMinutes, a.Weekday, a.NextRunAt,
                    from.Value, to.Value,
                    cronExpression: a.CronExpression,
                    timeZoneId: a.TimeZoneId);
                runsByAutomation.TryGetValue(a.Id, out var ar);
                return new
                {
                    automation_id = a.Id,
                    name = a.Name,
                    next_runs = next,
                    runs = (ar ?? []).Select(r => new
                    {
                        id = r.Id,
                        status = r.Status,
                        started_at = r.StartedAt,
                        duration_ms = r.FinishedAt is { } f
                            ? (long?)(f - r.StartedAt)
                                .TotalMilliseconds
                            : null,
                        conversation_id = r.ConversationId,
                    }).ToList(),
                };
            })
            .Where(x => x.next_runs.Count > 0
                || x.runs.Count > 0)
            .ToList();

        return Results.Ok(result);
    }

    private static async Task<IResult> PostIdGuidHooksAsync(Guid id, HttpContext ctx, ICurrentUserAccessor current, IAutomationService svc, CancellationToken ct)
    {
        if (current.User is not { } u) { return Results.Unauthorized(); }
        var (hook, token) = await svc.CreateHookAsync(id, u.UserId, ct);
        return hook is null
            ? SandcastleProblemFactory.Create(
                ctx, 404, AutomationNotFound)
            : Results.Created(
                $"/api/v1/automations/hooks/{hook.Id}", new
                {
                    id = hook.Id,
                    automation_id = hook.AutomationId,
                    token, // exibido uma única vez — só hash no banco
                    created_at = hook.CreatedAt,
                });

    }

    private static async Task<IResult> DeleteHooksHookIdGuidAsync(Guid hookId, ICurrentUserAccessor current, IAutomationService svc, CancellationToken ct)
    {
        if (current.User is not { } u)
        {
            return Results.Unauthorized();
        }

        return await svc.DeleteHookAsync(hookId, u.UserId, ct)
            ? Results.NoContent()
            : Results.NotFound();

    }

    private static async Task<IResult> ListAutomationsAsync(
        ICurrentUserAccessor current, IAutomationService svc,
        CancellationToken ct) =>
        current.User is not { } u
            ? Results.Unauthorized()
            : Results.Ok((await svc.ListAsync(u.UserId, ct))
                .Select(ToDto).ToList());

}

/// <summary>Body JSON do CRUD de automations (snake_case).</summary>
public sealed record AutomationUpsertRequest(
    string? Name = null, string? Prompt = null, string? LlmModel = null,
    string? SelectedRepository = null, string? ScheduleKind = null,
    string? ScheduleTimeUtc = null, int? IntervalMinutes = null,
    int? Weekday = null, bool? Enabled = null,
    DateTimeOffset? NextRunAt = null,
    // SPEC-143: kind=scan — repositories[] + profile + effort.
    string? Kind = null, IReadOnlyList<string>? Repositories = null,
    string? ScanProfile = null, string? ScanEffort = null,
    // SPEC-161: misfire policy skip|run-once.
    string? MisfirePolicy = null,
    // SPEC-162: cron + timezone IANA + approval preset.
    string? TimeZoneId = null, string? CronExpression = null,
    string? ApprovalPreset = null);