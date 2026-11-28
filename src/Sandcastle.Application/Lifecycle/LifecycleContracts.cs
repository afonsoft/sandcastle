namespace Sandcastle.Application.Lifecycle;

/// <summary>SPEC-190: provisioning queue entry (waitlist/invite/approval/onboarding).</summary>
public sealed record LifecycleProvisioningInfo(Guid Id, Guid? OrgId, string Email, string Status, string QuotasJson, string FeatureFlagsJson, int InviteCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ApprovedAt, DateTimeOffset? OnboardedAt);
public sealed record LifecycleProvisioningCreate(string Email, Guid? OrgId, string? QuotasJson, string? FeatureFlagsJson);
/// <summary>SPEC-190: durable integration outbox event.</summary>
public sealed record IntegrationOutboxInfo(Guid Id, Guid? OrgId, string Provider, string EventType, string Status, int Attempts, int MaxAttempts, DateTimeOffset? NextRetryAt, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt);
public sealed record IntegrationEventEnqueue(string Provider, string EventType, string PayloadJson, string? TargetUrl, Guid? OrgId, int MaxAttempts);
/// <summary>SPEC-190: audited operator job run.</summary>
public sealed record LifecycleJobInfo(Guid Id, string JobKey, string Status, string DetailsJson, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public interface ILifecycleService
{
    Task<IReadOnlyList<LifecycleProvisioningInfo>> ListProvisioningAsync(CancellationToken ct);
    Task<LifecycleProvisioningInfo> RequestProvisioningAsync(LifecycleProvisioningCreate request, string? actorUserId, CancellationToken ct);
    Task<LifecycleProvisioningInfo?> InviteProvisioningAsync(Guid id, string? actorUserId, CancellationToken ct);
    Task<LifecycleProvisioningInfo?> ApproveProvisioningAsync(Guid id, string? actorUserId, CancellationToken ct);
    Task<LifecycleProvisioningInfo?> MarkOnboardedAsync(Guid id, string? actorUserId, CancellationToken ct);
    Task<IntegrationOutboxInfo> EnqueueEventAsync(IntegrationEventEnqueue request, string? actorUserId, CancellationToken ct);
    Task<IReadOnlyList<IntegrationOutboxInfo>> ListOutboxAsync(string? status, CancellationToken ct);
    Task<IntegrationOutboxInfo?> ReplayDeadLetterAsync(Guid id, string? actorUserId, CancellationToken ct);
    Task<int> ProcessDueEventsAsync(int batch, CancellationToken ct);
    Task<IReadOnlyList<LifecycleJobInfo>> ListJobsAsync(CancellationToken ct);
    Task<LifecycleJobInfo> RunJobAsync(string jobKey, string? actorUserId, CancellationToken ct);
}