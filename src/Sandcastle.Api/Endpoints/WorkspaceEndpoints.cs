using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Identity;
using Sandcastle.Domain.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// <c>/api/v1/workspace</c> — SPEC-157: workspace items CRUD
/// (prompts salvos, presets de modelo, context files) com ownership
/// pessoal ou org (admin-editable) + catálogo de tools MCP.
/// </summary>
public static class WorkspaceEndpoints
{
    private const int MaxBodyBytes = 64 * 1024;
    private static readonly string[] Kinds =
        ["prompt", "model_preset", "context_file"];

    /// <summary>Maps workspace endpoints.</summary>
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(
        this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/workspace/items")
            .WithTags("Workspace").RequireAuthorization();

        g.MapGet("/", ListItemsAsync);
        g.MapPost("/", CreateItemAsync);
        g.MapPut("/{id:guid}", UpdateItemAsync);
        g.MapDelete("/{id:guid}", DeleteItemAsync);
        app.MapGet("/api/v1/workspace/tools", ListToolsAsync)
            .WithTags("Workspace").RequireAuthorization();

        return app;
    }

    private static bool ValidKind(string? k) =>
        k is not null && Kinds.Contains(k);

    private static bool LooksLikeSecret(string s) =>
        s.Contains("sk-", StringComparison.Ordinal) ||
        s.Contains("ghp_", StringComparison.Ordinal) ||
        s.Contains("xoxb-", StringComparison.Ordinal) ||
        s.Contains("-----BEGIN", StringComparison.Ordinal) ||
        s.Contains("api_key", StringComparison.OrdinalIgnoreCase);

    private static string? ValidateBody(
        string kind, string bodyJson)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(bodyJson)
            > MaxBodyBytes)
            return "body_too_large";
        JsonObject? doc;
        try { doc = JsonNode.Parse(bodyJson) as JsonObject; }
        catch (JsonException) { return "body_not_json"; }
        if (doc is null) return "body_not_json";

        bool Has(string k) => doc[k] is not null;

        var ok = kind switch
        {
            "prompt" => Has("text"),
            "model_preset" => Has("provider") && Has("model"),
            "context_file" => Has("text") && Has("filename"),
            _ => false,
        };
        if (!ok) return "body_schema";
        if (LooksLikeSecret(bodyJson)) return "body_secret";
        return null;
    }

    private static async Task<IResult> ListItemsAsync(
        [FromQuery] string? kind,
        [FromQuery] string? scope,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var userId = current.User?.UserId.ToString();
        var org = current.User is { } u
            ? await orgs.ResolveAsync(u, null, ct) : null;

        var q = db.WorkspaceItems.AsQueryable();
        q = q.Where(i =>
            (i.OwnerId != null && i.OwnerId == userId) ||
            (i.OrgId != null && org != null && i.OrgId == org.OrgId));
        if (ValidKind(kind)) q = q.Where(i => i.Kind == kind);
        if (scope == "personal")
            q = q.Where(i => i.OwnerId != null);
        else if (scope == "org")
            q = q.Where(i => i.OrgId != null);

        var items = await q.OrderBy(i => i.Name)
            .Select(i => new
            {
                i.Id,
                i.Kind,
                i.Name,
                scope = i.OwnerId != null ? "personal" : "org",
                i.UpdatedAt,
                i.BodyJson,
            }).ToListAsync(ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> CreateItemAsync(
        [FromBody] WorkspaceItemRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, HttpContext http,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return SandcastleProblemFactory.Create(http, 401, "auth required");
        if (!ValidKind(req.Kind))
            return SandcastleProblemFactory.Create(http, 422, "invalid kind");
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200)
            return SandcastleProblemFactory.Create(http, 422, "invalid name");
        var bodyJson = req.Body?.ToJsonString() ?? "{}";
        if (ValidateBody(req.Kind!, bodyJson) is { } verr)
            return SandcastleProblemFactory.Create(http, 422, verr);

        Guid? orgId = null;
        string? ownerId = user.UserId.ToString();
        if (req.Scope == "org")
        {
            var org = await orgs.ResolveAsync(user, null, ct);
            if (org is null)
                return SandcastleProblemFactory.Create(http, 403, "no org");
            var role = await db.OrgMembers
                .Where(m => m.OrgId == org.OrgId && m.UserId == user.UserId)
                .Select(m => m.Role).FirstOrDefaultAsync(ct);
            if (role is not (RoleName.Owner or RoleName.Admin))
                return SandcastleProblemFactory.Create(
                    http, 403, "org admin required");
            orgId = org.OrgId;
            ownerId = null;
        }

        var item = new WorkspaceItemRecord
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            OrgId = orgId,
            Kind = req.Kind!,
            Name = req.Name.Trim(),
            BodyJson = bodyJson,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkspaceItems.Add(item);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(
            "workspace.item.create", user.UserId.ToString(), orgId,
            "workspace_item", item.Id.ToString(),
            metadata: new { item.Kind, item.Name }, cancellationToken: ct);
        return Results.Ok(ToDto(item));
    }

    /// <summary>Guard de escrita: dono pessoal ou admin da org.</summary>
    private static async Task<IResult?> EnsureWritableAsync(
        WorkspaceItemRecord item, AuthUser user,
        IEffectiveOrgResolver orgs, AppDbContext db,
        HttpContext http, CancellationToken ct)
    {
        if (item.OwnerId != null)
        {
            return item.OwnerId == user.UserId.ToString()
                ? null
                : SandcastleProblemFactory.Create(http, 403, Forbidden);
        }
        var org = await orgs.ResolveAsync(user, null, ct);
        if (org is null || item.OrgId != org.OrgId)
            return SandcastleProblemFactory.Create(http, 403, Forbidden);
        var role = await db.OrgMembers
            .Where(m => m.OrgId == item.OrgId && m.UserId == user.UserId)
            .Select(m => m.Role).FirstOrDefaultAsync(ct);
        return role is RoleName.Owner or RoleName.Admin
            ? null
            : SandcastleProblemFactory.Create(http, 403, Forbidden);
    }

    private const string Forbidden = "forbidden";

    private static async Task<IResult> UpdateItemAsync(
        Guid id, [FromBody] WorkspaceItemRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, HttpContext http,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return SandcastleProblemFactory.Create(http, 401, "auth required");
        var item = await db.WorkspaceItems.FindAsync([id], ct);
        if (item is null) return Results.NotFound();

        if (await EnsureWritableAsync(item, user, orgs, db, http, ct)
            is { } denied)
            return denied;

        if (!string.IsNullOrWhiteSpace(req.Name)
            && req.Name.Length <= 200)
            item.Name = req.Name.Trim();
        if (req.Body is not null)
        {
            var bodyJson = req.Body.ToJsonString();
            if (ValidateBody(item.Kind, bodyJson) is { } verr)
                return SandcastleProblemFactory.Create(http, 422, verr);
            item.BodyJson = bodyJson;
        }
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(
            "workspace.item.update", user.UserId.ToString(), item.OrgId,
            "workspace_item", item.Id.ToString(),
            metadata: new { item.Kind, item.Name }, cancellationToken: ct);
        return Results.Ok(ToDto(item));
    }

    private static async Task<IResult> DeleteItemAsync(
        Guid id,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, HttpContext http,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return SandcastleProblemFactory.Create(http, 401, "auth required");
        var item = await db.WorkspaceItems.FindAsync([id], ct);
        if (item is null) return Results.NotFound();
        if (await EnsureWritableAsync(item, user, orgs, db, http, ct)
            is { } denied)
            return denied;
        db.WorkspaceItems.Remove(item);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(
            "workspace.item.delete", user.UserId.ToString(), item.OrgId,
            "workspace_item", item.Id.ToString(),
            metadata: new { item.Kind, item.Name }, cancellationToken: ct);
        return Results.NoContent();
    }

    /// <summary>
    /// RF-005: catálogo read-only — servidores MCP do usuário e, quando
    /// <paramref name="conversationId"/> é informado, os tools resolvidos.
    /// </summary>
    private static async Task<IResult> ListToolsAsync(
        [FromQuery] Guid? conversationId,
        ICurrentUserAccessor current,
        Sandcastle.Application.Mcp.IMcpSettingsService settings,
        Sandcastle.Application.Mcp.IMcpClientService mcp,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var servers = await settings.ListAsync(
            user.UserId.ToString(), ct);
        var tools = conversationId is { } convId
            ? await mcp.ListToolsAsync(convId, ct)
            : [];
        return Results.Ok(new { servers, tools });
    }

    private static object ToDto(WorkspaceItemRecord i) => new
    {
        i.Id,
        i.Kind,
        i.Name,
        scope = i.OwnerId != null ? "personal" : "org",
        i.UpdatedAt,
        body = JsonNode.Parse(i.BodyJson),
    };

    /// <summary>Payload de create/update de workspace item.</summary>
    public sealed class WorkspaceItemRequest
    {
        /// <summary>prompt | model_preset | context_file.</summary>
        public string? Kind { get; init; }
        /// <summary>personal (default) | org.</summary>
        public string? Scope { get; init; }
        /// <summary>Nome do item.</summary>
        public string? Name { get; init; }
        /// <summary>Payload por kind.</summary>
        public JsonNode? Body { get; init; }
    }
}