using Sandcastle.Application.Identity;
using Sandcastle.Application.Lifecycle;

namespace Sandcastle.Api.Endpoints;

/// <summary>SPEC-190: enterprise lifecycle operations endpoints.</summary>
public static class LifecycleEndpoints
{
    private const string PermAdmin = "perm:manage_super_admins";
    private static readonly HashSet<string> InteractionEvents = new(StringComparer.Ordinal)
        { "onboarding_started", "onboarding_completed", "activation", "first_conversation", "first_pr" };

    public static IEndpointRouteBuilder MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin/lifecycle").WithTags("Admin").RequireAuthorization(PermAdmin);
        admin.MapGet("/provisioning", async (ILifecycleService svc, CancellationToken ct) => Results.Ok(await svc.ListProvisioningAsync(ct)));
        admin.MapPost("/provisioning", async (LifecycleProvisioningCreate req, ICurrentUserAccessor current, ILifecycleService svc, CancellationToken ct) =>
            Results.Created("/api/v1/admin/lifecycle/provisioning", await svc.RequestProvisioningAsync(req, current.User?.UserId.ToString(), ct)));
        admin.MapPost("/provisioning/{id:guid}/invite", async (Guid id, ICurrentUserAccessor current, ILifecycleService svc, CancellationToken ct) =>
            await svc.InviteProvisioningAsync(id, current.User?.UserId.ToString(), ct) is { } row ? Results.Ok(row) : Results.NotFound());
        admin.MapPost("/provisioning/{id:guid}/approve", async (Guid id, ICurrentUserAccessor current, ILifecycleService svc, CancellationToken ct) =>
            await svc.ApproveProvisioningAsync(id, current.User?.UserId.ToString(), ct) is { } row ? Results.Ok(row) : Results.NotFound());
        admin.MapPost("/provisioning/{id:guid}/onboarded", async (Guid id, ICurrentUserAccessor current, ILifecycleService svc, CancellationToken ct) =>
            await svc.MarkOnboardedAsync(id, current.User?.UserId.ToString(), ct) is { } row ? Results.Ok(row) : Results.NotFound());
        admin.MapGet("/outbox", async (string? status, ILifecycleService svc, CancellationToken ct) => Results.Ok(await svc.ListOutboxAsync(status, ct)));
        admin.MapPost("/outbox", async (IntegrationEventEnqueue req, ICurrentUserAccessor current, ILifecycleService svc, CancellationToken ct) =>
            Results.Created("/api/v1/admin/lifecycle/outbox", await svc.EnqueueEventAsync(req, current.User?.UserId.ToString(), ct)));
        admin.MapPost("/dead-letters/{id:guid}/replay", async (Guid id, ICurrentUserAccessor current, ILifecycleService svc, CancellationToken ct) =>
            await svc.ReplayDeadLetterAsync(id, current.User?.UserId.ToString(), ct) is { } row ? Results.Accepted(null, row) : Results.NotFound());
        admin.MapGet("/jobs", async (ILifecycleService svc, CancellationToken ct) => Results.Ok(await svc.ListJobsAsync(ct)));
        admin.MapPost("/jobs/{key}", async (string key, ICurrentUserAccessor current, ILifecycleService svc, CancellationToken ct) =>
        {
            try { return Results.Accepted(null, await svc.RunJobAsync(key, current.User?.UserId.ToString(), ct)); }
            catch (ArgumentException) { return Results.NotFound(); }
        });

        app.MapPost("/api/v1/lifecycle/interactions", async (InteractionRequest req, ICurrentUserAccessor current, IAnalyticsService? analytics, CancellationToken ct) =>
        {
            if (!InteractionEvents.Contains(req.Event)) return Results.BadRequest(new { detail = "unsupported interaction event" });
            if (analytics is not null)
            {
                await analytics.TrackAsync(req.Event, current.User?.UserId.ToString(), new { surface = "web", privacy = "no_content" }, ct);
            }
            return Results.Accepted();
        }).WithTags("Lifecycle").RequireAuthorization();
        return app;
    }

    private sealed record InteractionRequest(string Event);
}