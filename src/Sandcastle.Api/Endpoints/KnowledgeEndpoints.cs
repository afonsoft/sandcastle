using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Identity;
using Sandcastle.Application.Knowledge;
using Sandcastle.Application.Memory;
using Sandcastle.Infrastructure.Knowledge;

namespace Sandcastle.Api.Endpoints;

/// <summary>SPEC-63: knowledge pipeline — suggestions + groom.</summary>
public static class KnowledgeEndpoints
{
    public static WebApplication MapKnowledgeEndpoints(
        this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/knowledge").RequireAuthorization();

        g.MapGet("/suggestions", ListSuggestionsAsync)
            .WithTags(EndpointTags.Api);
        g.MapPost("/suggestions", async (SuggestRequest body,
            IKnowledgeService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Content))
                return Results.BadRequest(
                    new { error = "content_required" });
            var s = await svc.SuggestAsync(
                body.Title ?? "suggestion", body.Content,
                body.Scope ?? "org", body.RepoPath,
                SuggestionKind.Entry, "manual", null, null, ct);
            return Results.Created(
                $"/api/v1/knowledge/suggestions/{s.Id}", s);
        });
        g.MapPost("/suggestions/{id:guid}/approve", async (Guid id,
            ApproveRequest? body, IKnowledgeService svc,
            ICurrentUserAccessor current, HttpContext http,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await svc.ApproveAsync(id,
                    body?.ContentOverride,
                    current.User?.UserId.ToString(), ct));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (SuggestionResolvedException)
            {
                return SandcastleProblemFactory.Create(
                    http, 409, "suggestion_resolved");
            }
        });
        g.MapPost("/suggestions/{id:guid}/dismiss", async (Guid id,
            DismissRequest? body, IKnowledgeService svc,
            HttpContext http, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await svc.DismissAsync(
                    id, body?.Reason, ct));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (SuggestionResolvedException)
            {
                return SandcastleProblemFactory.Create(
                    http, 409, "suggestion_resolved");
            }
        });
        g.MapPost("/groom", async (IKnowledgeService svc,
            CancellationToken ct) =>
            Results.Ok(await svc.GroomAsync(ct)))
            .RequireAuthorization("perm:edit_org_settings");
        g.MapGet("/entries", async (string? query,
            IMemoryService memory, IEffectiveOrgResolver orgs,
            ICurrentUserAccessor current, HttpContext http,
            CancellationToken ct) =>
            current.User is { } u
                && await orgs.ResolveAsync(
                    u, http.Request.Headers["X-Org-Id"], ct) is { } org
                ? Results.Ok(await memory.ListOrgAsync(
                    org.OrgId, query, ct))
                : Results.Ok(Array.Empty<object>()));

        // SPEC-189: knowledge collections/documents + hybrid retrieval.
        g.MapGet("/collections", async (ICurrentUserAccessor current, IKnowledgeRagService rag, CancellationToken ct) =>
            Results.Ok(await rag.ListCollectionsAsync(current.User!.UserId, ct)));
        g.MapPost("/collections", async (KnowledgeCollectionUpsert req, ICurrentUserAccessor current, IKnowledgeRagService rag, CancellationToken ct) =>
            Results.Created("/api/v1/knowledge/collections", await rag.CreateCollectionAsync(req, current.User!.UserId, ct)));
        g.MapDelete("/collections/{id:guid}", async (Guid id, ICurrentUserAccessor current, IKnowledgeRagService rag, CancellationToken ct) =>
            await rag.DeleteCollectionAsync(id, current.User!.UserId, ct) ? Results.NoContent() : Results.NotFound());
        g.MapGet("/collections/{id:guid}/documents", async (Guid id, ICurrentUserAccessor current, IKnowledgeRagService rag, CancellationToken ct) =>
            Results.Ok(await rag.ListDocumentsAsync(id, current.User!.UserId, ct)));
        g.MapPost("/collections/{id:guid}/documents", async (Guid id, KnowledgeDocumentUpsert req, ICurrentUserAccessor current, IKnowledgeRagService rag, CancellationToken ct) =>
            await rag.IngestDocumentAsync(id, req, current.User!.UserId, ct) is { } doc ? Results.Created($"/api/v1/knowledge/documents/{doc.Id}", doc) : Results.NotFound());
        g.MapPost("/query", async (KnowledgeQueryRequest req, ICurrentUserAccessor current, IKnowledgeRagService rag, CancellationToken ct) =>
            Results.Ok(await rag.QueryAsync(req with { Take = req.Take <= 0 ? 8 : req.Take }, current.User!.UserId, ct)));
        return app;
    }

    private static async Task<IResult> ListSuggestionsAsync(
        string? status, string? scope,
        IKnowledgeService svc, HttpContext http, CancellationToken ct)
    {
        SuggestionStatus? parsed = null;
        if (status is not null)
        {
            if (!Enum.TryParse(status, true, out SuggestionStatus s))
            {
                return SandcastleProblemFactory.Create(
                    http, 400, $"invalid status '{status}'");
            }
            parsed = s;
        }

        return Results.Ok(await svc.ListSuggestionsAsync(
            parsed, scope, ct));
    }

    public sealed record SuggestRequest(
        string? Title, string Content, string? Scope,
        string? RepoPath);
    public sealed record ApproveRequest(string? ContentOverride);
    public sealed record DismissRequest(string? Reason);
}