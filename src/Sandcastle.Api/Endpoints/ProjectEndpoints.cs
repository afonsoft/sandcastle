using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Api.ProblemDetails;
using Sandcastle.Application.Conversations;
using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>SPEC-195: project coordinator workspaces.</summary>
public static class ProjectEndpoints
{
    private const int MaxName = 200;
    private const int MaxDescription = 4000;
    private const int MaxMemoryValueBytes = 8 * 1024;
    private const int MaxFileBytes = 16 * 1024;
    private const int MaxProjectFiles = 50;
    private const int MaxProjectMemories = 100;

    /// <summary>Maps project endpoints.</summary>
    public static IEndpointRouteBuilder MapProjectEndpoints(
        this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/projects")
            .WithTags("Projects").RequireAuthorization();
        g.MapGet("/", ListAsync);
        g.MapPost("/", CreateAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapPost("/{id:guid}/archive", ArchiveAsync);
        g.MapPost("/{id:guid}/workers", AttachWorkerAsync);
        g.MapDelete("/{id:guid}/workers/{conversationId:guid}", DetachWorkerAsync);
        g.MapPut("/{id:guid}/memory/{key}", PutMemoryAsync);
        g.MapDelete("/{id:guid}/memory/{key}", DeleteMemoryAsync);
        g.MapPut("/{id:guid}/files/{*path}", PutFileAsync);
        g.MapDelete("/{id:guid}/files/{*path}", DeleteFileAsync);
        return app;
    }

    private static async Task<(ProjectRecord? Project, IResult? Error)> LoadAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return (null, SandcastleProblemFactory.Create(http, 401, "auth required"));
        var org = await orgs.ResolveAsync(user, null, ct);
        var userId = user.UserId.ToString();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id
            && (p.OwnerId == userId || (org != null && p.OrgId == org.OrgId)), ct);
        return project is null
            ? (null, SandcastleProblemFactory.Create(http, 404, "project not found"))
            : (project, null);
    }

    private static object ToDto(ProjectRecord p, IReadOnlyList<ProjectWorkerRecord>? workers = null,
        IReadOnlyList<ProjectMemoryRecord>? memories = null,
        IReadOnlyList<ProjectFileRecord>? files = null) => new
        {
            p.Id,
            p.Name,
            p.Description,
            repositories = JsonSerializer.Deserialize<List<string>>(p.RepositoriesJson) ?? [],
            p.CoordinatorConversationId,
            p.Status,
            p.CreatedAt,
            p.UpdatedAt,
            p.ArchivedAt,
            scope = p.OwnerId is null ? "org" : "personal",
            workers = workers?.Select(w => new { w.ConversationId, w.CreatedAt }).ToArray(),
            memories = memories?.Select(m => new { m.Key, m.Value, m.UpdatedAt }).ToArray(),
            files = files?.Select(f => new { f.Path, f.ContentType, f.SizeBytes, f.UpdatedAt }).ToArray(),
        };

    private static async Task<IResult> ListAsync(
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var org = await orgs.ResolveAsync(user, null, ct);
        var userId = user.UserId.ToString();
        var rows = await db.Projects.AsNoTracking()
            .Where(p => p.Status != "archived"
                && (p.OwnerId == userId || (org != null && p.OrgId == org.OrgId)))
            .Take(500).ToListAsync(ct);
        var projects = rows.OrderByDescending(p => p.UpdatedAt).Take(200).ToList();
        return Results.Ok(projects.Select(p => ToDto(p)).ToArray());
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] ProjectUpsertRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAppConversationService conversations,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var user = current.User;
        if (user is null)
            return SandcastleProblemFactory.Create(http, 401, "auth required");
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > MaxName)
            return SandcastleProblemFactory.Create(http, 422, "invalid project name");
        if ((req.Description?.Length ?? 0) > MaxDescription)
            return SandcastleProblemFactory.Create(http, 422, "description too long");
        var repos = (req.Repositories ?? [])
            .Select(r => r.Trim()).Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        if (repos.Count == 0)
            return SandcastleProblemFactory.Create(http, 422, "at least one repository is required");

        var org = await orgs.ResolveAsync(user, null, ct);
        Guid? orgId = req.Scope == "org" ? org?.OrgId : null;
        if (req.Scope == "org" && org is null)
            return SandcastleProblemFactory.Create(http, 403, "no org");
        var now = DateTimeOffset.UtcNow;
        var project = new ProjectRecord
        {
            Id = Guid.NewGuid(),
            OwnerId = orgId is null ? user.UserId.ToString() : null,
            OrgId = orgId,
            Name = req.Name.Trim(),
            Description = req.Description?.Trim(),
            RepositoriesJson = JsonSerializer.Serialize(repos),
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (req.StartCoordinator == true)
        {
            var message = string.IsNullOrWhiteSpace(req.CoordinatorMessage)
                ? $"Coordinate project '{project.Name}'. Break work into bounded worker tasks, review evidence, and keep shared project memory current."
                : req.CoordinatorMessage.Trim();
            try
            {
                var task = await conversations.CreateForUserAsync(
                    user.UserId, orgId, new CreateConversationRequest(
                        new InitialMessage("user",
                            [new { type = "text", text = message }], Run: true),
                        repos[0], null, null, null,
                        SelectedRepositories: repos,
                        AgentProfileId: req.AgentProfileId), ct);
                project.CoordinatorConversationId = task.AppConversationId;
            }
            catch (Exception ex) when (ex is ProfileSelectionException
                or RepositorySelectionException)
            {
                return SandcastleProblemFactory.Create(http, 400, ex.Message);
            }
        }

        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("project.create", user.UserId.ToString(), orgId,
            "project", project.Id.ToString(),
            metadata: new
            {
                project.Name,
                repositories = repos.Count,
                coordinator_started = req.StartCoordinator == true
            }, cancellationToken: ct);
        return Results.Created($"/api/v1/projects/{project.Id}", ToDto(project));
    }

    private static async Task<IResult> GetAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var (project, error) = await LoadAsync(id, current, orgs, db, http, ct);
        if (error is not null) return error;
        var workers = (await db.ProjectWorkers.AsNoTracking()
            .Where(w => w.ProjectId == id).ToListAsync(ct))
            .OrderBy(w => w.CreatedAt).ToList();
        var memories = await db.ProjectMemories.AsNoTracking()
            .Where(m => m.ProjectId == id).OrderBy(m => m.Key).Take(MaxProjectMemories).ToListAsync(ct);
        var files = await db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == id).OrderBy(f => f.Path).Take(MaxProjectFiles).ToListAsync(ct);
        return Results.Ok(ToDto(project!, workers, memories, files));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, [FromBody] ProjectUpsertRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var (project, error) = await LoadAsync(id, current, orgs, db, http, ct);
        if (error is not null) return error;
        if (!string.IsNullOrWhiteSpace(req.Name) && req.Name.Length <= MaxName)
            project!.Name = req.Name.Trim();
        if (req.Description is not null && req.Description.Length <= MaxDescription)
            project!.Description = req.Description.Trim();
        if (req.Repositories is { Count: > 0 })
        {
            var repos = req.Repositories.Select(r => r.Trim()).Where(r => r.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
            project!.RepositoriesJson = JsonSerializer.Serialize(repos);
        }
        project!.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("project.update", current.User!.UserId.ToString(), project.OrgId,
            "project", project.Id.ToString(), cancellationToken: ct);
        return Results.Ok(ToDto(project));
    }

    private static async Task<IResult> ArchiveAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var (project, error) = await LoadAsync(id, current, orgs, db, http, ct);
        if (error is not null) return error;
        project!.Status = "archived";
        project.ArchivedAt ??= DateTimeOffset.UtcNow;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("project.archive", current.User!.UserId.ToString(), project.OrgId,
            "project", project.Id.ToString(), cancellationToken: ct);
        return Results.Ok(ToDto(project));
    }

    private static async Task<IResult> AttachWorkerAsync(
        Guid id, [FromBody] ProjectWorkerRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var (project, error) = await LoadAsync(id, current, orgs, db, http, ct);
        if (error is not null) return error;
        if (req.ConversationId == project!.CoordinatorConversationId)
            return SandcastleProblemFactory.Create(http, 422, "coordinator cannot be attached as worker");
        var conversation = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == req.ConversationId, ct);
        if (conversation is null)
            return SandcastleProblemFactory.Create(http, 404, "conversation not found");
        var userId = current.User!.UserId.ToString();
        var canUseConversation = conversation.CreatedByUserId == userId
            || (project.OrgId is not null && conversation.OrgId == project.OrgId);
        if (!canUseConversation)
            return SandcastleProblemFactory.Create(http, 403, "conversation outside project scope");
        if (!await db.ProjectWorkers.AnyAsync(w => w.ProjectId == id
            && w.ConversationId == req.ConversationId, ct))
        {
            db.ProjectWorkers.Add(new ProjectWorkerRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = id,
                ConversationId = req.ConversationId,
                AddedByUserId = current.User!.UserId.ToString(),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync("project.worker.attach", current.User!.UserId.ToString(), project.OrgId,
                "project", id.ToString(), metadata: new { req.ConversationId }, cancellationToken: ct);
        }
        return Results.Ok(new { attached = true, req.ConversationId });
    }

    private static async Task<IResult> DetachWorkerAsync(
        Guid id, Guid conversationId, ICurrentUserAccessor current,
        IEffectiveOrgResolver orgs, AppDbContext db, IAuditService audit,
        HttpContext http, CancellationToken ct)
    {
        var (project, error) = await LoadAsync(id, current, orgs, db, http, ct);
        if (error is not null) return error;
        var rows = await db.ProjectWorkers.Where(w => w.ProjectId == id
            && w.ConversationId == conversationId).ToListAsync(ct);
        if (rows.Count == 0) return Results.NotFound();
        db.ProjectWorkers.RemoveRange(rows);
        project!.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("project.worker.detach", current.User!.UserId.ToString(), project.OrgId,
            "project", id.ToString(), metadata: new { conversationId }, cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PutMemoryAsync(
        Guid id, string key, [FromBody] ProjectMemoryRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var (project, error) = await LoadAsync(id, current, orgs, db, http, ct);
        if (error is not null) return error;
        key = key.Trim();
        if (key.Length is 0 or > 200)
            return SandcastleProblemFactory.Create(http, 422, "invalid memory key");
        if (System.Text.Encoding.UTF8.GetByteCount(req.Value ?? "") > MaxMemoryValueBytes)
            return SandcastleProblemFactory.Create(http, 422, "memory value too large");
        var row = await db.ProjectMemories.FirstOrDefaultAsync(m => m.ProjectId == id && m.Key == key, ct);
        if (row is null)
        {
            if (await db.ProjectMemories.CountAsync(m => m.ProjectId == id, ct) >= MaxProjectMemories)
                return SandcastleProblemFactory.Create(http, 422, "project memory limit reached");
            row = new ProjectMemoryRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = id,
                Key = key,
                Value = req.Value ?? "",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.ProjectMemories.Add(row);
        }
        row.Value = req.Value ?? "";
        row.UpdatedByUserId = current.User!.UserId.ToString();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        project!.UpdatedAt = row.UpdatedAt;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("project.memory.upsert", row.UpdatedByUserId, project.OrgId,
            "project", id.ToString(), metadata: new { key }, cancellationToken: ct);
        return Results.Ok(new { row.Key, row.Value, row.UpdatedAt });
    }

    private static async Task<IResult> DeleteMemoryAsync(
        Guid id, string key, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var (project, error) = await LoadAsync(id, current, orgs, db, http, ct);
        if (error is not null) return error;
        var row = await db.ProjectMemories.FirstOrDefaultAsync(m => m.ProjectId == id && m.Key == key, ct);
        if (row is null) return Results.NotFound();
        db.ProjectMemories.Remove(row);
        project!.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PutFileAsync(
        Guid id, string path, [FromBody] ProjectFileRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var (project, error) = await LoadAsync(id, current, orgs, db, http, ct);
        if (error is not null) return error;
        path = path.Replace('\\', '/').Trim('/');
        if (path.Length is 0 or > 500 || path.Contains("..", StringComparison.Ordinal))
            return SandcastleProblemFactory.Create(http, 422, "invalid project file path");
        var text = req.TextContent ?? "";
        var size = System.Text.Encoding.UTF8.GetByteCount(text);
        if (size > MaxFileBytes)
            return SandcastleProblemFactory.Create(http, 422, "project file too large");
        var row = await db.ProjectFiles.FirstOrDefaultAsync(f => f.ProjectId == id && f.Path == path, ct);
        if (row is null)
        {
            if (await db.ProjectFiles.CountAsync(f => f.ProjectId == id, ct) >= MaxProjectFiles)
                return SandcastleProblemFactory.Create(http, 422, "project file limit reached");
            row = new ProjectFileRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = id,
                Path = path,
                ContentType = string.IsNullOrWhiteSpace(req.ContentType) ? "text/markdown" : req.ContentType!,
                TextContent = text,
                SizeBytes = size,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.ProjectFiles.Add(row);
        }
        row.TextContent = text;
        row.SizeBytes = size;
        row.ContentType = string.IsNullOrWhiteSpace(req.ContentType) ? row.ContentType : req.ContentType!;
        row.UpdatedByUserId = current.User!.UserId.ToString();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        project!.UpdatedAt = row.UpdatedAt;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("project.file.upsert", row.UpdatedByUserId, project.OrgId,
            "project", id.ToString(), metadata: new { path, size }, cancellationToken: ct);
        return Results.Ok(new { row.Path, row.ContentType, row.SizeBytes, row.TextContent, row.UpdatedAt });
    }

    private static async Task<IResult> DeleteFileAsync(
        Guid id, string path, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var (project, error) = await LoadAsync(id, current, orgs, db, http, ct);
        if (error is not null) return error;
        path = path.Replace('\\', '/').Trim('/');
        var row = await db.ProjectFiles.FirstOrDefaultAsync(f => f.ProjectId == id && f.Path == path, ct);
        if (row is null) return Results.NotFound();
        db.ProjectFiles.Remove(row);
        project!.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Project create/update payload.</summary>
    public sealed record ProjectUpsertRequest(
        string? Name, string? Description, IReadOnlyList<string>? Repositories,
        string? Scope, bool? StartCoordinator, Guid? AgentProfileId,
        string? CoordinatorMessage);
    /// <summary>Worker attach payload.</summary>
    public sealed record ProjectWorkerRequest(Guid ConversationId);
    /// <summary>Shared memory payload.</summary>
    public sealed record ProjectMemoryRequest(string? Value);
    /// <summary>Shared text file payload.</summary>
    public sealed record ProjectFileRequest(string? ContentType, string? TextContent);
}