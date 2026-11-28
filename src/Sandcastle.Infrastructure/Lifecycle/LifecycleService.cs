using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Identity;
using Sandcastle.Application.Lifecycle;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Web;

namespace Sandcastle.Infrastructure.Lifecycle;

/// <summary>SPEC-190: provisioning lifecycle, durable integration outbox and audited operator jobs.</summary>
public sealed class LifecycleService(AppDbContext db, IHttpClientFactory httpFactory, IAuditService? audit = null) : ILifecycleService
{
    public async Task<IReadOnlyList<LifecycleProvisioningInfo>> ListProvisioningAsync(CancellationToken ct) =>
        (await db.LifecycleProvisioningRequests.AsNoTracking().ToListAsync(ct)).OrderByDescending(x => x.CreatedAt).Take(200).Select(MapProvisioning).ToList();

    public async Task<LifecycleProvisioningInfo> RequestProvisioningAsync(LifecycleProvisioningCreate request, string? actorUserId, CancellationToken ct)
    {
        var normalized = request.Email.Trim().ToLowerInvariant();
        var existing = await db.LifecycleProvisioningRequests.FirstOrDefaultAsync(x => x.NormalizedEmail == normalized && x.Status != "rejected", ct);
        var now = DateTimeOffset.UtcNow;
        if (existing is not null)
        {
            existing.InviteCount++; existing.UpdatedAt = now;
            if (!string.IsNullOrWhiteSpace(request.QuotasJson)) existing.QuotasJson = request.QuotasJson;
            if (!string.IsNullOrWhiteSpace(request.FeatureFlagsJson)) existing.FeatureFlagsJson = request.FeatureFlagsJson;
        }
        else
        {
            existing = new LifecycleProvisioningRequestRecord { Id = Guid.NewGuid(), OrgId = request.OrgId, Email = request.Email.Trim(), NormalizedEmail = normalized, Status = "waitlist", QuotasJson = request.QuotasJson ?? "{}", FeatureFlagsJson = request.FeatureFlagsJson ?? "{}", InviteCount = 1, CreatedAt = now, UpdatedAt = now };
            db.LifecycleProvisioningRequests.Add(existing);
        }
        await db.SaveChangesAsync(ct);
        await AuditAsync("lifecycle_provisioning_request", actorUserId, existing.OrgId, existing.Id, new { duplicate = existing.InviteCount > 1 }, ct);
        return MapProvisioning(existing);
    }

    public async Task<LifecycleProvisioningInfo?> InviteProvisioningAsync(Guid id, string? actorUserId, CancellationToken ct) => await TransitionProvisioningAsync(id, "invited", actorUserId, ct);
    public async Task<LifecycleProvisioningInfo?> ApproveProvisioningAsync(Guid id, string? actorUserId, CancellationToken ct) => await TransitionProvisioningAsync(id, "approved", actorUserId, ct);
    public async Task<LifecycleProvisioningInfo?> MarkOnboardedAsync(Guid id, string? actorUserId, CancellationToken ct) => await TransitionProvisioningAsync(id, "onboarded", actorUserId, ct);
    private async Task<LifecycleProvisioningInfo?> TransitionProvisioningAsync(Guid id, string status, string? actorUserId, CancellationToken ct)
    {
        var row = await db.LifecycleProvisioningRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return null;
        var now = DateTimeOffset.UtcNow; row.Status = status; row.UpdatedAt = now;
        if (status == "approved") row.ApprovedAt = now;
        if (status == "onboarded") row.OnboardedAt = now;
        await db.SaveChangesAsync(ct);
        await AuditAsync("lifecycle_provisioning_" + status, actorUserId, row.OrgId, row.Id, null, ct);
        return MapProvisioning(row);
    }

    public async Task<IntegrationOutboxInfo> EnqueueEventAsync(IntegrationEventEnqueue request, string? actorUserId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var row = new IntegrationOutboxEventRecord { Id = Guid.NewGuid(), OrgId = request.OrgId, Provider = request.Provider.Trim(), EventType = request.EventType.Trim(), PayloadJson = SecretRedactor.Redact(request.PayloadJson), TargetUrl = request.TargetUrl, MaxAttempts = Math.Clamp(request.MaxAttempts <= 0 ? 5 : request.MaxAttempts, 1, 10), CreatedAt = now, UpdatedAt = now, NextRetryAt = now };
        db.IntegrationOutboxEvents.Add(row); await db.SaveChangesAsync(ct);
        await AuditAsync("integration_outbox_enqueue", actorUserId, row.OrgId, row.Id, new { row.Provider, row.EventType }, ct);
        return MapOutbox(row);
    }

    public async Task<IReadOnlyList<IntegrationOutboxInfo>> ListOutboxAsync(string? status, CancellationToken ct)
    {
        var q = db.IntegrationOutboxEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        return (await q.ToListAsync(ct)).OrderByDescending(x => x.CreatedAt).Take(200).Select(MapOutbox).ToList();
    }

    public async Task<IntegrationOutboxInfo?> ReplayDeadLetterAsync(Guid id, string? actorUserId, CancellationToken ct)
    {
        var row = await db.IntegrationOutboxEvents.FirstOrDefaultAsync(x => x.Id == id && x.Status == "dead_letter", ct);
        if (row is null) return null;
        row.Status = "pending"; row.Attempts = 0; row.LastError = null; row.NextRetryAt = DateTimeOffset.UtcNow; row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await AuditAsync("integration_outbox_replay", actorUserId, row.OrgId, row.Id, null, ct);
        return MapOutbox(row);
    }

    public async Task<int> ProcessDueEventsAsync(int batch, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.IntegrationOutboxEvents.Where(x => x.Status == "pending").ToListAsync(ct);
        var rows = candidates.Where(x => x.NextRetryAt == null || x.NextRetryAt <= now).OrderBy(x => x.CreatedAt).Take(Math.Clamp(batch, 1, 50)).ToList();
        var processed = 0;
        foreach (var row in rows)
        {
            row.Attempts++; row.UpdatedAt = DateTimeOffset.UtcNow;
            try
            {
                if (!Uri.TryCreate(row.TargetUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("target_url must be absolute https");
                if (await SsrfGuard.IsBlockedAsync(uri, ct)) throw new InvalidOperationException("target_url blocked by SSRF guard");
                using var resp = await httpFactory.CreateClient("lifecycle-outbox").PostAsJsonAsync(uri, JsonSerializer.Deserialize<JsonElement>(row.PayloadJson), ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"target HTTP {(int)resp.StatusCode}");
                row.Status = "delivered"; row.DeliveredAt = DateTimeOffset.UtcNow; row.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.LastError = ex.Message.Length <= 500 ? ex.Message : ex.Message[..500];
                if (row.Attempts >= row.MaxAttempts) row.Status = "dead_letter";
                else row.NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, row.Attempts) * 15);
            }
            processed++;
        }
        await db.SaveChangesAsync(ct);
        return processed;
    }

    public async Task<IReadOnlyList<LifecycleJobInfo>> ListJobsAsync(CancellationToken ct) =>
        (await db.LifecycleJobRuns.AsNoTracking().ToListAsync(ct)).OrderByDescending(x => x.CreatedAt).Take(100).Select(x => new LifecycleJobInfo(x.Id, x.JobKey, x.Status, x.DetailsJson, x.CreatedAt, x.CompletedAt)).ToList();

    public async Task<LifecycleJobInfo> RunJobAsync(string jobKey, string? actorUserId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var row = new LifecycleJobRunRecord { Id = Guid.NewGuid(), JobKey = jobKey, CreatedAt = now };
        db.LifecycleJobRuns.Add(row);
        var cutoff = now.AddHours(-24).UtcTicks;
        Dictionary<string, object> details = jobKey switch
        {
            "proactive-conversation-cleanup" => new Dictionary<string, object> { ["stale_conversations"] = await db.Conversations.CountAsync(x => x.ExecutionStatus != "Archived" && x.UpdatedAtTicks < cutoff, ct), ["owner"] = "conversation lifecycle reconciler" },
            "start-task-cleanup" => new Dictionary<string, object> { ["stale_start_tasks"] = await db.StartTasks.CountAsync(x => x.Status != "READY" && x.Status != "ERROR" && x.UpdatedAtTicks < cutoff, ct), ["owner"] = "start-task orphan reconciler" },
            "abandoned-sandbox-cleanup" => new Dictionary<string, object> { ["abandoned_sandboxes"] = await db.Sandboxes.CountAsync(x => x.Status != "deleted" && x.UpdatedAtTicks < cutoff, ct), ["owner"] = "sandbox orphan sweep" },
            "integration-resync" => new Dictionary<string, object> { ["pending_events"] = await db.IntegrationOutboxEvents.CountAsync(x => x.Status == "pending", ct), ["dead_letters"] = await db.IntegrationOutboxEvents.CountAsync(x => x.Status == "dead_letter", ct) },
            _ => throw new ArgumentException("unknown lifecycle job", nameof(jobKey)),
        };
        row.Status = "completed"; row.CompletedAt = DateTimeOffset.UtcNow; row.DetailsJson = JsonSerializer.Serialize(details);
        await db.SaveChangesAsync(ct);
        await AuditAsync("lifecycle_job_run", actorUserId, null, row.Id, new { jobKey }, ct);
        return new LifecycleJobInfo(row.Id, row.JobKey, row.Status, row.DetailsJson, row.CreatedAt, row.CompletedAt);
    }

    private async Task AuditAsync(string action, string? actor, Guid? orgId, Guid id, object? metadata, CancellationToken ct)
    { if (audit is not null) await audit.RecordAsync(action, actor, orgId, "lifecycle", id.ToString(), metadata: metadata, cancellationToken: ct); }
    private static LifecycleProvisioningInfo MapProvisioning(LifecycleProvisioningRequestRecord x) => new(x.Id, x.OrgId, x.Email, x.Status, x.QuotasJson, x.FeatureFlagsJson, x.InviteCount, x.CreatedAt, x.UpdatedAt, x.ApprovedAt, x.OnboardedAt);
    private static IntegrationOutboxInfo MapOutbox(IntegrationOutboxEventRecord x) => new(x.Id, x.OrgId, x.Provider, x.EventType, x.Status, x.Attempts, x.MaxAttempts, x.NextRetryAt, x.LastError, x.CreatedAt, x.DeliveredAt);
}

/// <summary>SPEC-190: periodic durable outbox processor.</summary>
public sealed class LifecycleOutboxWorker(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<LifecycleOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Clamp(cfg.GetValue("Lifecycle:OutboxIntervalSeconds", 30), 5, 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ILifecycleService>().ProcessDueEventsAsync(cfg.GetValue("Lifecycle:OutboxBatch", 20), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "lifecycle outbox processing failed"); }
        }
    }
}