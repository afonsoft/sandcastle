using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Identity;

/// <inheritdoc cref="IPersonalAccessTokenService" />
public sealed class PersonalAccessTokenService(
    AppDbContext db,
    IAuditService audit) : IPersonalAccessTokenService
{
    /// <summary>Throttle de <c>last_used_at</c> + audit <c>pat.used</c>.</summary>
    private static readonly TimeSpan UsageThrottle = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOpts = new(
        JsonSerializerDefaults.Web);

    /// <summary>SHA-256 hex do token cru — única forma persistida.</summary>
    public static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    /// <inheritdoc />
    public async Task<(Guid Id, string RawToken)> CreateAsync(
        Guid userId, string name, IReadOnlyList<string> scopes,
        DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var raw = IPersonalAccessTokenService.TokenPrefix
            + Convert.ToHexString(RandomNumberGenerator.GetBytes(24))
                .ToLowerInvariant();
        var rec = new PersonalAccessTokenRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Prefix = raw[..12],
            TokenHash = Hash(raw),
            ScopesJson = JsonSerializer.Serialize(scopes, JsonOpts),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
        };
        db.PersonalAccessTokens.Add(rec);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("pat.created", actorUserId: userId.ToString(),
            targetType: "personal_access_token",
            targetId: rec.Id.ToString(),
            metadata: new Dictionary<string, string>
            {
                ["name"] = name,
                ["scopes"] = string.Join(' ', scopes),
            }, cancellationToken: ct);
        return (rec.Id, raw);
    }

    /// <inheritdoc />
    public async Task<PatResolution?> ValidateAsync(
        string rawToken, CancellationToken ct)
    {
        var rec = await db.PersonalAccessTokens
            .FirstOrDefaultAsync(t => t.TokenHash == Hash(rawToken), ct);
        if (rec is null || rec.RevokedAt is not null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (rec.ExpiresAt is { } exp && exp <= now)
        {
            return null;
        }

        var scopes = ParseScopes(rec.ScopesJson);

        // Throttle: atualiza last_used_at + audit no máx. 1×/60s por token.
        if (rec.LastUsedAt is null || now - rec.LastUsedAt > UsageThrottle)
        {
            rec.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync("pat.used", actorUserId: rec.UserId.ToString(),
                targetType: "personal_access_token",
                targetId: rec.Id.ToString(),
                cancellationToken: CancellationToken.None);
        }

        return new PatResolution(rec.Id, rec.UserId, scopes);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PatSummary>> ListAsync(
        Guid userId, CancellationToken ct)
    {
        var items = await db.PersonalAccessTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .Select(t => new PatSummary(
                t.Id, t.Name, t.Prefix, ParseScopes(t.ScopesJson),
                t.CreatedAt, t.LastUsedAt, t.ExpiresAt))
            .ToListAsync(ct);
        // SQLite não ordena DateTimeOffset — ordena em memória.
        return items.OrderByDescending(t => t.CreatedAt).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(
        Guid userId, Guid tokenId, CancellationToken ct)
    {
        var rec = await db.PersonalAccessTokens
            .FirstOrDefaultAsync(
                t => t.Id == tokenId && t.UserId == userId
                    && t.RevokedAt == null, ct);
        if (rec is null)
        {
            return false;
        }

        rec.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("pat.revoked", actorUserId: userId.ToString(),
            targetType: "personal_access_token",
            targetId: rec.Id.ToString(),
            metadata: new Dictionary<string, string> { ["name"] = rec.Name },
            cancellationToken: ct);
        return true;
    }

    internal static string[] ParseScopes(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json ?? "[]",
                JsonOpts) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}