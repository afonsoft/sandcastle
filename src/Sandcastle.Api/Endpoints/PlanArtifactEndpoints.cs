using Microsoft.EntityFrameworkCore;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Events;
using Sandcastle.Application.Git;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>SPEC-169: versioned plan approval artifact endpoints.</summary>
public static class PlanArtifactEndpoints
{
    /// <summary>Maps plan artifact endpoints under app-conversations.</summary>
    public static IEndpointRouteBuilder MapPlanArtifactEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/app-conversations")
            .WithTags("Conversations");
        group.MapGet("/{id:guid}/plan", GetAsync);
        group.MapPut("/{id:guid}/plan", PutAsync);
        group.MapPost("/{id:guid}/plan/items/{itemId:guid}/decision",
            DecideAsync);
        group.MapPost("/{id:guid}/plan/execute", ExecuteAsync);
        group.MapGet("/{id:guid}/plan/diff", DiffAsync);
        return app;
    }

    private static async Task<IResult> GetAsync(
        Guid id, IPlanArtifactService plans, IAppConversationService svc,
        HttpContext http, CancellationToken ct)
    {
        if (!await ExistsAsync(id, svc, ct))
        {
            return SandcastleProblemFactory.Create(http, 404,
                $"Conversation {id} not found");
        }

        return await plans.GetAsync(id, ct) is { } plan
            ? Results.Ok(plan)
            : SandcastleProblemFactory.Create(http, 404, "plan not found");
    }

    private static async Task<IResult> PutAsync(
        Guid id, PutPlanArtifactRequest body, IPlanArtifactService plans,
        IAppConversationService svc, AppDbContext db,
        HttpContext http, CancellationToken ct)
    {
        var conv = await svc.GetAsync(id, ct);
        if (conv is null)
        {
            return SandcastleProblemFactory.Create(http, 404,
                $"Conversation {id} not found");
        }

        var mode = await db.Conversations.Where(c => c.Id == id)
            .Select(c => c.Mode).FirstOrDefaultAsync(ct);
        if (!string.Equals(mode, "plan", StringComparison.OrdinalIgnoreCase))
        {
            return SandcastleProblemFactory.Create(http, 409,
                "conversation_not_in_plan_mode");
        }

        try
        {
            return Results.Ok(await plans.PutAsync(id, body, ct));
        }
        catch (PlanArtifactStateException ex)
        {
            return SandcastleProblemFactory.Create(http, 409, ex.Code);
        }
    }

    private static async Task<IResult> DecideAsync(
        Guid id, Guid itemId, PlanDecisionRequest body,
        IPlanArtifactService plans, IAppConversationService svc,
        IEventService events, HttpContext http, CancellationToken ct)
    {
        if (!await ExistsAsync(id, svc, ct))
        {
            return SandcastleProblemFactory.Create(http, 404,
                $"Conversation {id} not found");
        }

        try
        {
            var plan = await plans.DecideAsync(
                id, itemId, body.Decision ?? "", body.Comment, ct);
            if (plan is null)
            {
                return SandcastleProblemFactory.Create(
                    http, 404, "plan item not found");
            }

            if (string.Equals(body.Decision, "reject",
                    StringComparison.OrdinalIgnoreCase))
            {
                await events.AppendAsync(id, new AgentDiagnosticEvent
                {
                    Severity = "info",
                    Code = "plan_item_rejected",
                    Message = body.Comment ?? "plan item rejected",
                }, ct);
            }

            return Results.Ok(plan);
        }
        catch (PlanArtifactStateException ex)
        {
            return SandcastleProblemFactory.Create(http, 409, ex.Code);
        }
    }

    private static async Task<IResult> ExecuteAsync(
        Guid id, IPlanArtifactService plans, IAppConversationService svc,
        IEventService events, HttpContext http, CancellationToken ct)
    {
        if (!await ExistsAsync(id, svc, ct))
        {
            return SandcastleProblemFactory.Create(http, 404,
                $"Conversation {id} not found");
        }

        try
        {
            var plan = await plans.ExecuteAsync(id, ct);
            if (plan is null)
            {
                return SandcastleProblemFactory.Create(
                    http, 404, "plan not found");
            }

            await svc.SetModeAsync(id, "build", ct);
            await events.AppendAsync(id, new AgentDiagnosticEvent
            {
                Severity = "info",
                Code = "plan_executed",
                Message = $"plan version {plan.Version} executed",
            }, ct);
            return Results.Ok(plan);
        }
        catch (PlanArtifactStateException ex)
        {
            return SandcastleProblemFactory.Create(http, 409, ex.Code);
        }
    }

    private static async Task<IResult> DiffAsync(
        Guid id, string? file, IPlanArtifactService plans,
        IAppConversationService svc, IGitService git,
        ISandboxService sandboxes, HttpContext http, CancellationToken ct)
    {
        if (await plans.GetAsync(id, ct) is null)
        {
            return SandcastleProblemFactory.Create(http, 404, "plan not found");
        }

        var conv = await svc.GetAsync(id, ct);
        if (conv?.SandboxId is null)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        var sandbox = await sandboxes.GetAsync(conv.SandboxId.Value, ct);
        if (sandbox?.Status is not Domain.Sandbox.SandboxStatus.Running)
        {
            return SandcastleProblemFactory.Create(
                http, 409, SandcastleProblemFactory.SandboxNotRunning);
        }

        if (!await git.HasRepositoryAsync(sandbox, ct))
        {
            return SandcastleProblemFactory.Create(http, 404, "no_repository");
        }

        return Results.Text(
            await git.GetRawDiffAsync(sandbox, file, ct) ?? "",
            "text/plain");
    }

    private static async Task<bool> ExistsAsync(
        Guid id, IAppConversationService svc, CancellationToken ct) =>
        await svc.GetAsync(id, ct) is not null;

    /// <summary>Decision request body.</summary>
    public sealed record PlanDecisionRequest(string? Decision, string? Comment);
}