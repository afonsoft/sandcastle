using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Workflows;

/// <summary>SPEC-183: factory grouping automations/workflows.</summary>
public sealed class FactoryRecord : IOrgScoped
{
    /// <summary>PK.</summary>
    public required Guid Id { get; set; }
    /// <inheritdoc />
    public Guid? OrgId { get; set; }
    /// <summary>Owner user id.</summary>
    public required string OwnerUserId { get; set; }
    /// <summary>Display name.</summary>
    public required string Name { get; set; }
    /// <summary>Optional description.</summary>
    public string? Description { get; set; }
    /// <summary>Created timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Updated timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>SPEC-183: versioned named workflow.</summary>
public sealed class WorkflowRecord : IOrgScoped
{
    /// <summary>PK.</summary>
    public required Guid Id { get; set; }
    /// <inheritdoc />
    public Guid? OrgId { get; set; }
    /// <summary>Owner user id.</summary>
    public required string OwnerUserId { get; set; }
    /// <summary>Optional factory grouping.</summary>
    public Guid? FactoryId { get; set; }
    /// <summary>Display name.</summary>
    public required string Name { get; set; }
    /// <summary>Optional description.</summary>
    public string? Description { get; set; }
    /// <summary>Monotonic definition version.</summary>
    public int Version { get; set; }
    /// <summary>JSON definition: inputs, retry policy and steps.</summary>
    public required string DefinitionJson { get; set; }
    /// <summary>Whether autonomous-delivery hard-gated loop is enabled.</summary>
    public bool AutonomousDelivery { get; set; }
    /// <summary>Created timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Updated timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>SPEC-183: auditable workflow run.</summary>
public sealed class WorkflowRunRecord
{
    /// <summary>PK.</summary>
    public required Guid Id { get; set; }
    /// <summary>Workflow FK.</summary>
    public required Guid WorkflowId { get; set; }
    /// <summary>Owner snapshot for query isolation.</summary>
    public required string OwnerUserId { get; set; }
    /// <summary>running|waiting_agent|waiting_approval|succeeded|failed|rejected.</summary>
    public required string Status { get; set; }
    /// <summary>Zero-based current step.</summary>
    public int CurrentStepIndex { get; set; }
    /// <summary>Current step display name.</summary>
    public string? CurrentStepName { get; set; }
    /// <summary>Active conversation for agent/script steps.</summary>
    public Guid? ConversationId { get; set; }
    /// <summary>Terminal error.</summary>
    public string? Error { get; set; }
    /// <summary>JSON object with typed run inputs.</summary>
    public required string InputsJson { get; set; }
    /// <summary>JSON array with immutable audit entries.</summary>
    public required string AuditJson { get; set; }
    /// <summary>JSON object mapping step index to attempts.</summary>
    public required string AttemptsJson { get; set; }
    /// <summary>Created timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Started timestamp.</summary>
    public DateTimeOffset? StartedAt { get; set; }
    /// <summary>Finished timestamp.</summary>
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Tick mirror for SQLite ordering.</summary>
    public long CreatedAtTicks { get; set; }
}

/// <inheritdoc />
public sealed class FactoryRecordConfiguration : IEntityTypeConfiguration<FactoryRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<FactoryRecord> b)
    {
        b.ToTable("factories");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.OwnerUserId);
        b.Property(e => e.Name).HasMaxLength(160);
        b.Property(e => e.Description).HasMaxLength(2000);
    }
}

/// <inheritdoc />
public sealed class WorkflowRecordConfiguration : IEntityTypeConfiguration<WorkflowRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WorkflowRecord> b)
    {
        b.ToTable("workflows");
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.OwnerUserId);
        b.HasIndex(e => e.FactoryId);
        b.Property(e => e.Name).HasMaxLength(160);
        b.Property(e => e.Description).HasMaxLength(2000);
    }
}

/// <inheritdoc />
public sealed class WorkflowRunRecordConfiguration : IEntityTypeConfiguration<WorkflowRunRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WorkflowRunRecord> b)
    {
        b.ToTable("workflow_runs");
        b.HasKey(e => e.Id);
        b.HasIndex(e => new { e.WorkflowId, e.CreatedAtTicks });
        b.HasIndex(e => e.Status);
        b.HasIndex(e => e.OwnerUserId);
    }
}