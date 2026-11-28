using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Lifecycle;

public sealed class LifecycleProvisioningRequestRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public string Status { get; set; } = "waitlist";
    public string QuotasJson { get; set; } = "{}";
    public string FeatureFlagsJson { get; set; } = "{}";
    public int InviteCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? OnboardedAt { get; set; }
}
public sealed class IntegrationOutboxEventRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public required string Provider { get; set; }
    public required string EventType { get; set; }
    public required string PayloadJson { get; set; }
    public string? TargetUrl { get; set; }
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTimeOffset? NextRetryAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
public sealed class LifecycleJobRunRecord
{
    public required Guid Id { get; set; }
    public required string JobKey { get; set; }
    public string Status { get; set; } = "running";
    public string DetailsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
public sealed class LifecycleProvisioningRequestConfiguration : IEntityTypeConfiguration<LifecycleProvisioningRequestRecord>
{ public void Configure(EntityTypeBuilder<LifecycleProvisioningRequestRecord> b) { b.ToTable("lifecycle_provisioning_requests"); b.HasKey(x => x.Id); b.HasIndex(x => x.NormalizedEmail); b.Property(x => x.Email).HasMaxLength(320); b.Property(x => x.NormalizedEmail).HasMaxLength(320); } }
public sealed class IntegrationOutboxEventConfiguration : IEntityTypeConfiguration<IntegrationOutboxEventRecord>
{ public void Configure(EntityTypeBuilder<IntegrationOutboxEventRecord> b) { b.ToTable("integration_outbox_events"); b.HasKey(x => x.Id); b.HasIndex(x => new { x.Status, x.NextRetryAt }); b.Property(x => x.Provider).HasMaxLength(80); b.Property(x => x.EventType).HasMaxLength(120); } }
public sealed class LifecycleJobRunConfiguration : IEntityTypeConfiguration<LifecycleJobRunRecord>
{ public void Configure(EntityTypeBuilder<LifecycleJobRunRecord> b) { b.ToTable("lifecycle_job_runs"); b.HasKey(x => x.Id); b.HasIndex(x => x.JobKey); b.Property(x => x.JobKey).HasMaxLength(120); } }