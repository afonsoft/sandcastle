using Microsoft.AspNetCore.Mvc;

using Sandcastle.Application.Identity;
using Sandcastle.Application.Observability;

namespace Sandcastle.Api.Endpoints;

/// <summary>SPEC-185: admin analytics, budgets and per-run trace endpoints.</summary>
public static class ObservabilityEndpoints
{
    private const string PermObservabilityAdmin = "perm:manage_super_admins";

    /// <summary>Maps observability endpoints.</summary>
    public static IEndpointRouteBuilder MapObservabilityEndpoints(
        this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").WithTags("Admin");
        admin.MapGet("/analytics/summary", GetAnalyticsAsync)
            .RequireAuthorization(PermObservabilityAdmin);
        admin.MapGet("/analytics/rankings", GetRankingsAsync)
            .RequireAuthorization(PermObservabilityAdmin);
        admin.MapGet("/analytics/export", ExportAsync)
            .RequireAuthorization(PermObservabilityAdmin);
        admin.MapGet("/budgets", GetBudgetsAsync)
            .RequireAuthorization(PermObservabilityAdmin);
        admin.MapPost("/budgets/reconcile", ReconcileBudgetsAsync)
            .RequireAuthorization(PermObservabilityAdmin);

        app.MapGet("/api/v1/runs/{id:guid}/trace", GetRunTraceAsync)
            .WithTags(EndpointTags.Api)
            .RequireAuthorization();
        return app;
    }

    private static AnalyticsQuery Query(
        int days, Guid? org, string? user, string? model, string? status) =>
        new(days, org, user, model, status);

    private static async Task<IResult> GetAnalyticsAsync(
        IObservabilityAnalyticsService svc, CancellationToken ct,
        [FromQuery] int days = 30, [FromQuery] Guid? org = null,
        [FromQuery] string? user = null, [FromQuery] string? model = null,
        [FromQuery] string? status = null) =>
        Results.Ok(await svc.GetAnalyticsAsync(
            Query(days, org, user, model, status), ct));

    private static async Task<IResult> GetRankingsAsync(
        IObservabilityAnalyticsService svc, CancellationToken ct,
        [FromQuery] int days = 30, [FromQuery] Guid? org = null,
        [FromQuery] string? user = null, [FromQuery] string? model = null,
        [FromQuery] string? status = null, [FromQuery] string? by = null) =>
        Results.Ok(await svc.GetRankingsAsync(
            Query(days, org, user, model, status),
            string.IsNullOrWhiteSpace(by) ? "conversation" : by, ct));

    private static async Task<IResult> ExportAsync(
        IObservabilityAnalyticsService svc, CancellationToken ct,
        [FromQuery] int days = 30, [FromQuery] Guid? org = null,
        [FromQuery] string? user = null, [FromQuery] string? model = null,
        [FromQuery] string? status = null) =>
        Results.Text(await svc.ExportCsvAsync(
            Query(days, org, user, model, status), ct),
            "text/csv");

    private static async Task<IResult> GetBudgetsAsync(
        IObservabilityAnalyticsService svc, CancellationToken ct) =>
        Results.Ok(await svc.GetBudgetsAsync(ct));

    private static async Task<IResult> ReconcileBudgetsAsync(
        ICurrentUserAccessor current, IObservabilityAnalyticsService svc,
        CancellationToken ct)
    {
        if (current.User is not { } user) return Results.Unauthorized();
        return Results.Accepted(null, await svc.ReconcileBudgetsAsync(
            user.UserId.ToString(), periodic: false, ct));
    }

    private static async Task<IResult> GetRunTraceAsync(
        Guid id, ICurrentUserAccessor current,
        IObservabilityAnalyticsService svc, CancellationToken ct)
    {
        if (current.User is not { } user) return Results.Unauthorized();
        var trace = await svc.GetRunTraceAsync(
            id, user.UserId, user.IsSuperAdmin, ct);
        return trace is null ? Results.NotFound() : Results.Ok(trace);
    }
}
