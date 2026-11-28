using Sandcastle.Application.Extensions;
using Sandcastle.Application.Identity;

namespace Sandcastle.Api.Endpoints;

/// <summary>SPEC-187: extensions/plugins/integrations hub endpoints.</summary>
public static class ExtensionEndpoints
{
    /// <summary>Maps extension hub endpoints.</summary>
    public static IEndpointRouteBuilder MapExtensionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").WithTags(EndpointTags.Api).RequireAuthorization();
        group.MapGet("/extensions/summary", async (ICurrentUserAccessor user, IExtensionHubService svc, CancellationToken ct) =>
            Results.Ok(await svc.SummaryAsync(user.User!.UserId, ct)));
        group.MapGet("/plugins", async (ICurrentUserAccessor user, IExtensionHubService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListPluginsAsync(user.User!.UserId, ct)));
        group.MapPost("/plugins", async (ICurrentUserAccessor user, PluginUpsert req, IExtensionHubService svc, CancellationToken ct) =>
            Results.Created("/api/v1/plugins", await svc.UpsertPluginAsync(null, req, user.User!.UserId, ct)));
        group.MapPut("/plugins/{id:guid}", async (Guid id, ICurrentUserAccessor user, PluginUpsert req, IExtensionHubService svc, CancellationToken ct) =>
            Results.Ok(await svc.UpsertPluginAsync(id, req, user.User!.UserId, ct)));
        group.MapPost("/plugins/{id:guid}/enabled", async (Guid id, ICurrentUserAccessor user, EnabledRequest req, IExtensionHubService svc, CancellationToken ct) =>
            await svc.SetPluginEnabledAsync(id, req.Enabled, user.User!.UserId, ct) ? Results.NoContent() : Results.NotFound());
        group.MapPost("/integrations/{provider}/test", async (string provider, ICurrentUserAccessor user, IExtensionHubService svc, CancellationToken ct) =>
            Results.Ok(await svc.TestIntegrationAsync(provider, user.User!.UserId, ct)));
        return app;
    }

    /// <summary>Enable/disable request.</summary>
    public sealed record EnabledRequest(bool Enabled);
}