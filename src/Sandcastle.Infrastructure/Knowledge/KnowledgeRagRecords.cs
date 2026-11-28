using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Knowledge;

public sealed class KnowledgeCollectionRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public required string OwnerUserId { get; set; }
    public required string Name { get; set; }
    public string? RepoPath { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class KnowledgeDocumentRecord
{
    public required Guid Id { get; set; }
    public required Guid CollectionId { get; set; }
    public required string Title { get; set; }
    public required string SourceUri { get; set; }
    public required string ContentHash { get; set; }
    public int ChunkCount { get; set; }
    public string LineageJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class KnowledgeChunkRecord
{
    public required Guid Id { get; set; }
    public required Guid DocumentId { get; set; }
    public int Ordinal { get; set; }
    public required string Text { get; set; }
    public string? EmbeddingJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class KnowledgeCollectionRecordConfiguration : IEntityTypeConfiguration<KnowledgeCollectionRecord>
{ public void Configure(EntityTypeBuilder<KnowledgeCollectionRecord> b) { b.ToTable("knowledge_collections"); b.HasKey(x => x.Id); b.HasIndex(x => x.OrgId); b.HasIndex(x => x.OwnerUserId); b.Property(x => x.Name).HasMaxLength(200); } }
public sealed class KnowledgeDocumentRecordConfiguration : IEntityTypeConfiguration<KnowledgeDocumentRecord>
{ public void Configure(EntityTypeBuilder<KnowledgeDocumentRecord> b) { b.ToTable("knowledge_documents"); b.HasKey(x => x.Id); b.HasIndex(x => x.CollectionId); b.Property(x => x.Title).HasMaxLength(240); } }
public sealed class KnowledgeChunkRecordConfiguration : IEntityTypeConfiguration<KnowledgeChunkRecord>
{ public void Configure(EntityTypeBuilder<KnowledgeChunkRecord> b) { b.ToTable("knowledge_chunks"); b.HasKey(x => x.Id); b.HasIndex(x => new { x.DocumentId, x.Ordinal }); } }