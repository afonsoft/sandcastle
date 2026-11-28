using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Extensions;

public sealed class PluginRecord : IOrgScoped
{
    public required Guid Id { get; set; }
    public Guid? OrgId { get; set; }
    public required string OwnerUserId { get; set; }
    public required string Name { get; set; }
    public required string Version { get; set; }
    public required string ManifestJson { get; set; }
    public string PermissionsJson { get; set; } = "[]";
    public bool Enabled { get; set; }
    public string? RollbackVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PluginRecordConfiguration : IEntityTypeConfiguration<PluginRecord>
{
    public void Configure(EntityTypeBuilder<PluginRecord> b)
    {
        b.ToTable("plugins"); b.HasKey(x => x.Id); b.HasIndex(x => x.OrgId); b.HasIndex(x => x.OwnerUserId);
        b.Property(x => x.Name).HasMaxLength(160); b.Property(x => x.Version).HasMaxLength(40);
    }
}