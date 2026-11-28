using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using Sandcastle.Api.Policies;
using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Workflows;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-183: <c>/api/v1/factories</c>, <c>/api/v1/workflows</c>,
/// <c>/api/v1/automation-templates</c> and <c>/api/v1/automation-events</c>.
/// </summary>
public static class WorkflowEndpoints
{
    private const string FactoriesWrite = "automations:write";
    private const string FactoriesRead = "automations:read";

    /// <summary>Maps factories, workflows and automation event-center endpoints.</summary>
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        var factories = app.MapGroup("/api/v1/factories").WithTags("Factories");
        factories.MapGet("", ListFactoriesAsync).RequireAuthorization().RequireScope(FactoriesRead);
        factories.MapPost("", CreateFactoryAsync).RequireAuthorization().RequireScope(FactoriesWrite);
        factories.MapGet("/{id:guid}", GetFactoryAsync).RequireAuthorization().RequireScope(FactoriesRead);
        factories.MapPatch("/{id:guid}", UpdateFactoryAsync).RequireAuthorization().RequireScope(FactoriesWrite);
        factories.MapDelete("/{id:guid}", DeleteFactoryAsync).RequireAuthorization().RequireScope(FactoriesWrite);
        factories.MapGet("/{id:guid}/automations", ListFactoryAutomationsAsync)
            .RequireAuthorization().RequireScope(FactoriesRead);

        var workflows = app.MapGroup("/api/v1/workflows").WithTags("Workflows");
        workflows.MapGet("", ListWorkflowsAsync).RequireAuthorization().RequireScope(FactoriesRead);
        workflows.MapPost("", CreateWorkflowAsync).RequireAuthorization().RequireScope(FactoriesWrite);
        workflows.MapGet("/{id:guid}", GetWorkflowAsync).RequireAuthorization().RequireScope(FactoriesRead);
        workflows.MapPatch("/{id:guid}", UpdateWorkflowAsync).RequireAuthorization().RequireScope(FactoriesWrite);
        workflows.MapDelete("/{id:guid}", DeleteWorkflowAsync).RequireAuthorization().RequireScope(FactoriesWrite);
        workflows.MapPost("/{id:guid}/runs", StartWorkflowRunAsync)
            .RequireAuthorization().RequireScope(FactoriesWrite);
        workflows.MapGet("/{id:guid}/runs", ListWorkflowRunsAsync)
            .RequireAuthorization().RequireScope(FactoriesRead);
        workflows.MapGet("/runs/{runId:guid}", GetWorkflowRunAsync)
            .RequireAuthorization().RequireScope(FactoriesRead);
        workflows.MapPost("/runs/{runId:guid}/approval", ApproveWorkflowRunAsync)
            .RequireAuthorization().RequireScope(FactoriesWrite);

        app.MapGet("/api/v1/automation-templates",
            ([FromServices] IWorkflowService svc) => Results.Ok(svc.ListTemplates()))
            .RequireAuthorization().RequireScope(FactoriesRead).WithTags("Automations");
        app.MapGet("/api/v1/automation-events", ListEventsAsync)
            .RequireAuthorization().RequireScope(FactoriesRead).WithTags("Automations");
        return app;
    }

    private static async Task<IResult> ListFactoriesAsync(
        HttpContext ctx, ICurrentUserAccessor current, IFactoryService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        return Results.Ok(await svc.ListAsync(u.UserId, ct));
    }

    private static async Task<IResult> CreateFactoryAsync(
        FactoryUpsert body, HttpContext ctx, ICurrentUserAccessor current,
        IEffectiveOrgResolver resolver, IFactoryService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        var org = await resolver.ResolveAsync(u, ctx.Request.Headers["X-Org-Id"].ToString(), ct);
        var (info, error) = await svc.CreateAsync(body, u.UserId, org?.OrgId, ct);
        return error is not null
            ? SandcastleProblemFactory.Create(ctx, 400, error)
            : Results.Created($"/api/v1/factories/{info!.Id}", info);
    }

    private static async Task<IResult> GetFactoryAsync(
        Guid id, HttpContext ctx, ICurrentUserAccessor current, IFactoryService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        var info = await svc.GetAsync(id, u.UserId, ct);
        return info is null
            ? SandcastleProblemFactory.Create(ctx, 404, "factory not found")
            : Results.Ok(info);
    }

    private static async Task<IResult> UpdateFactoryAsync(
        Guid id, FactoryUpsert body, HttpContext ctx, ICurrentUserAccessor current,
        IFactoryService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        var (info, error) = await svc.UpdateAsync(id, body, u.UserId, ct);
        return error switch
        {
            "not found" => SandcastleProblemFactory.Create(ctx, 404, "factory not found"),
            not null => SandcastleProblemFactory.Create(ctx, 400, error),
            _ => Results.Ok(info!),
        };
    }

    private static async Task<IResult> DeleteFactoryAsync(
        Guid id, ICurrentUserAccessor current, IFactoryService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        return await svc.DeleteAsync(id, u.UserId, ct) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ListFactoryAutomationsAsync(
        Guid id, HttpContext ctx, ICurrentUserAccessor current, IFactoryService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        if (await svc.GetAsync(id, u.UserId, ct) is null)
            return SandcastleProblemFactory.Create(ctx, 404, "factory not found");
        return Results.Ok(await svc.ListAutomationsAsync(id, u.UserId, ct));
    }

    private static async Task<IResult> ListWorkflowsAsync(
        Guid? factory_id, ICurrentUserAccessor current, IWorkflowService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        return Results.Ok(await svc.ListAsync(u.UserId, factory_id, ct));
    }

    private static async Task<IResult> CreateWorkflowAsync(
        WorkflowUpsert body, HttpContext ctx, ICurrentUserAccessor current,
        IEffectiveOrgResolver resolver, IWorkflowService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        var org = await resolver.ResolveAsync(u, ctx.Request.Headers["X-Org-Id"].ToString(), ct);
        var (info, error) = await svc.CreateAsync(body, u.UserId, org?.OrgId, ct);
        return error is not null
            ? SandcastleProblemFactory.Create(ctx, 400, error)
            : Results.Created($"/api/v1/workflows/{info!.Id}", info);
    }

    private static async Task<IResult> GetWorkflowAsync(
        Guid id, HttpContext ctx, ICurrentUserAccessor current, IWorkflowService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        var info = await svc.GetAsync(id, u.UserId, ct);
        return info is null
            ? SandcastleProblemFactory.Create(ctx, 404, "workflow not found")
            : Results.Ok(info);
    }

    private static async Task<IResult> UpdateWorkflowAsync(
        Guid id, WorkflowUpsert body, HttpContext ctx, ICurrentUserAccessor current,
        IWorkflowService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        var (info, error) = await svc.UpdateAsync(id, body, u.UserId, ct);
        return error switch
        {
            "not found" => SandcastleProblemFactory.Create(ctx, 404, "workflow not found"),
            not null => SandcastleProblemFactory.Create(ctx, 400, error),
            _ => Results.Ok(info!),
        };
    }

    private static async Task<IResult> DeleteWorkflowAsync(
        Guid id, ICurrentUserAccessor current, IWorkflowService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        return await svc.DeleteAsync(id, u.UserId, ct) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> StartWorkflowRunAsync(
        Guid id, WorkflowRunStart body, HttpContext ctx, ICurrentUserAccessor current,
        IWorkflowService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        var (run, error) = await svc.StartRunAsync(id, body, u.UserId, ct);
        return error switch
        {
            "not found" => SandcastleProblemFactory.Create(ctx, 404, "workflow not found"),
            "workflow run already in progress" => SandcastleProblemFactory.Create(ctx, 409, error),
            not null => SandcastleProblemFactory.Create(ctx, 400, error),
            _ => Results.Accepted($"/api/v1/workflows/runs/{run!.Id}", run),
        };
    }

    private static async Task<IResult> ListWorkflowRunsAsync(
        Guid id, int? take, HttpContext ctx, ICurrentUserAccessor current,
        IWorkflowService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        if (await svc.GetAsync(id, u.UserId, ct) is null)
            return SandcastleProblemFactory.Create(ctx, 404, "workflow not found");
        return Results.Ok(await svc.ListRunsAsync(id, u.UserId, take ?? 25, ct));
    }

    private static async Task<IResult> GetWorkflowRunAsync(
        Guid runId, HttpContext ctx, ICurrentUserAccessor current,
        IWorkflowService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        var run = await svc.GetRunAsync(runId, u.UserId, ct);
        return run is null
            ? SandcastleProblemFactory.Create(ctx, 404, "workflow run not found")
            : Results.Ok(run);
    }

    private static async Task<IResult> ApproveWorkflowRunAsync(
        Guid runId, WorkflowApprovalRequest body, HttpContext ctx,
        ICurrentUserAccessor current, IWorkflowService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        var (run, error) = await svc.ApproveRunAsync(runId, body, u.UserId, ct);
        return error switch
        {
            "not found" => SandcastleProblemFactory.Create(ctx, 404, "workflow run not found"),
            not null => SandcastleProblemFactory.Create(ctx, 400, error),
            _ => Results.Ok(run!),
        };
    }

    private static async Task<IResult> ListEventsAsync(
        Guid? factory_id, string? status, string? repository, int? take,
        ICurrentUserAccessor current, IWorkflowService svc, CancellationToken ct)
    {
        if (current.User is not { } u) return Results.Unauthorized();
        return Results.Ok(await svc.ListEventsAsync(
            u.UserId, factory_id, status, repository, take ?? 50, ct));
    }
}