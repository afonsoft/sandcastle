using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Outpost;

/// <summary>
/// SPEC-153 RF-002: <c>outposts</c> — runner agents enrolled by an org.
/// Enroll/channel keys are <c>opk-*</c> (SHA-256 at rest, same scheme as
/// <c>sk-*</c>); <see cref="CertFingerprint"/> (SHA-256 hex of the client
/// cert DER) gates the mTLS connect path.
/// </summary>
public sealed class OutpostRecord : IOrgScoped
{
    /// <summary>Outpost id.</summary>
    public required Guid Id { get; set; }

    /// <summary>Owning org (null only for legacy/global rows).</summary>
    public Guid? OrgId { get; set; }

    /// <summary>Friendly name shown in Settings → Outposts.</summary>
    public required string Name { get; set; }

    /// <summary>Capability labels (gpu|windows|region|docker-version…) as JSON.</summary>
    public string? LabelsJson { get; set; }

    /// <summary>pending|online|draining|offline|revoked.</summary>
    public required string Status { get; set; }

    /// <summary>Runner version reported on hello/register.</summary>
    public string? Version { get; set; }

    /// <summary>Max concurrent sandboxes reported on hello.</summary>
    public int MaxSandboxes { get; set; }

    /// <summary>Last heartbeat/activity timestamp (status derivation).</summary>
    public DateTimeOffset? LastHeartbeat { get; set; }

    /// <summary>Admin user that created the enrollment.</summary>
    public required Guid CreatedBy { get; set; }

    /// <summary>SHA-256 hex of the channel key (<c>opk-*</c>) — connect auth.</summary>
    public string? ChannelKeyHash { get; set; }

    /// <summary>SHA-256 hex of the pending one-time enroll token.</summary>
    public string? EnrollTokenHash { get; set; }

    /// <summary>Enroll token expiry (default 24h).</summary>
    public DateTimeOffset? EnrollTokenExpiresAt { get; set; }

    /// <summary>SHA-256 fingerprint of the issued client cert (mTLS).</summary>
    public string? CertFingerprint { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Ticks mirror of <see cref="CreatedAt"/> — SQLite ordering.</summary>
    public long CreatedAtTicks { get; set; }

    /// <summary>Set on deregister — revoked outposts never reconnect.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>EF config for <see cref="OutpostRecord"/>.</summary>
public sealed class OutpostRecordConfiguration
    : IEntityTypeConfiguration<OutpostRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OutpostRecord> builder)
    {
        builder.ToTable("outposts");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Version).HasMaxLength(60);
        builder.Property(e => e.ChannelKeyHash).HasMaxLength(64);
        builder.Property(e => e.EnrollTokenHash).HasMaxLength(64);
        builder.Property(e => e.CertFingerprint).HasMaxLength(64);
        builder.HasIndex(e => e.ChannelKeyHash);
        builder.HasIndex(e => e.EnrollTokenHash);
        builder.HasIndex(e => new { e.OrgId, e.Status });
        builder.HasIndex(e => e.CreatedAtTicks);
    }
}