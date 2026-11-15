namespace Sandcastle.Application.Outpost;

/// <summary>
/// SPEC-153 RF-002: plane-side view of connected outposts and task
/// dispatch over the outbound WS channel. Implemented by the channel
/// registry (singleton); the sandbox engine/executor route ops through
/// it — no direct WebSocket knowledge outside the channel layer.
/// </summary>
public interface IOutpostTaskDispatcher
{
    /// <summary>True when the outpost is connected and not draining.</summary>
    bool IsOnline(Guid outpostId);

    /// <summary>Online outposts of an org (empty when none).</summary>
    IReadOnlyList<Guid> OnlineOutposts(Guid? orgId);

    /// <summary>
    /// Sends a <c>task_assign</c> frame and awaits the terminal
    /// <c>task_event</c> (completed|failed) or timeout/disconnect.
    /// </summary>
    Task<OutpostTaskEvent> SendTaskAsync(
        Guid outpostId, OutpostTaskPayload task,
        TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Sends a <c>drain</c> frame — the outpost stops accepting tasks.
    /// No-op when disconnected (the row already shows draining).
    /// </summary>
    Task DrainAsync(Guid outpostId, CancellationToken ct);

    /// <summary>
    /// Sends <c>kill</c> frames for every in-flight task of the outpost
    /// and drops the connection (revocation path).
    /// </summary>
    Task KillAndDisconnectAsync(Guid outpostId, CancellationToken ct);

    /// <summary>
    /// Owner outpost of a remote container (learned at provision time).
    /// Null = unknown/local container.
    /// </summary>
    Guid? ContainerOwner(string containerId);

    /// <summary>Records container ownership (provision path).</summary>
    void TrackContainer(Guid outpostId, string containerId);

    /// <summary>Drops container ownership (destroy path).</summary>
    void UntrackContainer(string containerId);

    /// <summary>Running task count on a connected outpost (scheduler).</summary>
    int RunningTasks(Guid outpostId);
}

/// <summary>
/// SPEC-153 RF-002: sandbox route resolution — order: conversation pin
/// (<c>conversations.outpost_id</c>) → org residency
/// (<c>data_residency=internal-only</c>) → org/global policy
/// (<c>outpost_policy</c> | <c>Outpost:DefaultPolicy</c>).
/// </summary>
public interface IOutpostRouter
{
    /// <summary>
    /// Resolves where the sandbox for <paramref name="conversationId"/>
    /// must run. When an outpost is required but none is online the
    /// decision still carries <c>Required=true</c> — callers fail fast.
    /// </summary>
    Task<OutpostRouteDecision> ResolveAsync(
        Guid? conversationId, CancellationToken ct);

    /// <summary>
    /// Picks an online outpost for an org (least running tasks, then
    /// lowest id for determinism). Null when none online.
    /// </summary>
    Task<Guid?> PickOnlineAsync(Guid? orgId, CancellationToken ct);
}