using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>SPEC-179: badges de navegação e telemetria UX sem PII.</summary>
public static class NavigationEndpoints
{
    private static readonly string[] RunningStatuses =
        ["Starting", "Running"];

    /// <summary>Maps navigation shell endpoints.</summary>
    public static IEndpointRouteBuilder MapNavigationEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/navigation/summary", SummaryAsync)
            .WithTags("Navigation").RequireAuthorization()
            .Produces<NavigationSummaryResponse>();
        app.MapPost("/api/v1/ux-events", PostUxEvent)
            .WithTags("Navigation").RequireAuthorization()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return app;
    }

    private static async Task<IResult> SummaryAsync(
        AppDbContext db, ICurrentUserAccessor current, CancellationToken ct)
    {
        var userId = current.User?.UserId.ToString();
        var conversations = db.Conversations.AsNoTracking()
            .Where(c => c.ExecutionStatus != "Archived")
            .Where(c => userId == null || c.CreatedByUserId == null
                || c.CreatedByUserId == userId);
        var attention = await conversations
            .CountAsync(c => c.NeedsAttentionKind != null, ct);
        var running = await conversations
            .CountAsync(c => RunningStatuses.Contains(c.ExecutionStatus), ct);
        var automations = await db.Automations.AsNoTracking()
            .Where(a => userId == null || a.CreatedByUserId == userId)
            .CountAsync(a => a.Enabled, ct);
        var orgId = current.User?.CurrentOrgId;
        var extensions = await db.InstalledSkills.AsNoTracking()
            .CountAsync(i => i.Enabled
                && (i.Scope == "system"
                    || (i.Scope == "user" && i.ScopeId == userId)
                    || (i.Scope == "org" && orgId != null
                        && i.ScopeId == orgId)), ct);

        return Results.Ok(new NavigationSummaryResponse(
            attention, running, automations, 0, extensions));
    }

    private static IResult PostUxEvent(
        UxEventRequest body, ILoggerFactory loggerFactory)
    {
        var eventName = (body.EventName ?? "").Trim();
        if (eventName.Length is 0 or > 64
            || !eventName.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
        {
            return Results.BadRequest(new { error = "invalid event_name" });
        }

        var area = NormalizeArea(body.Route);
        var duration = Math.Clamp(body.DurationMs ?? 0, 0, 600_000);
        loggerFactory.CreateLogger("Sandcastle.Ux").LogInformation(
            "ux_event {EventName} area={RouteArea} duration_ms={DurationMs}",
            eventName, area, duration);
        return Results.Accepted();
    }

    /// <summary>Reduz a rota à área de produto; IDs/títulos nunca são logados.</summary>
    private static string NormalizeArea(string? route)
    {
        var segment = (route ?? "").Trim().Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "home";
        return segment.Length <= 32 && segment.All(
            c => char.IsLetterOrDigit(c) || c is '-' or '_')
            ? segment.ToLowerInvariant() : "unknown";
    }

    /// <summary>Contadores agregados dos badges da shell.</summary>
    public sealed record NavigationSummaryResponse(
        int AttentionCount, int RunningCount, int AutomationCount,
        int FactoryCount, int ExtensionCount);

    /// <summary>Payload de telemetria UX (route vira apenas área/categoria).</summary>
    public sealed record UxEventRequest
    {
        public string? EventName { get; init; }
        public string? Route { get; init; }
        public int? DurationMs { get; init; }
    }
}