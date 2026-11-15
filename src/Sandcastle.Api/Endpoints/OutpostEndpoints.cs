using Microsoft.AspNetCore.Mvc;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Outpost;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// <c>/api/v1/outposts</c> — SPEC-153: lifecycle de outposts self-hosted
/// (enroll token one-time, register handshake, list/drain/deregister
/// org-scoped). O <c>/connect</c> WebSocket chega na próxima fatia.
/// </summary>
public static class OutpostEndpoints
{
    /// <summary>Maps outpost endpoints.</summary>
    public static IEndpointRouteBuilder MapOutpostEndpoints(
        this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/outposts").WithTags("Outposts");

        g.MapPost("/enroll", EnrollAsync)
            .RequireAuthorization("perm:outposts.manage");
        g.MapGet("/", ListAsync)
            .RequireAuthorization("perm:outposts.view");
        g.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization("perm:outposts.view");
        g.MapPost("/{id:guid}/drain", DrainAsync)
            .RequireAuthorization("perm:outposts.manage");
        g.MapPost("/{id:guid}/deregister", DeregisterAsync)
            .RequireAuthorization("perm:outposts.manage");

        // Handshake do agente — auth é o enroll token (one-time).
        g.MapPost("/register", RegisterAsync).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> EnrollAsync(
        [FromBody] EnrollRequest req, HttpContext http,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        IOutpostService svc, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        if (org is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "no org");
        }

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "name is required");
        }

        var connectUrl =
            $"{http.Request.Scheme}://{http.Request.Host}";
        return Results.Ok(await svc.EnrollAsync(
            org.OrgId, req.Name, req.Labels, user.UserId, connectUrl, ct));
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IOutpostService svc,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        return org is null
            ? SandcastleProblemFactory.Create(http, 404, "no org")
            : Results.Ok(await svc.ListAsync(org.OrgId, ct));
    }

    private static async Task<IResult> GetAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IOutpostService svc,
        CancellationToken ct) =>
        await ScopedAsync(http, current, orgs, ct,
            async org => await svc.GetAsync(id, org.OrgId, ct) is { } o
                ? Results.Ok(o)
                : NotFound(http, id));

    private static async Task<IResult> DrainAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IOutpostService svc,
        CancellationToken ct) =>
        await ScopedAsync(http, current, orgs, ct,
            async org => await svc.DrainAsync(id, org.OrgId, ct)
                ? Results.Ok(new { status = OutpostStatuses.Draining })
                : NotFound(http, id));

    private static async Task<IResult> DeregisterAsync(
        Guid id, HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, IOutpostService svc,
        CancellationToken ct) =>
        await ScopedAsync(http, current, orgs, ct,
            async org => await svc.DeregisterAsync(id, org.OrgId, ct)
                ? Results.Ok(new { status = OutpostStatuses.Revoked })
                : NotFound(http, id));

    /// <summary>
    /// <c>POST /register</c> — troca o enroll token one-time pela
    /// channel key <c>opk-*</c> (+ par de cert mTLS quando exigido).
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        [FromBody] RegisterRequest req, IOutpostService svc,
        HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.EnrollToken))
        {
            return SandcastleProblemFactory.Create(
                http, 400, "enroll_token is required");
        }

        return await svc.RegisterAsync(
            req.EnrollToken, req.Name, req.Labels, req.Version, ct)
            is { } r
            ? Results.Ok(r)
            : SandcastleProblemFactory.Create(
                http, 401, "invalid or consumed enroll token");
    }

    private static IResult NotFound(HttpContext http, Guid id) =>
        SandcastleProblemFactory.Create(
            http, 404, $"outpost {id} not found");

    private static async Task<IResult> ScopedAsync(
        HttpContext http, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, CancellationToken ct,
        Func<EffectiveOrg, Task<IResult>> body)
    {
        var user = current.User;
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var org = await orgs.ResolveAsync(
            user, http.Request.Headers["X-Org-Id"].ToString(), ct);
        return org is null
            ? SandcastleProblemFactory.Create(http, 404, "no org")
            : await body(org);
    }

    /// <summary>Enroll request body.</summary>
    public sealed record EnrollRequest(
        string Name,
        IReadOnlyDictionary<string, string>? Labels);

    /// <summary>Register request body (agente → plano).</summary>
    public sealed record RegisterRequest(
        string EnrollToken, string? Name,
        IReadOnlyDictionary<string, string>? Labels, string? Version);
}