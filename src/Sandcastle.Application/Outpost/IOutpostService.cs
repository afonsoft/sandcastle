namespace Sandcastle.Application.Outpost;

/// <summary>Outpost lifecycle status (wire values).</summary>
public static class OutpostStatuses
{
    /// <summary>Enrolled, waiting for first register+connect.</summary>
    public const string Pending = "pending";

    /// <summary>Connected and accepting tasks.</summary>
    public const string Online = "online";

    /// <summary>Draining — finishing tasks, not accepting new ones.</summary>
    public const string Draining = "draining";

    /// <summary>Registered but not connected (or heartbeat stale).</summary>
    public const string Offline = "offline";

    /// <summary>Revoked — keys invalidated, cannot reconnect.</summary>
    public const string Revoked = "revoked";
}

/// <summary>Sandbox routing policy (org/<c>Outpost:DefaultPolicy</c>).</summary>
public static class OutpostPolicies
{
    /// <summary>Always run sandboxes on the plane's docker (default).</summary>
    public const string Cloud = "cloud";

    /// <summary>Use an online outpost when the org has one, else cloud.</summary>
    public const string PreferOutpost = "prefer-outpost";

    /// <summary>Only run on an outpost — fail when none is online.</summary>
    public const string OutpostOnly = "outpost-only";

    /// <summary>All valid wire values.</summary>
    public static readonly string[] All =
        [Cloud, PreferOutpost, OutpostOnly];
}

/// <summary>Admin-facing outpost DTO (no secrets/hashes).</summary>
public sealed record OutpostInfo(
    Guid Id,
    Guid? OrgId,
    string Name,
    IReadOnlyDictionary<string, string> Labels,
    string Status,
    string? Version,
    int MaxSandboxes,
    int RunningTasks,
    DateTimeOffset? LastHeartbeat,
    string? CertFingerprint,
    Guid CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>
/// Result of <c>POST /outposts/enroll</c> — the enroll token is shown
/// once (SHA-256 hash at rest); <see cref="DockerCommand"/> is the
/// convenience bootstrap line surfaced by the admin UI.
/// </summary>
public sealed record OutpostEnrollResult(
    Guid OutpostId,
    string EnrollToken,
    string ConnectUrl,
    string DockerCommand);

/// <summary>
/// Result of <c>POST /outposts/register</c> — issued once per enroll
/// token: channel key (<c>opk-*</c>, hash at rest) + optional client cert
/// pair for mTLS (fingerprint registered server-side).
/// </summary>
public sealed record OutpostRegisterResult(
    Guid OutpostId,
    string ChannelKey,
    int HeartbeatSec,
    string? CertPem = null,
    string? KeyPem = null);

/// <summary>Decision of the per-conversation sandbox router.</summary>
/// <param name="OutpostId">Selected outpost (null = cloud route).</param>
/// <param name="Reason">Why: pin | residency | policy:* | default.</param>
/// <param name="Required">True when outpost is mandatory (fail if none online).</param>
public sealed record OutpostRouteDecision(
    Guid? OutpostId,
    string Reason,
    bool Required);

/// <summary>
/// SPEC-153 RF-002: outpost admin service — enroll tokens, register
/// handshake, list/drain/deregister. Keys are <c>opk-*</c> (same scheme
/// as <c>sk-*</c>: SHA-256 at rest, plaintext once).
/// </summary>
public interface IOutpostService
{
    /// <summary>Creates a pending outpost + one-time enroll token.</summary>
    Task<OutpostEnrollResult> EnrollAsync(
        Guid orgId, string name,
        IReadOnlyDictionary<string, string>? labels,
        Guid createdBy, string connectUrl, CancellationToken ct);

    /// <summary>
    /// Exchanges a valid enroll token for the channel key (+client cert
    /// when mTLS is on). One-time: consumed tokens never re-issue.
    /// </summary>
    Task<OutpostRegisterResult?> RegisterAsync(
        string enrollToken, string? name,
        IReadOnlyDictionary<string, string>? labels,
        string? version, CancellationToken ct);

    /// <summary>Lists outposts of an org (any status, newest first).</summary>
    Task<IReadOnlyList<OutpostInfo>> ListAsync(Guid orgId, CancellationToken ct);

    /// <summary>Gets one outpost (org-scoped).</summary>
    Task<OutpostInfo?> GetAsync(Guid id, Guid orgId, CancellationToken ct);

    /// <summary>
    /// Marks <c>draining</c> — the connected channel (if any) receives a
    /// <c>drain</c> frame; returns false when not found.
    /// </summary>
    Task<bool> DrainAsync(Guid id, Guid orgId, CancellationToken ct);

    /// <summary>
    /// Revokes: kills in-flight tasks, clears channel/enroll key hashes,
    /// status <c>revoked</c> — reconnect is refused thereafter.
    /// </summary>
    Task<bool> DeregisterAsync(Guid id, Guid orgId, CancellationToken ct);

    /// <summary>
    /// Resolves an <c>opk-*</c> channel key to its outpost row (connect
    /// auth). Null when unknown/revoked.
    /// </summary>
    Task<OutpostInfo?> ResolveChannelKeyAsync(
        string presentedKey, CancellationToken ct);
}
