using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Identity;
using Sandcastle.Domain.Identity;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-181: persistent declarative agent definitions and versioned prompt packs.
/// Instructions are always redacted before leaving the API; write operations are audited.
/// </summary>
public static class AgentDefinitionEndpoints
{
    public static IEndpointRouteBuilder MapAgentDefinitionEndpoints(
        this IEndpointRouteBuilder app)
    {
        var defs = app.MapGroup("/api/v1/agent-definitions")
            .WithTags("Agents").RequireAuthorization();
        defs.MapGet("/", ListDefinitionsAsync);
        defs.MapPost("/", CreateDefinitionAsync);
        defs.MapGet("/{id:guid}", GetDefinitionAsync);
        defs.MapPut("/{id:guid}", UpdateDefinitionAsync);
        defs.MapDelete("/{id:guid}", DeleteDefinitionAsync);
        defs.MapPost("/{id:guid}/validate", ValidateDefinitionAsync);

        var packs = app.MapGroup("/api/v1/prompt-packs")
            .WithTags("Agents").RequireAuthorization();
        packs.MapGet("/", ListPromptPacksAsync);
        packs.MapPost("/", CreatePromptPackAsync);
        packs.MapGet("/{packId:guid}", GetPromptPackAsync);
        packs.MapPut("/{packId:guid}", UpdatePromptPackAsync);
        packs.MapDelete("/{packId:guid}", DeletePromptPackAsync);
        packs.MapGet("/{packId:guid}/versions", ListPromptPackVersionsAsync);
        packs.MapGet("/{packId:guid}/compare", ComparePromptPackVersionsAsync);
        packs.MapPost("/{packId:guid}/rollback/{version:int}", RollbackPromptPackAsync);
        return app;
    }

    private static async Task<IResult> ListDefinitionsAsync(
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAgentCatalog catalog, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var rows = await VisibleDefinitionRows(user, orgs, db, ct);
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in await catalog.ListAsync(null, null, ct))
        {
            map[item.Name] = CatalogDto(item);
        }
        foreach (var row in rows.Where(r => r.Scope == "org"))
        {
            if (row.Enabled) map[row.Name] = DefinitionDto(row);
        }
        foreach (var row in rows.Where(r => r.Scope == "user"))
        {
            if (row.Enabled) map[row.Name] = DefinitionDto(row);
        }
        return Results.Ok(map.Values.OrderBy(v => NameOf(v), StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<IResult> CreateDefinitionAsync(
        [FromBody] AgentDefinitionRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        if (ValidateDefinition(req, null) is { } error)
            return Results.BadRequest(new { detail = error });
        var scope = await ResolveWriteScopeAsync(req.Scope, user, orgs, db, ct);
        if (scope.Error is not null)
            return Results.Json(new { detail = scope.Error }, statusCode: 403);
        var now = DateTimeOffset.UtcNow;
        var row = new AgentDefinitionRecord
        {
            Id = Guid.NewGuid(),
            Scope = scope.Scope!,
            OwnerId = scope.OwnerId,
            OrgId = scope.OrgId,
            Name = req.Name!.Trim(),
            Description = req.Description!.Trim(),
            Instructions = req.Instructions!.Trim(),
            ToolsJson = Json(req.Tools),
            Model = TrimOrNull(req.Model),
            ModelProfile = TrimOrNull(req.ModelProfile),
            SkillsJson = Json(req.Skills),
            Mode = TrimOrNull(req.Mode),
            PermissionPreset = TrimOrNull(req.PermissionPreset),
            Enabled = req.Enabled ?? true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AgentDefinitions.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("agent_definition.create", user.UserId.ToString(),
            row.OrgId, "agent_definition", row.Id.ToString(), cancellationToken: ct);
        return Results.Created($"/api/v1/agent-definitions/{row.Id}", DefinitionDto(row));
    }

    private static async Task<IResult> GetDefinitionAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var row = await FindVisibleDefinitionAsync(id, current, orgs, db, ct);
        return row is null ? Results.NotFound() : Results.Ok(DefinitionDto(row));
    }

    private static async Task<IResult> UpdateDefinitionAsync(
        Guid id, [FromBody] AgentDefinitionRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var row = await FindVisibleDefinitionAsync(id, current, orgs, db, ct);
        if (row is null) return Results.NotFound();
        if (!await CanWriteAsync(row.Scope, row.OrgId, user, db, ct))
            return Results.Json(new { detail = "forbidden" }, statusCode: 403);
        if (ValidateDefinition(req, row) is { } error)
            return Results.BadRequest(new { detail = error });

        if (req.Name is not null) row.Name = req.Name.Trim();
        if (req.Description is not null) row.Description = req.Description.Trim();
        if (req.Instructions is not null) row.Instructions = req.Instructions.Trim();
        if (req.Tools is not null) row.ToolsJson = Json(req.Tools);
        if (req.Model is not null) row.Model = TrimOrNull(req.Model);
        if (req.ModelProfile is not null) row.ModelProfile = TrimOrNull(req.ModelProfile);
        if (req.Skills is not null) row.SkillsJson = Json(req.Skills);
        if (req.Mode is not null) row.Mode = TrimOrNull(req.Mode);
        if (req.PermissionPreset is not null) row.PermissionPreset = TrimOrNull(req.PermissionPreset);
        if (req.Enabled.HasValue) row.Enabled = req.Enabled.Value;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("agent_definition.update", user.UserId.ToString(),
            row.OrgId, "agent_definition", row.Id.ToString(), cancellationToken: ct);
        return Results.Ok(DefinitionDto(row));
    }

    private static async Task<IResult> DeleteDefinitionAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var row = await FindVisibleDefinitionAsync(id, current, orgs, db, ct);
        if (row is null) return Results.NotFound();
        if (!await CanWriteAsync(row.Scope, row.OrgId, user, db, ct))
            return Results.Json(new { detail = "forbidden" }, statusCode: 403);
        db.AgentDefinitions.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("agent_definition.delete", user.UserId.ToString(),
            row.OrgId, "agent_definition", row.Id.ToString(), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ValidateDefinitionAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IToolRegistry tools, CancellationToken ct)
    {
        var row = await FindVisibleDefinitionAsync(id, current, orgs, db, ct);
        if (row is null) return Results.NotFound();
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(row.Name)) issues.Add("name_required");
        if (string.IsNullOrWhiteSpace(row.Description)) issues.Add("description_required");
        if (string.IsNullOrWhiteSpace(row.Instructions)) issues.Add("instructions_required");
        if (row.Mode is not null && row.Mode is not ("build" or "plan"))
            issues.Add("mode_invalid");
        var registered = tools.All.Select(t => t.Schema.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownTools = new List<string>();
        foreach (var tool in ParseJsonList(row.ToolsJson) ?? [])
        {
            if (!registered.Contains(tool))
            {
                unknownTools.Add(tool);
                issues.Add($"unknown_tool:{tool}");
            }
        }
        return Results.Ok(new
        {
            valid = issues.Count == 0,
            errors = issues,
            unknown_tools = unknownTools,
        });
    }

    private static async Task<IResult> ListPromptPacksAsync(
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var rows = await VisiblePromptPacks(user, orgs, db, ct);
        return Results.Ok(rows.Where(p => p.Active).Select(PromptPackDto));
    }

    private static async Task<IResult> CreatePromptPackAsync(
        [FromBody] PromptPackRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new { detail = "name_required" });
        if (string.IsNullOrWhiteSpace(req.BasePrompt))
            return Results.BadRequest(new { detail = "base_prompt_required" });
        var scope = await ResolveWriteScopeAsync(req.Scope, user, orgs, db, ct);
        if (scope.Error is not null)
            return Results.Json(new { detail = scope.Error }, statusCode: 403);
        var packId = Guid.NewGuid();
        var row = NewPackVersion(req, scope, packId, 1);
        db.PromptPacks.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("prompt_pack.create", user.UserId.ToString(),
            row.OrgId, "prompt_pack", packId.ToString(),
            metadata: new { version = 1 }, cancellationToken: ct);
        return Results.Created($"/api/v1/prompt-packs/{packId}", PromptPackDto(row));
    }

    private static async Task<IResult> GetPromptPackAsync(
        Guid packId, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var row = await ActiveVisiblePackAsync(packId, current, orgs, db, ct);
        return row is null ? Results.NotFound() : Results.Ok(PromptPackDto(row));
    }

    private static async Task<IResult> UpdatePromptPackAsync(
        Guid packId, [FromBody] PromptPackRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var versions = await VisiblePackVersionsAsync(packId, current, orgs, db, ct);
        var active = versions.FirstOrDefault(p => p.Active);
        if (active is null) return Results.NotFound();
        if (!await CanWriteAsync(active.Scope, active.OrgId, user, db, ct))
            return Results.Json(new { detail = "forbidden" }, statusCode: 403);
        var merged = MergedPackRequest(req, active);
        if (string.IsNullOrWhiteSpace(merged.BasePrompt))
            return Results.BadRequest(new { detail = "base_prompt_required" });
        foreach (var row in versions.Where(p => p.Active)) row.Active = false;
        var next = NewPackVersion(merged,
            (active.Scope, active.OwnerId, active.OrgId, null), packId,
            versions.Max(p => p.Version) + 1);
        db.PromptPacks.Add(next);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("prompt_pack.update", user.UserId.ToString(),
            next.OrgId, "prompt_pack", packId.ToString(),
            metadata: new { version = next.Version }, cancellationToken: ct);
        return Results.Ok(PromptPackDto(next));
    }

    private static async Task<IResult> DeletePromptPackAsync(
        Guid packId, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var versions = await VisiblePackVersionsAsync(packId, current, orgs, db, ct);
        var active = versions.FirstOrDefault(p => p.Active);
        if (active is null) return Results.NotFound();
        if (!await CanWriteAsync(active.Scope, active.OrgId, user, db, ct))
            return Results.Json(new { detail = "forbidden" }, statusCode: 403);
        db.PromptPacks.RemoveRange(versions);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("prompt_pack.delete", user.UserId.ToString(),
            active.OrgId, "prompt_pack", packId.ToString(), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListPromptPackVersionsAsync(
        Guid packId, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var versions = await VisiblePackVersionsAsync(packId, current, orgs, db, ct);
        return versions.Count == 0
            ? Results.NotFound()
            : Results.Ok(versions.Select(PromptPackDto));
    }

    private static async Task<IResult> ComparePromptPackVersionsAsync(
        Guid packId, int from, int to,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var versions = await VisiblePackVersionsAsync(packId, current, orgs, db, ct);
        var a = versions.FirstOrDefault(p => p.Version == from);
        var b = versions.FirstOrDefault(p => p.Version == to);
        if (a is null || b is null) return Results.NotFound();
        var before = PackText(a).Split('\n');
        var after = PackText(b).Split('\n');
        return Results.Ok(new
        {
            from,
            to,
            added = after.Except(before).Select(line => SecretRedactor.Redact(line)).ToArray(),
            removed = before.Except(after).Select(line => SecretRedactor.Redact(line)).ToArray(),
        });
    }

    private static async Task<IResult> RollbackPromptPackAsync(
        Guid packId, int version,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var versions = await VisiblePackVersionsAsync(packId, current, orgs, db, ct);
        var target = versions.FirstOrDefault(p => p.Version == version);
        var active = versions.FirstOrDefault(p => p.Active);
        if (target is null || active is null) return Results.NotFound();
        if (!await CanWriteAsync(active.Scope, active.OrgId, user, db, ct))
            return Results.Json(new { detail = "forbidden" }, statusCode: 403);
        foreach (var row in versions.Where(p => p.Active)) row.Active = false;
        var rollback = new PromptPackRecord
        {
            Id = Guid.NewGuid(),
            PackId = packId,
            Scope = target.Scope,
            OwnerId = target.OwnerId,
            OrgId = target.OrgId,
            Name = target.Name,
            BasePrompt = target.BasePrompt,
            HarnessRules = target.HarnessRules,
            PlanModePrompt = target.PlanModePrompt,
            BuildModePrompt = target.BuildModePrompt,
            AdvisorPrompt = target.AdvisorPrompt,
            PermissionJudgePrompt = target.PermissionJudgePrompt,
            Version = versions.Max(p => p.Version) + 1,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.PromptPacks.Add(rollback);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("prompt_pack.rollback", user.UserId.ToString(),
            rollback.OrgId, "prompt_pack", packId.ToString(),
            metadata: new { from_version = active.Version, to_version = version, version = rollback.Version },
            cancellationToken: ct);
        return Results.Ok(PromptPackDto(rollback));
    }

    private static async Task<List<AgentDefinitionRecord>> VisibleDefinitionRows(
        AuthUser user, IEffectiveOrgResolver orgs, AppDbContext db, CancellationToken ct)
    {
        var org = await orgs.ResolveAsync(user, null, ct);
        var uid = user.UserId.ToString();
        return await db.AgentDefinitions.Where(p =>
            (p.Scope == "user" && p.OwnerId == uid)
            || (p.Scope == "org" && org != null && p.OrgId == org.OrgId))
            .OrderBy(p => p.Name).ToListAsync(ct);
    }

    private static async Task<AgentDefinitionRecord?> FindVisibleDefinitionAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return null;
        return (await VisibleDefinitionRows(user, orgs, db, ct)).FirstOrDefault(p => p.Id == id);
    }

    private static async Task<List<PromptPackRecord>> VisiblePromptPacks(
        AuthUser user, IEffectiveOrgResolver orgs, AppDbContext db, CancellationToken ct)
    {
        var org = await orgs.ResolveAsync(user, null, ct);
        var uid = user.UserId.ToString();
        return await db.PromptPacks.Where(p =>
            (p.Scope == "user" && p.OwnerId == uid)
            || (p.Scope == "org" && org != null && p.OrgId == org.OrgId))
            .OrderBy(p => p.Name).ThenByDescending(p => p.Version).ToListAsync(ct);
    }

    private static async Task<List<PromptPackRecord>> VisiblePackVersionsAsync(
        Guid packId, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return [];
        return (await VisiblePromptPacks(user, orgs, db, ct))
            .Where(p => p.PackId == packId)
            .OrderBy(p => p.Version).ToList();
    }

    private static async Task<PromptPackRecord?> ActiveVisiblePackAsync(
        Guid packId, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct) =>
        (await VisiblePackVersionsAsync(packId, current, orgs, db, ct))
            .FirstOrDefault(p => p.Active);

    private static string? ValidateDefinition(AgentDefinitionRequest req, AgentDefinitionRecord? existing)
    {
        if (existing is null && string.IsNullOrWhiteSpace(req.Name)) return "name_required";
        if (existing is null && string.IsNullOrWhiteSpace(req.Description)) return "description_required";
        if (existing is null && string.IsNullOrWhiteSpace(req.Instructions)) return "instructions_required";
        if (req.Mode is not null && req.Mode is not ("build" or "plan")) return "mode_invalid";
        return null;
    }

    private static PromptPackRequest MergedPackRequest(
        PromptPackRequest req, PromptPackRecord active) => new()
        {
            Name = req.Name ?? active.Name,
            BasePrompt = req.BasePrompt ?? active.BasePrompt,
            HarnessRules = req.HarnessRules ?? active.HarnessRules,
            PlanModePrompt = req.PlanModePrompt ?? active.PlanModePrompt,
            BuildModePrompt = req.BuildModePrompt ?? active.BuildModePrompt,
            AdvisorPrompt = req.AdvisorPrompt ?? active.AdvisorPrompt,
            PermissionJudgePrompt = req.PermissionJudgePrompt ?? active.PermissionJudgePrompt,
        };

    private static PromptPackRecord NewPackVersion(
        PromptPackRequest req,
        (string? Scope, string? OwnerId, Guid? OrgId, string? Error) scope,
        Guid packId, int version) => new()
        {
            Id = Guid.NewGuid(),
            PackId = packId,
            Scope = scope.Scope!,
            OwnerId = scope.OwnerId,
            OrgId = scope.OrgId,
            Name = req.Name!.Trim(),
            BasePrompt = req.BasePrompt!.Trim(),
            HarnessRules = TrimOrNull(req.HarnessRules),
            PlanModePrompt = TrimOrNull(req.PlanModePrompt),
            BuildModePrompt = TrimOrNull(req.BuildModePrompt),
            AdvisorPrompt = TrimOrNull(req.AdvisorPrompt),
            PermissionJudgePrompt = TrimOrNull(req.PermissionJudgePrompt),
            Version = version,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static string PackText(PromptPackRecord p) => string.Join('\n',
        new[] { p.BasePrompt, p.HarnessRules, p.PlanModePrompt, p.BuildModePrompt,
            p.AdvisorPrompt, p.PermissionJudgePrompt }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    private static async Task<(string? Scope, string? OwnerId, Guid? OrgId,
        string? Error)> ResolveWriteScopeAsync(
        string? scope, AuthUser user, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope == "user")
            return ("user", user.UserId.ToString(), null, null);
        if (scope != "org") return (null, null, null, "invalid_scope");
        var org = await orgs.ResolveAsync(user, null, ct);
        if (org is null) return (null, null, null, "no_org");
        var role = await db.OrgMembers.Where(m => m.OrgId == org.OrgId
            && m.UserId == user.UserId)
            .Select(m => m.Role).FirstOrDefaultAsync(ct);
        return role is RoleName.Owner or RoleName.Admin
            ? ("org", null, org.OrgId, null)
            : (null, null, null, "org_admin_required");
    }

    private static async Task<bool> CanWriteAsync(
        string scope, Guid? orgId, AuthUser user,
        AppDbContext db, CancellationToken ct)
    {
        if (scope == "user") return true;
        if (orgId is null) return false;
        var role = await db.OrgMembers.Where(m => m.OrgId == orgId
            && m.UserId == user.UserId)
            .Select(m => m.Role).FirstOrDefaultAsync(ct);
        return role is RoleName.Owner or RoleName.Admin;
    }

    private static string NameOf(object dto) =>
        (string)dto.GetType().GetProperty("Name")!.GetValue(dto)!;

    private static object CatalogDto(AgentDefinition a) => new
    {
        Id = (Guid?)null,
        a.Name,
        a.Description,
        instructions = SecretRedactor.Redact(a.Body),
        tools = a.Tools,
        a.Model,
        model_profile = a.ModelProfile,
        skills = a.Skills,
        a.Mode,
        permission_preset = a.PermissionPreset,
        source = a.Source,
        persisted = false,
    };

    private static object DefinitionDto(AgentDefinitionRecord p) => new
    {
        p.Id,
        p.Scope,
        p.Name,
        p.Description,
        instructions = SecretRedactor.Redact(p.Instructions),
        tools = ParseJsonList(p.ToolsJson),
        p.Model,
        model_profile = p.ModelProfile,
        skills = ParseJsonList(p.SkillsJson),
        p.Mode,
        permission_preset = p.PermissionPreset,
        p.Enabled,
        source = p.Scope,
        persisted = true,
        p.CreatedAt,
        p.UpdatedAt,
    };

    private static object PromptPackDto(PromptPackRecord p) => new
    {
        p.Id,
        p.PackId,
        p.Scope,
        p.Name,
        base_prompt = SecretRedactor.Redact(p.BasePrompt),
        harness_rules = RedactOrNull(p.HarnessRules),
        plan_mode_prompt = RedactOrNull(p.PlanModePrompt),
        build_mode_prompt = RedactOrNull(p.BuildModePrompt),
        advisor_prompt = RedactOrNull(p.AdvisorPrompt),
        permission_judge_prompt = RedactOrNull(p.PermissionJudgePrompt),
        p.Version,
        p.Active,
        p.CreatedAt,
    };

    private static string? RedactOrNull(string? value) =>
        value is null ? null : SecretRedactor.Redact(value);

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Json<T>(T? value) =>
        value is null ? null : JsonSerializer.Serialize(value);

    private static List<string>? ParseJsonList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<string>>(json); }
        catch (JsonException) { return null; }
    }

    public sealed class AgentDefinitionRequest
    {
        public string? Scope { get; init; }
        public string? Name { get; init; }
        public string? Description { get; init; }
        public string? Instructions { get; init; }
        public List<string>? Tools { get; init; }
        public string? Model { get; init; }
        public string? ModelProfile { get; init; }
        public List<string>? Skills { get; init; }
        public string? Mode { get; init; }
        public string? PermissionPreset { get; init; }
        public bool? Enabled { get; init; }
    }

    public sealed class PromptPackRequest
    {
        public string? Scope { get; init; }
        public string? Name { get; init; }
        public string? BasePrompt { get; init; }
        public string? HarnessRules { get; init; }
        public string? PlanModePrompt { get; init; }
        public string? BuildModePrompt { get; init; }
        public string? AdvisorPrompt { get; init; }
        public string? PermissionJudgePrompt { get; init; }
    }
}