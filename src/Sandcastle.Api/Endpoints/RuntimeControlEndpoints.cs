using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Runtime;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-184: admin runtime control plane — fleet, pools, images and
/// destructive lifecycle operations gated to instance admins.
/// </summary>
public static class RuntimeControlEndpoints
{
    private const string PermRuntimeAdmin = "perm:manage_super_admins";

    /// <summary>Maps <c>/api/v1/admin/runtime</c> endpoints.</summary>
    public static IEndpointRouteBuilder MapRuntimeControlEndpoints(
        this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/admin/runtime").WithTags("Admin");
        g.MapGet("/fleet", GetFleetAsync).RequireAuthorization(PermRuntimeAdmin);
        g.MapGet("/pools", GetPoolsAsync).RequireAuthorization(PermRuntimeAdmin);
        g.MapGet("/images", GetImagesAsync).RequireAuthorization(PermRuntimeAdmin);
        g.MapPost("/drain", DrainAsync).RequireAuthorization(PermRuntimeAdmin);
        g.MapPost("/terminate", TerminateAsync).RequireAuthorization(PermRuntimeAdmin);
        g.MapPost("/recycle", RecycleAsync).RequireAuthorization(PermRuntimeAdmin);
        g.MapPost("/resize", ResizeAsync).RequireAuthorization(PermRuntimeAdmin);
        return app;
    }

    private static async Task<IResult> GetFleetAsync(
        IRuntimeControlService svc, CancellationToken ct) =>
        Results.Ok(await svc.GetFleetAsync(ct));

    private static async Task<IResult> GetPoolsAsync(
        IRuntimeControlService svc, CancellationToken ct) =>
        Results.Ok(await svc.GetPoolsAsync(ct));

    private static async Task<IResult> GetImagesAsync(
        IRuntimeControlService svc, CancellationToken ct) =>
        Results.Ok(await svc.GetImagesAsync(ct));

    private static async Task<IResult> DrainAsync(
        RuntimeIdRequest body, HttpContext ctx, ICurrentUserAccessor current,
        IRuntimeControlService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        return await svc.DrainOutpostAsync(body.Id, u.UserId.ToString(), ct)
            ? Results.Accepted()
            : SandcastleProblemFactory.Create(ctx, 404, "outpost not found");
    }

    private static async Task<IResult> TerminateAsync(
        RuntimeIdRequest body, HttpContext ctx, ICurrentUserAccessor current,
        IRuntimeControlService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        return await svc.TerminateSandboxAsync(body.Id, u.UserId.ToString(), ct)
            ? Results.Accepted()
            : SandcastleProblemFactory.Create(ctx, 404, "sandbox not found");
    }

    private static async Task<IResult> RecycleAsync(
        RuntimeIdRequest body, HttpContext ctx, ICurrentUserAccessor current,
        IRuntimeControlService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        return await svc.RecycleSandboxAsync(body.Id, u.UserId.ToString(), ct)
            ? Results.Accepted()
            : SandcastleProblemFactory.Create(ctx, 409, "runtime is not stale or does not exist");
    }

    private static async Task<IResult> ResizeAsync(
        RuntimePoolResizeRequest body, HttpContext ctx, ICurrentUserAccessor current,
        IRuntimeControlService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        var (changed, error) = await svc.ResizePoolAsync(body, u.UserId.ToString(), ct);
        return error is not null
            ? SandcastleProblemFactory.Create(ctx, 400, error)
            : changed ? Results.Accepted() : Results.BadRequest();
    }

    /// <summary>Runtime operation target.</summary>
    public sealed record RuntimeIdRequest(Guid Id);
}