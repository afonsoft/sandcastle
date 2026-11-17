using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Identity;
using Sandcastle.Infrastructure.Notifications;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>
/// SPEC-26 RF-001/API Contract: preferências do usuário corrente
/// (<c>GET/PUT /users/me/preferences</c>) — merge raso em
/// <c>UserRecord.PreferencesJson</c>; chaves conhecidas hoje: <c>theme</c>.
/// </summary>
public static class UserEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(
        JsonSerializerDefaults.Web);

    /// <summary>Map <c>/api/v1/users/me</c> endpoints.</summary>
    public static IEndpointRouteBuilder MapUserEndpoints(
        this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/users/me").RequireAuthorization();

        g.MapGet("/preferences", async (
            ICurrentUserAccessor current, AppDbContext db,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var rec = await db.Users.FindAsync([user.UserId], ct);
            return Results.Ok(Parse(rec?.PreferencesJson));
        });

        g.MapPut("/preferences", async (
            [FromBody] JsonElement body,
            ICurrentUserAccessor current, AppDbContext db,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (body.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(
                    new { detail = "preferences must be a JSON object" });
            }

            var rec = await db.Users.FindAsync([user.UserId], ct);
            if (rec is null)
            {
                return Results.NotFound();
            }

            // Merge raso: chaves do body sobrescrevem; demais preservadas.
            var merged = Parse(rec.PreferencesJson);
            foreach (var prop in body.EnumerateObject())
            {
                // whitelist — preferências nunca carregam payload arbitrário.
                // SPEC-147 RF-002: git_author_name/git_author_email/git_coauthor
                // alimentam a cadeia user→org→global de autoria de commit.
                if (prop.Name is not ("theme" or "sidebar_collapsed"
                    or "git_author_name" or "git_author_email" or "git_coauthor"))
                {
                    continue;
                }

                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    merged[prop.Name] = prop.Value.GetString()!;
                }
                else if (prop.Name == "git_coauthor"
                    && prop.Value.ValueKind
                        is JsonValueKind.True or JsonValueKind.False)
                {
                    merged[prop.Name] = prop.Value.GetBoolean()
                        ? "true" : "false";
                }
            }

            rec.PreferencesJson = JsonSerializer.Serialize(merged, JsonOpts);
            await db.SaveChangesAsync(ct);
            return Results.Ok(merged);
        });

        // ===== SPEC-158 RF-002: perfil =====

        g.MapPatch("", async (
            [FromBody] JsonElement body,
            ICurrentUserAccessor current, AppDbContext db,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var rec = await db.Users.FindAsync([user.UserId], ct);
            if (rec is null)
            {
                return Results.NotFound();
            }

            if (body.TryGetProperty("display_name", out var dn)
                && dn.ValueKind == JsonValueKind.String)
            {
                var v = dn.GetString()!.Trim();
                if (v.Length is 0 or > 120)
                {
                    return Results.UnprocessableEntity(
                        new { detail = "display_name: 1-120 chars" });
                }

                rec.DisplayName = v;
            }

            if (body.TryGetProperty("avatar_url", out var av))
            {
                if (av.ValueKind == JsonValueKind.Null)
                {
                    rec.AvatarUrl = null;
                }
                else if (av.ValueKind == JsonValueKind.String)
                {
                    var v = av.GetString()!;
                    if (!Uri.TryCreate(v, UriKind.Absolute, out var u)
                        || u.Scheme is not ("https" or "http")
                        || v.Length > 500)
                    {
                        return Results.UnprocessableEntity(
                            new { detail = "avatar_url must be http(s)" });
                    }

                    rec.AvatarUrl = v;
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                id = rec.Id,
                email = rec.Email,
                display_name = rec.DisplayName,
                avatar_url = rec.AvatarUrl,
            });
        });

        // ===== SPEC-158 RF-002: senha (provider local) =====

        g.MapPost("/password", async (
            [FromBody] JsonElement body,
            ICurrentUserAccessor current, IConfiguration config,
            AppDbContext db, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (!string.Equals(config["Auth:Provider"], "local",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(
                    new { detail = "password auth disabled for provider" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var currentPwd = body.TryGetProperty("current_password",
                out var cp) && cp.ValueKind == JsonValueKind.String
                ? cp.GetString()! : null;
            var newPwd = body.TryGetProperty("new_password",
                out var np) && np.ValueKind == JsonValueKind.String
                ? np.GetString()! : null;
            if (newPwd is null || newPwd.Length < 12)
            {
                return Results.UnprocessableEntity(
                    new { detail = "new_password: min 12 chars" });
            }

            var rec = await db.Users.FindAsync([user.UserId], ct);
            if (rec is null)
            {
                return Results.NotFound();
            }

            // Senha só é exigida se já houver uma cadastrada.
            if (rec.PasswordHash is not null
                && !LocalPasswordHasher.Verify(
                    currentPwd ?? "", rec.PasswordHash))
            {
                return Results.UnprocessableEntity(
                    new { detail = "current_password mismatch" });
            }

            rec.PasswordHash = LocalPasswordHasher.Hash(newPwd);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ===== SPEC-158 RF-002: sessões =====
        // Auth é cookie stateless — a única sessão visível é a corrente.

        g.MapGet("/sessions", (
            ICurrentUserAccessor current, HttpContext http) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                sessions = new[]
                {
                    new
                    {
                        id = "current",
                        current = true,
                        auth_type = http.User.FindFirst("auth_type")
                            ?.Value ?? "cookie",
                        ip = http.Connection.RemoteIpAddress?.ToString(),
                        user_agent = http.Request.Headers.UserAgent
                            .ToString(),
                    },
                },
            });
        });

        g.MapDelete("/sessions/{id}", async (
            string id, ICurrentUserAccessor current, HttpContext http) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (id != "current")
            {
                return Results.NotFound();
            }

            await http.SignOutAsync(AuthEndpoints.CookieScheme);
            return Results.NoContent();
        });

        // ===== SPEC-158 RF-003: personal access tokens =====

        g.MapGet("/tokens", async (
            ICurrentUserAccessor current,
            IPersonalAccessTokenService pats, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var list = await pats.ListAsync(user.UserId, ct);
            return Results.Ok(new { tokens = list });
        });

        g.MapPost("/tokens", async (
            [FromBody] JsonElement body,
            ICurrentUserAccessor current,
            IPersonalAccessTokenService pats, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var name = body.TryGetProperty("name", out var n)
                && n.ValueKind == JsonValueKind.String
                ? n.GetString()!.Trim() : null;
            if (name is null || name.Length is 0 or > 120)
            {
                return Results.UnprocessableEntity(
                    new { detail = "name: 1-120 chars" });
            }

            string[] scopes = ["read"];
            if (body.TryGetProperty("scopes", out var sc)
                && sc.ValueKind == JsonValueKind.Array)
            {
                scopes = sc.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(v => v is "read" or "write")
                    .Distinct().ToArray();
                if (scopes.Length == 0)
                {
                    return Results.UnprocessableEntity(
                        new { detail = "scopes: read|write" });
                }
            }

            DateTimeOffset? expiresAt = null;
            if (body.TryGetProperty("expires_in_days", out var ex)
                && ex.ValueKind == JsonValueKind.Number
                && ex.TryGetInt32(out var days) && days > 0)
            {
                expiresAt = DateTimeOffset.UtcNow.AddDays(days);
            }

            var (id, raw) = await pats.CreateAsync(
                user.UserId, name, scopes, expiresAt, ct);
            return Results.Created($"/api/v1/users/me/tokens/{id}", new
            {
                id,
                token = raw, // exibido uma única vez — nunca persistido.
                name,
                scopes,
                expires_at = expiresAt,
            });
        });

        g.MapDelete("/tokens/{id:guid}", async (
            Guid id, ICurrentUserAccessor current,
            IPersonalAccessTokenService pats, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            return await pats.RevokeAsync(user.UserId, id, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        // ===== SPEC-158 RF-004: notificações =====

        g.MapGet("/notification-prefs", async (
            ICurrentUserAccessor current, AppDbContext db,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var prefsJson = await db.Users
                .Where(u => u.Id == user.UserId)
                .Select(u => u.PreferencesJson)
                .FirstOrDefaultAsync(ct);
            return Results.Ok(
                UserNotificationService.ReadPrefs(prefsJson));
        });

        g.MapPut("/notification-prefs", async (
            [FromBody] JsonElement body,
            ICurrentUserAccessor current,
            IUserNotificationService notify,
            AppDbContext db, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var rec = await db.Users.FindAsync([user.UserId], ct);
            if (rec is null)
            {
                return Results.NotFound();
            }

            var webhook = body.TryGetProperty("webhook_url", out var w)
                && w.ValueKind == JsonValueKind.String
                    ? w.GetString() : null;
            if (webhook is { Length: > 0 })
            {
                var (ok, error) = await notify.ValidateUrlAsync(
                    webhook, ct);
                if (!ok)
                {
                    return Results.UnprocessableEntity(
                        new { detail = error });
                }
            }

            var merged = Parse(rec.PreferencesJson);
            merged["notifications"] = JsonSerializer.Serialize(new
            {
                run_finished = body.TryGetProperty("run_finished",
                    out var rf) && rf.ValueKind is JsonValueKind.True
                        or JsonValueKind.False && rf.GetBoolean(),
                run_failed = body.TryGetProperty("run_failed",
                    out var rd) && rd.ValueKind is JsonValueKind.True
                        or JsonValueKind.False && rd.GetBoolean(),
                webhook_url = string.IsNullOrEmpty(webhook)
                    ? null : webhook,
            }, JsonOpts);
            rec.PreferencesJson = JsonSerializer.Serialize(merged, JsonOpts);
            await db.SaveChangesAsync(ct);
            return Results.Ok(
                UserNotificationService.ReadPrefs(rec.PreferencesJson));
        });

        g.MapPost("/notifications/test", async (
            ICurrentUserAccessor current,
            IUserNotificationService notify,
            AppDbContext db, CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var prefsJson = await db.Users
                .Where(u => u.Id == user.UserId)
                .Select(u => u.PreferencesJson)
                .FirstOrDefaultAsync(ct);
            var prefs = UserNotificationService.ReadPrefs(prefsJson);
            if (prefs.WebhookUrl is null)
            {
                return Results.UnprocessableEntity(
                    new { detail = "no webhook_url configured" });
            }

            var (ok, error) = await notify.SendTestAsync(
                prefs.WebhookUrl, ct);
            return ok
                ? Results.Ok(new { sent = true })
                : Results.UnprocessableEntity(new { detail = error });
        });

        // ===== SPEC-158 RF-005: export =====

        g.MapGet("/export", async (
            ICurrentUserAccessor current, AppDbContext db,
            IPersonalAccessTokenService pats,
            CancellationToken ct) =>
        {
            var user = current.User;
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var rec = await db.Users.FindAsync([user.UserId], ct);
            if (rec is null)
            {
                return Results.NotFound();
            }

            var tokens = await pats.ListAsync(user.UserId, ct);
            var workspaceItems = await db.WorkspaceItems
                .Where(i => i.OwnerId == rec.Id.ToString())
                .Select(i => new
                {
                    i.Id,
                    i.Kind,
                    i.Name,
                    i.BodyJson,
                    i.CreatedAt,
                })
                .ToListAsync(ct);
            var export = new
            {
                exported_at = DateTimeOffset.UtcNow,
                profile = new
                {
                    rec.Id,
                    rec.Email,
                    rec.DisplayName,
                    rec.AvatarUrl,
                },
                preferences = Parse(rec.PreferencesJson),
                notification_prefs = UserNotificationService
                    .ReadPrefs(rec.PreferencesJson),
                personal_access_tokens = tokens,
                workspace_items = workspaceItems,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                export, JsonOpts);
            return Results.File(bytes, "application/json",
                $"sandcastle-export-{DateTimeOffset.UtcNow:yyyyMMdd}.json");
        });

        return app;
    }

    private static Dictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer
                .Deserialize<Dictionary<string, string>>(json, JsonOpts)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}