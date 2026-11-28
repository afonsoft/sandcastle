using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Sandcastle.Application.Extensions;

namespace Sandcastle.Api.Endpoints;

public static class ExtensionEndpoints
{
    public static void MapExtensionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/extensions/summary", async (ClaimsPrincipal user, IExtensionHubService svc, CancellationToken ct) =>
            Results.Ok(await svc.SummaryAsync(UserId(user), ct))).WithTags("Extensions");
        app.MapGet("/api/v1/plugins", async (ClaimsPrincipal user, IExtensionHubService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListPluginsAsync(UserId(user), ct))).WithTags("Extensions");
        app.MapPost("/api/v1/plugins", async (ClaimsPrincipal user, PluginUpsert req, IExtensionHubService svc, CancellationToken ct) =>
            Results.Created("/api/v1/plugins", await svc.UpsertPluginAsync(null, req, UserId(user), ct))).WithTags("Extensions");
        app.MapPut("/api/v1/plugins/{id:guid}", async (Guid id, ClaimsPrincipal user, PluginUpsert req, IExtensionHubService svc, CancellationToken ct) =>
            Results.Ok(await svc.UpsertPluginAsync(id, req, UserId(user), ct))).WithTags("Extensions");
        app.MapPost("/api/v1/plugins/{id:guid}/enabled", async (Guid id, ClaimsPrincipal user, EnabledRequest req, IExtensionHubService svc, CancellationToken ct) =>
            await svc.SetPluginEnabledAsync(id, req.Enabled, UserId(user), ct) ? Results.NoContent() : Results.NotFound()).WithTags("Extensions");
        app.MapPost("/api/v1/integrations/{provider}/test", async (string provider, ClaimsPrincipal user, IExtensionHubService svc, CancellationToken ct) =>
            Results.Ok(await svc.TestIntegrationAsync(provider, UserId(user), ct))).WithTags("Extensions");
    }
    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")!);
    public sealed record EnabledRequest(bool Enabled);
}
