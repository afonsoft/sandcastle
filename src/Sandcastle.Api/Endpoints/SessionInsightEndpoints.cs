using Microsoft.AspNetCore.Mvc;

using Sandcastle.Application.Insights;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-61: Endpoints para Session Insights e diagnósticos pós-sessão.
/// </summary>
public static class SessionInsightEndpoints
{
    public static IEndpointRouteBuilder MapSessionInsightEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .WithTags("SessionInsights")
            .ExcludeFromDescription();

        // RF-002: Consulta insight de uma sessão
        group.MapGet("/app-conversations/{id:guid}/insights", async (
            Guid id,
            [FromServices] ISessionInsightService insightService,
            CancellationToken ct) =>
        {
            var insight = await insightService.GetSessionInsightAsync(id, ct);
            return insight is null ? Results.NotFound() : Results.Ok(insight);
        });

        // RF-002: Forçar reanálise da sessão
        group.MapPost("/app-conversations/{id:guid}/insights/reanalyze", async (
            Guid id,
            [FromQuery] bool force,
            [FromServices] ISessionInsightService insightService,
            CancellationToken ct) =>
        {
            try
            {
                var insight = await insightService.AnalyzeSessionAsync(id, force, ct);
                return Results.Ok(insight);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Sandcastle.Infrastructure.Insights.InsightLlmAnalysisException ex)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });

        // RF-002: Métricas e estatísticas agregadas
        group.MapGet("/insights/stats", async (
            [FromQuery] Guid? orgId,
            [FromQuery] int? days,
            [FromServices] ISessionInsightService insightService,
            CancellationToken ct) =>
        {
            var stats = await insightService.GetStatsAsync(orgId, days, ct);
            return Results.Ok(stats);
        });

        return app;
    }
}