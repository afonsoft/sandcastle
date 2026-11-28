namespace Sandcastle.Application.Runtime;

/// <summary>SPEC-184: one sandbox/runtime row in the fleet inventory.</summary>
public sealed record RuntimeFleetItem(
    Guid SandboxId, Guid? ConversationId, string Status, string Pool,
    string Image, string? Profile, int MemoryMb, double CpuLimit,
    string Node, string? OwnerUserId, string Health, DateTimeOffset CreatedAt,
    TimeSpan Age, string? LastError);

/// <summary>SPEC-184: warm-pool capacity summary.</summary>
public sealed record RuntimePoolInfo(
    string Image, int Desired, int Available, int Claimed, int Stale,
    string Health);

/// <summary>SPEC-184: image rollout/pull summary.</summary>
public sealed record RuntimeImageInfo(
    string Image, int SandboxCount, int WarmAvailable, long ReservedMemoryMb,
    double ReservedCpu, string Status);

/// <summary>SPEC-184: aggregate fleet capacity and queue indicators.</summary>
public sealed record RuntimeCapacityInfo(
    int TotalSandboxes, int Running, int Paused, int Error,
    int WarmAvailable, long ReservedMemoryMb, double ReservedCpu,
    int QueueDepth, double? ProvisioningLatencySeconds, double FailureRate);

/// <summary>SPEC-184: fleet response with capacity summary.</summary>
public sealed record RuntimeFleetResponse(
    RuntimeCapacityInfo Capacity, IReadOnlyList<RuntimeFleetItem> Items,
    IReadOnlyList<string> Alerts);

/// <summary>Resize a warm-pool image at runtime.</summary>
public sealed record RuntimePoolResizeRequest(string Image, int Desired);

/// <summary>Runtime control-plane read model and admin operations.</summary>
public interface IRuntimeControlService
{
    /// <summary>Fleet inventory with capacity and degraded-pool alerts.</summary>
    Task<RuntimeFleetResponse> GetFleetAsync(CancellationToken ct);
    /// <summary>Warm-pool state by image.</summary>
    Task<IReadOnlyList<RuntimePoolInfo>> GetPoolsAsync(CancellationToken ct);
    /// <summary>Image usage/reservation summary.</summary>
    Task<IReadOnlyList<RuntimeImageInfo>> GetImagesAsync(CancellationToken ct);
    /// <summary>Drain an outpost (no new work).</summary>
    Task<bool> DrainOutpostAsync(Guid outpostId, string actorUserId, CancellationToken ct);
    /// <summary>Terminate a sandbox/runtime.</summary>
    Task<bool> TerminateSandboxAsync(Guid sandboxId, string actorUserId, CancellationToken ct);
    /// <summary>Recycle a stale runtime.</summary>
    Task<bool> RecycleSandboxAsync(Guid sandboxId, string actorUserId, CancellationToken ct);
    /// <summary>Resize warm pool dynamically.</summary>
    Task<(bool Changed, string? Error)> ResizePoolAsync(
        RuntimePoolResizeRequest request, string actorUserId, CancellationToken ct);
}

/// <summary>Process-local dynamic warm-pool overrides consumed by the filler.</summary>
public interface IRuntimePoolOverrides
{
    /// <summary>Current desired count override by image; null means config default.</summary>
    IReadOnlyDictionary<string, int> Snapshot();
    /// <summary>Sets desired count for one image.</summary>
    void Set(string image, int desired);
}
