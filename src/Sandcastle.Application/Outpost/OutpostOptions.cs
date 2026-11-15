namespace Sandcastle.Application.Outpost;

/// <summary>
/// SPEC-153: <c>Outpost</c> config section — hybrid runner channel,
/// routing defaults and mTLS policy.
/// </summary>
public sealed class OutpostOptions
{
    /// <summary>Config section name.</summary>
    public const string Section = "Outpost";

    /// <summary>Master switch — outpost endpoints/channel/routing off when false.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Require a client cert (fingerprint-bound) on
    /// <c>/outposts/connect</c>. When null the effective default is
    /// <c>true</c> in Production and <c>false</c> elsewhere — token-only
    /// stays opt-in for hardened deployments.
    /// </summary>
    public bool? RequireMtls { get; set; }

    /// <summary>Heartbeat cadence (seconds) — sent in the register reply.</summary>
    public int HeartbeatSec { get; set; } = 30;

    /// <summary>Heartbeat staleness after which status → offline.</summary>
    public int OfflineAfterSec { get; set; } = 90;

    /// <summary>Enroll token TTL in hours.</summary>
    public int EnrollTokenTtlHours { get; set; } = 24;

    /// <summary>
    /// Default sandbox routing policy —
    /// <c>cloud|prefer-outpost|outpost-only</c>; org override lives in
    /// <c>organizations.settings_json["outpost_policy"]</c>.
    /// </summary>
    public string DefaultPolicy { get; set; } = OutpostPolicies.Cloud;

    /// <summary>Default timeout for one task_assign → task_event (seconds).</summary>
    public int TaskTimeoutSec { get; set; } = 300;

    /// <summary>Client cert validity issued at register (days).</summary>
    public int CertValidityDays { get; set; } = 365;

    /// <summary>
    /// Outpost-side log redaction mode (<c>aggressive</c> scrubs
    /// resolved secret values from output before frames leave).
    /// </summary>
    public string LogRedaction { get; set; } = "aggressive";

    /// <summary>Effective mTLS requirement for the current environment.</summary>
    public bool EffectiveRequireMtls(bool isProduction) =>
        RequireMtls ?? isProduction;
}