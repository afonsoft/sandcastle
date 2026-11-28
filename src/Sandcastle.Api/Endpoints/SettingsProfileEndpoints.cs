using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Contracts;
using Sandcastle.Application.Identity;
using Sandcastle.Domain.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-180: provider connections, LLM profiles and agent profiles.
/// Secrets are write-only values stored through <see cref="ISecretStore"/>;
/// rows and responses carry references only.
/// </summary>
public static class SettingsProfileEndpoints
{
    public static IEndpointRouteBuilder MapSettingsProfileEndpoints(
        this IEndpointRouteBuilder app)
    {
        var providers = app.MapGroup("/api/v1/provider-connections")
            .WithTags("Settings").RequireAuthorization();
        providers.MapGet("/providers", () => Results.Ok(
            ProviderConnectionTypes.Catalog));
        providers.MapGet("/", ListProviderConnectionsAsync);
        providers.MapPost("/", CreateProviderConnectionAsync);
        providers.MapGet("/{id:guid}", GetProviderConnectionAsync);
        providers.MapPut("/{id:guid}", UpdateProviderConnectionAsync);
        providers.MapDelete("/{id:guid}", DeleteProviderConnectionAsync);
        providers.MapPost("/{id:guid}/test", TestProviderConnectionAsync);
        providers.MapGet("/{id:guid}/models", ListProviderModelsAsync);

        var llm = app.MapGroup("/api/v1/llm-profiles")
            .WithTags("Settings").RequireAuthorization();
        llm.MapGet("/", ListLlmProfilesAsync);
        llm.MapPost("/", CreateLlmProfileAsync);
        llm.MapGet("/{id:guid}", GetLlmProfileAsync);
        llm.MapPut("/{id:guid}", UpdateLlmProfileAsync);
        llm.MapDelete("/{id:guid}", DeleteLlmProfileAsync);

        var agents = app.MapGroup("/api/v1/agent-profiles")
            .WithTags("Settings").RequireAuthorization();
        agents.MapGet("/", ListAgentProfilesAsync);
        agents.MapPost("/", CreateAgentProfileAsync);
        agents.MapGet("/{id:guid}", GetAgentProfileAsync);
        agents.MapPut("/{id:guid}", UpdateAgentProfileAsync);
        agents.MapPost("/{id:guid}/duplicate", DuplicateAgentProfileAsync);
        agents.MapDelete("/{id:guid}", DeleteAgentProfileAsync);
        return app;
    }

    private static async Task<IResult> ListProviderConnectionsAsync(
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var org = await orgs.ResolveAsync(user, null, ct);
        var uid = user.UserId.ToString();
        var rows = await db.ProviderConnections.AsNoTracking()
            .Where(p => (p.Scope == "user" && p.OwnerId == uid)
                || (p.Scope == "org" && org != null && p.OrgId == org.OrgId))
            .OrderBy(p => p.Name).ToListAsync(ct);
        return Results.Ok(rows.Select(ToProviderDto));
    }

    private static async Task<IResult> CreateProviderConnectionAsync(
        [FromBody] ProviderConnectionRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, ISecretStore secrets, IAuditService audit,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        if (ValidateProviderRequest(req, null) is { } error)
            return Results.BadRequest(new { detail = error });
        var scope = await ResolveWriteScopeAsync(
            req.Scope, user, orgs, db, ct);
        if (scope.Error is not null) return Results.Json(
            new { detail = scope.Error }, statusCode: 403);

        var now = DateTimeOffset.UtcNow;
        var row = new ProviderConnectionRecord
        {
            Id = Guid.NewGuid(),
            Scope = scope.Scope!,
            OwnerId = scope.OwnerId,
            OrgId = scope.OrgId,
            Name = req.Name!.Trim(),
            Provider = req.Provider!.Trim().ToLowerInvariant(),
            BaseUrl = NormalizedBaseUrl(req),
            Enabled = req.Enabled ?? true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (!string.IsNullOrWhiteSpace(req.SecretValue))
        {
            row.SecretRef = $"provider_connection_{row.Id:N}";
            await secrets.SetAsync(scope.Scope!, scope.SecretOwnerId!,
                row.SecretRef, req.SecretValue, ct);
        }
        else if (!string.IsNullOrWhiteSpace(req.SecretRef))
        {
            var existing = await secrets.ResolveAsync(scope.Scope!,
                scope.SecretOwnerId!, req.SecretRef.Trim(), ct);
            if (existing is null)
                return Results.BadRequest(new { detail = "secret_ref_not_found" });
            row.SecretRef = req.SecretRef.Trim();
        }

        db.ProviderConnections.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("provider_connection.create",
            user.UserId.ToString(), row.OrgId, "provider_connection",
            row.Id.ToString(), cancellationToken: ct);
        return Results.Created($"/api/v1/provider-connections/{row.Id}",
            ToProviderDto(row));
    }

    private static async Task<IResult> GetProviderConnectionAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var row = await FindVisibleProviderAsync(id, current, orgs, db, ct);
        return row is null ? Results.NotFound() : Results.Ok(ToProviderDto(row));
    }

    private static async Task<IResult> UpdateProviderConnectionAsync(
        Guid id, [FromBody] ProviderConnectionRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, ISecretStore secrets, IAuditService audit,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var row = await FindVisibleProviderAsync(id, current, orgs, db, ct);
        if (row is null) return Results.NotFound();
        if (!await CanWriteAsync(row.Scope, row.OrgId, user, db, ct))
            return Results.Json(new { detail = "forbidden" }, statusCode: 403);
        if (ValidateProviderRequest(req, row) is { } error)
            return Results.BadRequest(new { detail = error });

        if (!string.IsNullOrWhiteSpace(req.Name)) row.Name = req.Name.Trim();
        if (!string.IsNullOrWhiteSpace(req.Provider))
            row.Provider = req.Provider.Trim().ToLowerInvariant();
        if (req.BaseUrl is not null)
            row.BaseUrl = string.IsNullOrWhiteSpace(req.BaseUrl)
                ? null : req.BaseUrl.Trim().TrimEnd('/');
        if (req.Enabled.HasValue) row.Enabled = req.Enabled.Value;
        if (!string.IsNullOrWhiteSpace(req.SecretValue))
        {
            row.SecretRef ??= $"provider_connection_{row.Id:N}";
            await secrets.SetAsync(row.Scope, SecretOwner(row),
                row.SecretRef, req.SecretValue, ct);
        }
        else if (!string.IsNullOrWhiteSpace(req.SecretRef))
        {
            var existing = await secrets.ResolveAsync(row.Scope,
                SecretOwner(row), req.SecretRef.Trim(), ct);
            if (existing is null)
                return Results.BadRequest(new { detail = "secret_ref_not_found" });
            row.SecretRef = req.SecretRef.Trim();
        }
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("provider_connection.update",
            user.UserId.ToString(), row.OrgId, "provider_connection",
            row.Id.ToString(), cancellationToken: ct);
        return Results.Ok(ToProviderDto(row));
    }

    private static async Task<IResult> DeleteProviderConnectionAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, ISecretStore secrets, IAuditService audit,
        CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var row = await FindVisibleProviderAsync(id, current, orgs, db, ct);
        if (row is null) return Results.NotFound();
        if (!await CanWriteAsync(row.Scope, row.OrgId, user, db, ct))
            return Results.Json(new { detail = "forbidden" }, statusCode: 403);
        var inUse = await db.LlmProfiles.AnyAsync(
            p => p.ProviderConnectionId == id, ct);
        if (inUse) return Results.Conflict(new { detail = "provider_connection_in_use" });
        if (row.SecretRef is not null)
            await secrets.DeleteAsync(row.Scope, SecretOwner(row), row.SecretRef, ct);
        db.ProviderConnections.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("provider_connection.delete",
            user.UserId.ToString(), row.OrgId, "provider_connection",
            row.Id.ToString(), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TestProviderConnectionAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, ISecretStore secrets, IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var probe = await ProbeProviderAsync(id, current, orgs, db, secrets,
            httpFactory, ct);
        return probe.Status is null
            ? Results.Ok(new
            {
                ok = probe.Ok,
                detail = probe.Detail,
                model_count = probe.Models.Count,
            })
            : probe.Status;
    }

    private static async Task<IResult> ListProviderModelsAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, ISecretStore secrets, IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        var probe = await ProbeProviderAsync(id, current, orgs, db, secrets,
            httpFactory, ct);
        if (probe.Status is not null) return probe.Status;
        return probe.Ok
            ? Results.Ok(new { models = probe.Models })
            : Results.Json(new { detail = probe.Detail }, statusCode: 502);
    }

    private static async Task<ProviderProbe> ProbeProviderAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, ISecretStore secrets, IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        if (current.User is null)
            return new(false, "unauthorized", [], Results.Unauthorized());
        var row = await FindVisibleProviderAsync(id, current, orgs, db, ct);
        if (row is null)
            return new(false, "not_found", [], Results.NotFound());
        if (!row.Enabled) return new(false, "connection_disabled", []);
        var descriptor = ProviderConnectionTypes.Find(row.Provider)!;
        var key = row.SecretRef is null ? null : await secrets.ResolveAsync(
            row.Scope, SecretOwner(row), row.SecretRef, ct);
        if (descriptor.RequiresSecret && key is null)
            return new(false, "secret_not_configured", []);

        try
        {
            var client = httpFactory.CreateClient("agent-llm");
            client.Timeout = TimeSpan.FromSeconds(10);
            var baseUrl = EffectiveBaseUrl(row, descriptor);
            var path = row.Provider == "ollama" ? "/api/tags" : "/models";
            using var request = new HttpRequestMessage(
                HttpMethod.Get, baseUrl.TrimEnd('/') + path);
            if (key is not null && row.Provider == "anthropic")
            {
                request.Headers.TryAddWithoutValidation("x-api-key", key);
                request.Headers.TryAddWithoutValidation(
                    "anthropic-version", "2023-06-01");
            }
            else if (key is not null && row.Provider != "ollama")
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", key);
            }

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new(false, $"http_{(int)response.StatusCode}", []);
            await using var stream = await response.Content
                .ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream,
                cancellationToken: ct);
            var models = ParseModels(doc.RootElement);
            return new(true, null, models);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, "unreachable", []);
        }
    }

    private static List<string> ParseModels(JsonElement root)
    {
        var result = new List<string>();
        if (root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String)
                    result.Add(id.GetString()!);
        }
        else if (root.TryGetProperty("models", out var models)
            && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in models.EnumerateArray())
            {
                var value = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.TryGetProperty("name", out var name)
                        && name.ValueKind == JsonValueKind.String
                            ? name.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<IResult> ListLlmProfilesAsync(
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var rows = await VisibleLlmProfiles(user, orgs, db, ct);
        return Results.Ok(rows.Select(ToLlmDto));
    }

    private static async Task<IResult> CreateLlmProfileAsync(
        [FromBody] LlmProfileRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        if (await ValidateLlmRequestAsync(req, null, current, orgs, db, ct) is { } error)
            return Results.BadRequest(new { detail = error });
        var scope = await ResolveWriteScopeAsync(req.Scope, user, orgs, db, ct);
        if (scope.Error is not null) return Results.Json(
            new { detail = scope.Error }, statusCode: 403);
        var now = DateTimeOffset.UtcNow;
        var row = new LlmProfileRecord
        {
            Id = Guid.NewGuid(),
            Scope = scope.Scope!,
            OwnerId = scope.OwnerId,
            OrgId = scope.OrgId,
            Name = req.Name!.Trim(),
            ProviderConnectionId = req.ProviderConnectionId,
            Provider = req.Provider?.Trim().ToLowerInvariant(),
            Model = req.Model!.Trim(),
            Temperature = req.Temperature,
            RoleModelsJson = Json(req.RoleModels),
            FallbackJson = Json(req.FallbackModels),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.LlmProfiles.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("llm_profile.create", user.UserId.ToString(),
            row.OrgId, "llm_profile", row.Id.ToString(), cancellationToken: ct);
        return Results.Created($"/api/v1/llm-profiles/{row.Id}", ToLlmDto(row));
    }

    private static async Task<IResult> GetLlmProfileAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var row = await FindVisibleLlmProfileAsync(id, current, orgs, db, ct);
        return row is null ? Results.NotFound() : Results.Ok(ToLlmDto(row));
    }

    private static async Task<IResult> UpdateLlmProfileAsync(
        Guid id, [FromBody] LlmProfileRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var row = await FindVisibleLlmProfileAsync(id, current, orgs, db, ct);
        if (row is null) return Results.NotFound();
        if (!await CanWriteAsync(row.Scope, row.OrgId, user, db, ct))
            return Results.Json(new { detail = "forbidden" }, statusCode: 403);
        if (await ValidateLlmRequestAsync(req, row, current, orgs, db, ct) is { } error)
            return Results.BadRequest(new { detail = error });
        if (!string.IsNullOrWhiteSpace(req.Name)) row.Name = req.Name.Trim();
        if (req.ProviderConnectionId.HasValue)
            row.ProviderConnectionId = req.ProviderConnectionId;
        if (!string.IsNullOrWhiteSpace(req.Provider))
            row.Provider = req.Provider.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(req.Model)) row.Model = req.Model.Trim();
        if (req.Temperature.HasValue) row.Temperature = req.Temperature;
        if (req.RoleModels is not null) row.RoleModelsJson = Json(req.RoleModels);
        if (req.FallbackModels is not null)
            row.FallbackJson = Json(req.FallbackModels);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToLlmDto(row));
    }

    private static async Task<IResult> DeleteLlmProfileAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var row = await FindVisibleLlmProfileAsync(id, current, orgs, db, ct);
        if (row is null) return Results.NotFound();
        if (!await CanWriteAsync(row.Scope, row.OrgId, user, db, ct))
            return Results.Json(new { detail = "forbidden" }, statusCode: 403);
        var inUse = await db.AgentProfiles.AnyAsync(p => p.LlmProfileId == id, ct)
            || await db.Conversations.AnyAsync(c => c.LlmProfileId == id, ct)
            || await db.Automations.AnyAsync(a => a.LlmProfileId == id, ct);
        if (inUse) return Results.Conflict(new { detail = "llm_profile_in_use" });
        db.LlmProfiles.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("llm_profile.delete", user.UserId.ToString(),
            row.OrgId, "llm_profile", row.Id.ToString(), cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListAgentProfilesAsync(
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var rows = await VisibleAgentProfiles(user, orgs, db, ct);
        return Results.Ok(rows.Select(ToAgentDto));
    }

    private static async Task<IResult> CreateAgentProfileAsync(
        [FromBody] AgentProfileRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        if (await ValidateAgentRequestAsync(req, null, current, orgs, db, ct)
            is { } error)
            return Results.BadRequest(new { detail = error });
        var scope = await ResolveWriteScopeAsync(req.Scope, user, orgs, db, ct);
        if (scope.Error is not null) return Results.Json(
            new { detail = scope.Error }, statusCode: 403);
        var now = DateTimeOffset.UtcNow;
        var row = new AgentProfileRecord
        {
            Id = Guid.NewGuid(),
            Scope = scope.Scope!,
            OwnerId = scope.OwnerId,
            OrgId = scope.OrgId,
            Name = req.Name!.Trim(),
            Description = TrimOrNull(req.Description),
            PromptPack = TrimOrNull(req.PromptPack),
            AgentType = TrimOrNull(req.AgentType),
            Mode = TrimOrNull(req.Mode),
            RuntimeProfile = TrimOrNull(req.RuntimeProfile),
            LlmProfileId = req.LlmProfileId,
            AllowedToolsJson = Json(req.AllowedTools),
            SkillsJson = Json(req.Skills),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AgentProfiles.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("agent_profile.create", user.UserId.ToString(),
            row.OrgId, "agent_profile", row.Id.ToString(), cancellationToken: ct);
        return Results.Created($"/api/v1/agent-profiles/{row.Id}", ToAgentDto(row));
    }

    private static async Task<IResult> GetAgentProfileAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var row = await FindVisibleAgentProfileAsync(id, current, orgs, db, ct);
        return row is null ? Results.NotFound() : Results.Ok(ToAgentDto(row));
    }

    private static async Task<IResult> UpdateAgentProfileAsync(
        Guid id, [FromBody] AgentProfileRequest req,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var row = await FindVisibleAgentProfileAsync(id, current, orgs, db, ct);
        if (row is null) return Results.NotFound();
        if (!await CanWriteAsync(row.Scope, row.OrgId, user, db, ct))
            return Results.Json(new { detail = "forbidden" }, statusCode: 403);
        if (await ValidateAgentRequestAsync(req, row, current, orgs, db, ct)
            is { } error)
            return Results.BadRequest(new { detail = error });
        if (!string.IsNullOrWhiteSpace(req.Name)) row.Name = req.Name.Trim();
        if (req.Description is not null) row.Description = TrimOrNull(req.Description);
        if (req.PromptPack is not null) row.PromptPack = TrimOrNull(req.PromptPack);
        if (!string.IsNullOrWhiteSpace(req.AgentType)) row.AgentType = req.AgentType.Trim();
        if (!string.IsNullOrWhiteSpace(req.Mode)) row.Mode = req.Mode.Trim();
        if (req.RuntimeProfile is not null)
            row.RuntimeProfile = TrimOrNull(req.RuntimeProfile);
        if (req.LlmProfileId.HasValue) row.LlmProfileId = req.LlmProfileId;
        if (req.AllowedTools is not null) row.AllowedToolsJson = Json(req.AllowedTools);
        if (req.Skills is not null) row.SkillsJson = Json(req.Skills);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToAgentDto(row));
    }

    private static async Task<IResult> DuplicateAgentProfileAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var row = await FindVisibleAgentProfileAsync(id, current, orgs, db, ct);
        if (row is null) return Results.NotFound();
        var now = DateTimeOffset.UtcNow;
        var copy = new AgentProfileRecord
        {
            Id = Guid.NewGuid(),
            Scope = "user",
            OwnerId = user.UserId.ToString(),
            OrgId = null,
            Name = $"{row.Name} copy",
            Description = row.Description,
            PromptPack = row.PromptPack,
            AgentType = row.AgentType,
            Mode = row.Mode,
            RuntimeProfile = row.RuntimeProfile,
            LlmProfileId = row.LlmProfileId,
            AllowedToolsJson = row.AllowedToolsJson,
            SkillsJson = row.SkillsJson,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AgentProfiles.Add(copy);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("agent_profile.duplicate",
            user.UserId.ToString(), null, "agent_profile",
            copy.Id.ToString(), cancellationToken: ct);
        return Results.Created($"/api/v1/agent-profiles/{copy.Id}", ToAgentDto(copy));
    }

    private static async Task<IResult> DeleteAgentProfileAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, IAuditService audit, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return Results.Unauthorized();
        var row = await FindVisibleAgentProfileAsync(id, current, orgs, db, ct);
        if (row is null) return Results.NotFound();
        if (!await CanWriteAsync(row.Scope, row.OrgId, user, db, ct))
            return Results.Json(new { detail = "forbidden" }, statusCode: 403);
        var inUse = await db.Conversations.AnyAsync(c => c.AgentProfileId == id, ct)
            || await db.Automations.AnyAsync(a => a.AgentProfileId == id, ct);
        if (inUse) return Results.Conflict(new { detail = "agent_profile_in_use" });
        db.AgentProfiles.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("agent_profile.delete", user.UserId.ToString(),
            row.OrgId, "agent_profile", row.Id.ToString(), cancellationToken: ct);
        return Results.NoContent();
    }

    private static string? ValidateProviderRequest(
        ProviderConnectionRequest req, ProviderConnectionRecord? existing)
    {
        var provider = req.Provider ?? existing?.Provider;
        var descriptor = ProviderConnectionTypes.Find(provider);
        if (descriptor is null) return "unknown_provider";
        if (existing is null && string.IsNullOrWhiteSpace(req.Name))
            return "name_required";
        var baseUrl = req.BaseUrl ?? existing?.BaseUrl;
        if (!ProviderConnectionTypes.IsValidBaseUrl(baseUrl))
            return "base_url_invalid";
        if (descriptor.RequiresBaseUrl && string.IsNullOrWhiteSpace(baseUrl))
            return "base_url_required";
        var secretRef = req.SecretRef ?? existing?.SecretRef;
        if (descriptor.RequiresSecret
            && string.IsNullOrWhiteSpace(req.SecretValue)
            && string.IsNullOrWhiteSpace(secretRef))
            return "secret_required";
        return null;
    }

    private static async Task<string?> ValidateLlmRequestAsync(
        LlmProfileRequest req, LlmProfileRecord? existing,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        if (existing is null && string.IsNullOrWhiteSpace(req.Name))
            return "name_required";
        if (existing is null && string.IsNullOrWhiteSpace(req.Model))
            return "model_required";
        if (req.Temperature is < 0 or > 2) return "temperature_invalid";
        if (req.Provider is not null
            && ProviderConnectionTypes.Find(req.Provider) is null)
            return "unknown_provider";
        if (req.ProviderConnectionId.HasValue
            && await FindVisibleProviderAsync(
                req.ProviderConnectionId.Value, current, orgs, db, ct) is null)
            return "provider_connection_not_found";
        return null;
    }

    private static async Task<string?> ValidateAgentRequestAsync(
        AgentProfileRequest req, AgentProfileRecord? existing,
        ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        if (existing is null && string.IsNullOrWhiteSpace(req.Name))
            return "name_required";
        if (req.AgentType is not null
            && req.AgentType is not ("default" or "plan" or "readonly"))
            return "agent_type_invalid";
        if (req.Mode is not null && req.Mode is not ("build" or "plan"))
            return "mode_invalid";
        if (req.RuntimeProfile is not null
            && !Domain.Sandbox.SandboxProfiles.IsValid(req.RuntimeProfile))
            return "runtime_profile_invalid";
        if (req.LlmProfileId.HasValue
            && await FindVisibleLlmProfileAsync(
                req.LlmProfileId.Value, current, orgs, db, ct) is null)
            return "llm_profile_not_found";
        return null;
    }

    private static async Task<(string? Scope, string? OwnerId, Guid? OrgId,
        string? SecretOwnerId, string? Error)> ResolveWriteScopeAsync(
        string? scope, AuthUser user, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope == "user")
        {
            var uid = user.UserId.ToString();
            return ("user", uid, null, uid, null);
        }
        if (scope != "org") return (null, null, null, null, "invalid_scope");
        var org = await orgs.ResolveAsync(user, null, ct);
        if (org is null) return (null, null, null, null, "no_org");
        var role = await db.OrgMembers.Where(m => m.OrgId == org.OrgId
            && m.UserId == user.UserId)
            .Select(m => m.Role).FirstOrDefaultAsync(ct);
        return role is RoleName.Owner or RoleName.Admin
            ? ("org", null, org.OrgId, org.OrgId.ToString(), null)
            : (null, null, null, null, "org_admin_required");
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

    private static async Task<ProviderConnectionRecord?> FindVisibleProviderAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct)
    {
        var user = current.User;
        if (user is null) return null;
        var org = await orgs.ResolveAsync(user, null, ct);
        var uid = user.UserId.ToString();
        return await db.ProviderConnections.FirstOrDefaultAsync(p => p.Id == id
            && ((p.Scope == "user" && p.OwnerId == uid)
                || (p.Scope == "org" && org != null && p.OrgId == org.OrgId)), ct);
    }

    private static async Task<LlmProfileRecord?> FindVisibleLlmProfileAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct) =>
        (await VisibleLlmProfiles(current.User!, orgs, db, ct))
            .FirstOrDefault(p => p.Id == id);

    private static async Task<AgentProfileRecord?> FindVisibleAgentProfileAsync(
        Guid id, ICurrentUserAccessor current, IEffectiveOrgResolver orgs,
        AppDbContext db, CancellationToken ct) =>
        (await VisibleAgentProfiles(current.User!, orgs, db, ct))
            .FirstOrDefault(p => p.Id == id);

    private static async Task<List<LlmProfileRecord>> VisibleLlmProfiles(
        AuthUser user, IEffectiveOrgResolver orgs, AppDbContext db,
        CancellationToken ct)
    {
        var org = await orgs.ResolveAsync(user, null, ct);
        var uid = user.UserId.ToString();
        return await db.LlmProfiles.AsNoTracking().Where(p =>
            (p.Scope == "user" && p.OwnerId == uid)
            || (p.Scope == "org" && org != null && p.OrgId == org.OrgId))
            .OrderBy(p => p.Name).ToListAsync(ct);
    }

    private static async Task<List<AgentProfileRecord>> VisibleAgentProfiles(
        AuthUser user, IEffectiveOrgResolver orgs, AppDbContext db,
        CancellationToken ct)
    {
        var org = await orgs.ResolveAsync(user, null, ct);
        var uid = user.UserId.ToString();
        return await db.AgentProfiles.AsNoTracking().Where(p =>
            (p.Scope == "user" && p.OwnerId == uid)
            || (p.Scope == "org" && org != null && p.OrgId == org.OrgId))
            .OrderBy(p => p.Name).ToListAsync(ct);
    }

    private static string EffectiveBaseUrl(
        ProviderConnectionRecord row, ProviderConnectionDescriptor descriptor) =>
        !string.IsNullOrWhiteSpace(row.BaseUrl)
            ? row.BaseUrl.TrimEnd('/') : descriptor.DefaultBaseUrl.TrimEnd('/');

    private static string? NormalizedBaseUrl(ProviderConnectionRequest req) =>
        string.IsNullOrWhiteSpace(req.BaseUrl)
            ? ProviderConnectionTypes.Find(req.Provider)?.DefaultBaseUrl is { Length: > 0 } d ? d : null
            : req.BaseUrl.Trim().TrimEnd('/');

    private static string SecretOwner(ProviderConnectionRecord row) =>
        row.Scope == "user" ? row.OwnerId! : row.OrgId!.Value.ToString();

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Json<T>(T? value) =>
        value is null ? null : JsonSerializer.Serialize(value);

    private static object ToProviderDto(ProviderConnectionRecord p) => new
    {
        p.Id,
        p.Scope,
        p.Name,
        p.Provider,
        p.BaseUrl,
        p.SecretRef,
        p.Enabled,
        p.CreatedAt,
        p.UpdatedAt,
    };

    private static object ToLlmDto(LlmProfileRecord p) => new
    {
        p.Id,
        p.Scope,
        p.Name,
        p.ProviderConnectionId,
        p.Provider,
        p.Model,
        p.Temperature,
        role_models = JsonDocument.Parse(p.RoleModelsJson ?? "{}").RootElement,
        fallback_models = JsonDocument.Parse(p.FallbackJson ?? "[]").RootElement,
        p.CreatedAt,
        p.UpdatedAt,
    };

    private static object ToAgentDto(AgentProfileRecord p) => new
    {
        p.Id,
        p.Scope,
        p.Name,
        p.Description,
        p.PromptPack,
        p.AgentType,
        p.Mode,
        p.RuntimeProfile,
        p.LlmProfileId,
        allowed_tools = JsonDocument.Parse(p.AllowedToolsJson ?? "[]").RootElement,
        skills = JsonDocument.Parse(p.SkillsJson ?? "[]").RootElement,
        p.CreatedAt,
        p.UpdatedAt,
    };

    private sealed record ProviderProbe(
        bool Ok, string? Detail, List<string> Models, IResult? Status = null);

    public sealed class ProviderConnectionRequest
    {
        public string? Scope { get; init; }
        public string? Name { get; init; }
        public string? Provider { get; init; }
        public string? BaseUrl { get; init; }
        public string? SecretRef { get; init; }
        public string? SecretValue { get; init; }
        public bool? Enabled { get; init; }
    }

    public sealed class LlmProfileRequest
    {
        public string? Scope { get; init; }
        public string? Name { get; init; }
        public Guid? ProviderConnectionId { get; init; }
        public string? Provider { get; init; }
        public string? Model { get; init; }
        public double? Temperature { get; init; }
        public Dictionary<string, string>? RoleModels { get; init; }
        public List<string>? FallbackModels { get; init; }
    }

    public sealed class AgentProfileRequest
    {
        public string? Scope { get; init; }
        public string? Name { get; init; }
        public string? Description { get; init; }
        public string? PromptPack { get; init; }
        public string? AgentType { get; init; }
        public string? Mode { get; init; }
        public string? RuntimeProfile { get; init; }
        public Guid? LlmProfileId { get; init; }
        public List<string>? AllowedTools { get; init; }
        public List<string>? Skills { get; init; }
    }
}