namespace Sandcastle.Domain.Sandbox;

/// <summary>
/// Representa um snapshot de workspace gravado para backup, fork ou checkpoint.
/// </summary>
public sealed record SandboxSnapshot(
    Guid Id,
    Guid SandboxId,
    string SnapshotName,
    long SizeBytes,
    string StoragePath,
    DateTimeOffset CreatedAt,
    string? Description = null);