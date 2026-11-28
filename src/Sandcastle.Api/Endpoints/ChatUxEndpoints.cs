using Microsoft.EntityFrameworkCore;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>SPEC-188: conversation branching and contextual PR status.</summary>
public static class ChatUxEndpoints
{
    /// <summary>Maps chat UX parity endpoints.</summary>
    public static IEndpointRouteBuilder MapChatUxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/conversations").WithTags(EndpointTags.Api).RequireAuthorization();
        group.MapGet("/{id:guid}/branches", ListBranchesAsync);
        group.MapPost("/{id:guid}/branch", BranchAsync);
        group.MapGet("/{id:guid}/pr-status", GetPrStatusAsync);
        return app;
    }

    private static async Task<IResult> ListBranchesAsync(Guid id, ICurrentUserAccessor current, AppDbContext db, CancellationToken ct)
    {
        var uid = current.User!.UserId.ToString();
        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.CreatedByUserId == uid, ct);
        if (conv is null) return Results.NotFound();
        var rows = await db.Conversations.AsNoTracking()
            .Where(c => c.CreatedByUserId == uid && (c.Id == id || c.ParentConversationId == id || c.Id == conv.ParentConversationId))
            .Select(c => new { c.Id, c.Title, c.ParentConversationId, c.ExecutionStatus, c.CreatedAt, c.UpdatedAt, c.PullRequestUrl })
            .ToListAsync(ct);
        return Results.Ok(rows.OrderBy(r => r.CreatedAt).Select(r => new
        {
            id = r.Id, title = r.Title, parent_conversation_id = r.ParentConversationId,
            status = r.ExecutionStatus, created_at = r.CreatedAt, updated_at = r.UpdatedAt,
            pull_request_url = r.PullRequestUrl, current = r.Id == id,
        }));
    }

    private static async Task<IResult> BranchAsync(Guid id, ForkConversationRequest body, IConversationUxService ux, ICurrentUserAccessor current, IEffectiveOrgResolver orgs, CancellationToken ct)
    {
        var org = current.User is { } u ? await orgs.ResolveAsync(u, null, ct) : null;
        var (result, error) = await ux.ForkAsync(id, org?.OrgId, current.User?.UserId.ToString(), body, ct);
        return (result, error) switch
        {
            ({ } r, null) => Results.Created($"/api/v1/conversations/{r.Id}", new { id = r.Id, start_task_id = r.StartTaskId, parent_conversation_id = r.ParentConversationId }),
            (_, ForkError.NotFound) => Results.NotFound(),
            (_, ForkError.RunActive) => Results.Conflict(new { error = "run_active" }),
            _ => Results.BadRequest(new { error = error?.ToString() }),
        };
    }

    private static async Task<IResult> GetPrStatusAsync(Guid id, ICurrentUserAccessor current, AppDbContext db, CancellationToken ct)
    {
        var uid = current.User!.UserId.ToString();
        var c = await db.Conversations.AsNoTracking()
            .Where(x => x.Id == id && x.CreatedByUserId == uid)
            .Select(x => new { x.PullRequestUrl, x.PullRequestNumber, x.PullRequestState, x.SelectedRepository, x.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        if (c is null) return Results.NotFound();
        return Results.Ok(new
        {
            repository = c.SelectedRepository,
            pull_request_url = c.PullRequestUrl,
            pull_request_number = c.PullRequestNumber,
            pull_request_state = c.PullRequestState ?? (c.PullRequestUrl is null ? "none" : "unknown"),
            checks = Array.Empty<object>(), review_comments = Array.Empty<object>(), preview_links = Array.Empty<string>(),
            updated_at = c.UpdatedAt,
        });
    }
}
