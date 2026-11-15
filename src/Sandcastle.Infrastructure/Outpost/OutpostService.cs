using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Identity;
using Sandcastle.Application.Outpost;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Outpost;

/// <summary>
/// <see cref="IOutpostService"/> — <c>outposts</c> rows + enroll/register
/// handshake. <c>opk-*</c> tokens follow the <c>sk-*</c> discipline:
/// base64url(32B) secret, SHA-256 hex at rest, plaintext returned once.
/// All mutations are audit-logged as <c>outpost.*</c>.
/// </summary>
public sealed class OutpostService(
    AppDbContext db,
    IOptions<OutpostOptions> options,
    ILogger<OutpostService> logger,
    IAuditService? audit = null,
    IOutpostTaskDispatcher? dispatcher = null) : IOutpostService
{
    /// <summary>Outpost key prefix (enroll + channel keys).</summary>
    public const string KeyPrefix = "opk-";

    /// <inheritdoc />
    public async Task<OutpostEnrollResult> EnrollAsync(
        Guid orgId, string name,
        IReadOnlyDictionary<string, string>? labels,
        Guid createdBy, string connectUrl, CancellationToken ct)
    {
        var token = KeyPrefix + NewSecret();
        var record = new OutpostRecord
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Name = name.Trim(),
            LabelsJson = labels is null ? null : JsonSerializer.Serialize(labels),
            Status = OutpostStatuses.Pending,
            CreatedBy = createdBy,
            EnrollTokenHash = Hash(token),
            EnrollTokenExpiresAt = DateTimeOffset.UtcNow
                .AddHours(options.Value.EnrollTokenTtlHours),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Outposts.Add(record);
        await db.SaveChangesAsync(ct);

        await AuditAsync("outpost.enroll", createdBy.ToString(), orgId,
            record.Id, new { name = record.Name, labels }, ct);

        var connect = connectUrl.TrimEnd('/') + "/api/v1/outposts/connect";
        var dockerCmd =
            "docker run -d --name sandcastle-outpost " +
            "-v /var/run/docker.sock:/var/run/docker.sock " +
            $"-e OUTPOST_PLANE_URL={connectUrl.TrimEnd('/')} " +
            $"sandcastle-outpost run --enroll-token {token}";
        logger.LogInformation("outpost {OutpostId} enrolled for org {OrgId}",
            record.Id, orgId);
        return new OutpostEnrollResult(
            record.Id, token, connect, dockerCmd);
    }

    /// <inheritdoc />
    public async Task<OutpostRegisterResult?> RegisterAsync(
        string enrollToken, string? name,
        IReadOnlyDictionary<string, string>? labels,
        string? version, CancellationToken ct)
    {
        if (!enrollToken.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(enrollToken);
        var now = DateTimeOffset.UtcNow;
        var record = await db.Outposts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.EnrollTokenHash == hash, ct);
        if (record is null
            || record.Status != OutpostStatuses.Pending
            || record.EnrollTokenExpiresAt is { } exp && exp <= now
            || record.RevokedAt is not null)
        {
            return null;
        }

        var channelKey = KeyPrefix + NewSecret();
        record.ChannelKeyHash = Hash(channelKey);
        record.EnrollTokenHash = null; // one-time — consumed
        record.EnrollTokenExpiresAt = null;
        record.Status = OutpostStatuses.Offline;
        record.Version = version ?? record.Version;
        if (!string.IsNullOrWhiteSpace(name))
        {
            record.Name = name.Trim();
        }

        if (labels is { Count: > 0 })
        {
            record.LabelsJson = JsonSerializer.Serialize(labels);
        }

        string? certPem = null, keyPem = null;
        // Always issue a client cert — cheap, and the fingerprint gates
        // the connect path the moment Outpost:RequireMtls goes on.
        var cert = OutpostCertIssuer.Issue(
            record.Id, options.Value.CertValidityDays);
        record.CertFingerprint = cert.Fingerprint;
        certPem = cert.CertPem;
        keyPem = cert.KeyPem;

        await db.SaveChangesAsync(ct);
        await AuditAsync("outpost.register", null, record.OrgId,
            record.Id, new { record.Name, version }, ct);
        logger.LogInformation(
            "outpost {OutpostId} registered (version {Version})",
            record.Id, version);
        return new OutpostRegisterResult(
            record.Id, channelKey, options.Value.HeartbeatSec,
            certPem, keyPem);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutpostInfo>> ListAsync(
        Guid orgId, CancellationToken ct)
    {
        var rows = await db.Outposts
            .Where(o => o.OrgId == orgId)
            .ToListAsync(ct);
        return rows.OrderByDescending(o => o.CreatedAtTicks)
            .Select(o => ToInfo(o, dispatcher?.RunningTasks(o.Id) ?? 0))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<OutpostInfo?> GetAsync(
        Guid id, Guid orgId, CancellationToken ct)
    {
        var o = await db.Outposts
            .FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, ct);
        return o is null
            ? null
            : ToInfo(o, dispatcher?.RunningTasks(o.Id) ?? 0);
    }

    /// <inheritdoc />
    public async Task<bool> DrainAsync(
        Guid id, Guid orgId, CancellationToken ct)
    {
        var record = await db.Outposts
            .FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, ct);
        if (record is null || record.RevokedAt is not null)
        {
            return false;
        }

        record.Status = OutpostStatuses.Draining;
        await db.SaveChangesAsync(ct);
        if (dispatcher is not null)
        {
            await dispatcher.DrainAsync(id, ct);
        }

        await AuditAsync("outpost.drain", null, orgId, id,
            new { record.Name }, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeregisterAsync(
        Guid id, Guid orgId, CancellationToken ct)
    {
        var record = await db.Outposts
            .FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, ct);
        if (record is null || record.RevokedAt is not null)
        {
            return false;
        }

        record.RevokedAt = DateTimeOffset.UtcNow;
        record.Status = OutpostStatuses.Revoked;
        // Keys invalidated — reconnect is refused (hash lookup misses).
        record.ChannelKeyHash = null;
        record.EnrollTokenHash = null;
        record.CertFingerprint = null;
        await db.SaveChangesAsync(ct);

        if (dispatcher is not null)
        {
            await dispatcher.KillAndDisconnectAsync(id, ct);
        }

        await AuditAsync("outpost.deregister", null, orgId, id,
            new { record.Name }, ct);
        logger.LogInformation("outpost {OutpostId} revoked", id);
        return true;
    }

    /// <inheritdoc />
    public async Task<OutpostInfo?> ResolveChannelKeyAsync(
        string presentedKey, CancellationToken ct)
    {
        if (!presentedKey.StartsWith(KeyPrefix, StringComparison.Ordinal)
            || presentedKey.Length < 14)
        {
            return null;
        }

        var hash = Hash(presentedKey);
        var record = await db.Outposts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.ChannelKeyHash == hash, ct);
        return record is null || record.RevokedAt is not null
            ? null
            : ToInfo(record, dispatcher?.RunningTasks(record.Id) ?? 0);
    }

    /// <summary>Maps a row to the admin DTO (hashes/keys never leave).</summary>
    public static OutpostInfo ToInfo(OutpostRecord r, int runningTasks = 0) =>
        new(r.Id, r.OrgId, r.Name,
            ParseLabels(r.LabelsJson), r.Status, r.Version,
            r.MaxSandboxes, runningTasks, r.LastHeartbeat,
            r.CertFingerprint, r.CreatedBy, r.CreatedAt);

    private static IReadOnlyDictionary<string, string> ParseLabels(
        string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer
                .Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private async Task AuditAsync(
        string action, string? actor, Guid? orgId, Guid outpostId,
        object? metadata, CancellationToken ct)
    {
        if (audit is null)
        {
            return;
        }

        try
        {
            await audit.RecordAsync(action, actor, orgId,
                "outpost", outpostId.ToString(),
                metadata: metadata, cancellationToken: ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "audit {Action} failed for outpost {Id}",
                action, outpostId);
        }
    }

    internal static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>SHA-256 hex of a presented token (indexed lookup).</summary>
    internal static string Hash(string plaintext) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
}