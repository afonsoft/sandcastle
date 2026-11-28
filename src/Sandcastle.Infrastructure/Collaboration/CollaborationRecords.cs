using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Collaboration;

public sealed class ChannelRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public required string OwnerUserId { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public string PinnedUserIdsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ChannelMessageRecord
{
    public required Guid Id { get; set; }
    public required Guid ChannelId { get; set; }
    public required string SenderUserId { get; set; }
    public required string Body { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? ConversationId { get; set; }
    public string ReactionsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ChannelReadRecord
{
    public required Guid ChannelId { get; set; }
    public required Guid UserId { get; set; }
    public DateTimeOffset LastReadAt { get; set; }
}

public sealed class NoteRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public required string OwnerUserId { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public int Version { get; set; }
    public Guid? ConversationId { get; set; }
    public string? KnowledgeLink { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class NoteVersionRecord
{
    public required Guid Id { get; set; }
    public required Guid NoteId { get; set; }
    public required string Body { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class NotificationRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public required string UserId { get; set; }
    public required string Type { get; set; }
    public required string Title { get; set; }
    public string? Body { get; set; }
    public string? DeepLink { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ChannelRecordConfiguration : IEntityTypeConfiguration<ChannelRecord>
{
    public void Configure(EntityTypeBuilder<ChannelRecord> b)
    { b.ToTable("collab_channels"); b.HasKey(x => x.Id); b.Property(x => x.Name).HasMaxLength(200); b.Property(x => x.Type).HasMaxLength(20); b.HasIndex(x => x.OrgId); }
}
public sealed class ChannelMessageRecordConfiguration : IEntityTypeConfiguration<ChannelMessageRecord>
{
    public void Configure(EntityTypeBuilder<ChannelMessageRecord> b)
    { b.ToTable("collab_channel_messages"); b.HasKey(x => x.Id); b.HasIndex(x => new { x.ChannelId, x.CreatedAt }); }
}
public sealed class ChannelReadRecordConfiguration : IEntityTypeConfiguration<ChannelReadRecord>
{
    public void Configure(EntityTypeBuilder<ChannelReadRecord> b)
    { b.ToTable("collab_channel_reads"); b.HasKey(x => new { x.ChannelId, x.UserId }); }
}
public sealed class NoteRecordConfiguration : IEntityTypeConfiguration<NoteRecord>
{
    public void Configure(EntityTypeBuilder<NoteRecord> b)
    { b.ToTable("collab_notes"); b.HasKey(x => x.Id); b.Property(x => x.Title).HasMaxLength(200); b.HasIndex(x => x.OrgId); }
}
public sealed class NoteVersionRecordConfiguration : IEntityTypeConfiguration<NoteVersionRecord>
{
    public void Configure(EntityTypeBuilder<NoteVersionRecord> b)
    { b.ToTable("collab_note_versions"); b.HasKey(x => x.Id); b.HasIndex(x => new { x.NoteId, x.Version }); }
}
public sealed class NotificationRecordConfiguration : IEntityTypeConfiguration<NotificationRecord>
{
    public void Configure(EntityTypeBuilder<NotificationRecord> b)
    { b.ToTable("collab_notifications"); b.HasKey(x => x.Id); b.HasIndex(x => new { x.UserId, x.CreatedAt }); }
}