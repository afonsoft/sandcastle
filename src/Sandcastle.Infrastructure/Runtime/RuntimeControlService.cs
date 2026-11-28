using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Identity;
using Sandcastle.Application.Runtime;
using Sandcastle.Application.Sandbox;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Sandboxing;

namespace Sandcastle.Infrastructure.Runtime;

/// <summary>
/// SPEC-184: runtime control-plane inventory/policy. Container truth comes
/// from <see cref="ISandboxEngine"/> (local Docker, runtime-api or k8s);
/// ownership/spec/capacity metadata comes from EF. No Polyform upstream code.
/// </summary>
public sealed class RuntimeControlService(
    AppDbContext db,
    ISandboxEngine engine,
    ISandboxService sandboxes,
    IOptions<SandboxOptions> sandboxOptions,
    IRuntimePoolOverrides poolOverrides,
    IAuditService? audit = null) : IRuntimeControlService
{
    private static string HealthOf(Infrastructure.Sandbox.SandboxRecord row)
    {
        if (row.Status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || row.LastError is not null
            || row.BlueprintStatus == "failed")
        {
            return "error";
        }
        if (row.Status.Equals("created", StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - row.CreatedAt > TimeSpan.FromMinutes(10))
        {
            return "degraded";
        }
        return "healthy";
    }

    private static string PoolHealth(int desired, int available, int stale) =>
        available < desired || stale > 0
            ? available == 0 && desired > 0 ? "error" : "degraded"
            : "healthy";

    private Dictionary<string, int> ConfiguredTargets()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (sandboxOptions.Value.WarmPoolSize > 0)
        {
            map[sandboxOptions.Value.DefaultImage] = sandboxOptions.Value.WarmPoolSize;
        }
        foreach (var part in (sandboxOptions.Value.WarmPoolImages ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.LastIndexOf(':');
            if (colon > 0 && int.TryParse(part[(colon + 1)..], out var n) && n >= 0)
            {
                map[part[..colon].Trim()] = n;
            }
        }
        foreach (var (image, desired) in poolOverrides.Snapshot())
        {
            map[image] = desired;
        }
        return map;
    }

    /// <inheritdoc />
    public async Task<RuntimeFleetResponse> GetFleetAsync(CancellationToken ct)
    {
        var rows = await db.Sandboxes.AsNoTracking()
            .OrderByDescending(s => s.CreatedAtTicks).Take(500).ToListAsync(ct);
        var specs = await db.SandboxSpecs.AsNoTracking().ToListAsync(ct);
        var conversations = await db.Conversations.AsNoTracking()
            .Where(c => rows.Select(r => r.ConversationId).Contains(c.Id))
            .ToListAsync(ct);
        var outposts = await db.Outposts.AsNoTracking().ToListAsync(ct);
        var specById = specs.ToDictionary(s => s.Id);
        var convById = conversations.ToDictionary(c => c.Id);
        var outpostById = outposts.ToDictionary(o => o.Id);
        var now = DateTimeOffset.UtcNow;
        var items = rows.Select(row =>
        {
            specById.TryGetValue(row.SpecId, out var spec);
            convById.TryGetValue(row.ConversationId ?? Guid.Empty, out var conv);
            var node = conv?.OutpostId is { } oid
                && outpostById.TryGetValue(oid, out var outpost)
                ? outpost.Name
                : engine.IsRemote ? "runtime-api" : "local";
            return new RuntimeFleetItem(
                row.Id, row.ConversationId, row.Status, row.Pool,
                spec?.Image ?? "unknown", spec?.Profile,
                spec?.MemoryMb ?? 0, spec?.CpuLimit ?? 0, node,
                conv?.CreatedByUserId, HealthOf(row), row.CreatedAt,
                now - row.CreatedAt, row.LastError);
        }).ToList();

        var running = items.Count(i => i.Status.Equals("running", StringComparison.OrdinalIgnoreCase));
        var errored = items.Count(i => i.Health == "error");
        var total = Math.Max(1, items.Count);
        var latencyRows = rows.Where(r => r.UpdatedAt > r.CreatedAt).Take(100).ToList();
        var latency = latencyRows.Count == 0
            ? (double?)null
            : latencyRows.Average(r => (r.UpdatedAt - r.CreatedAt).TotalSeconds);
        var capacity = new RuntimeCapacityInfo(
            items.Count, running,
            items.Count(i => i.Status.Equals("paused", StringComparison.OrdinalIgnoreCase)),
            errored,
            (await engine.ListPoolAsync(ct)).Count,
            items.Where(i => i.Status.Equals("running", StringComparison.OrdinalIgnoreCase))
                .Sum(i => (long)i.MemoryMb),
            items.Where(i => i.Status.Equals("running", StringComparison.OrdinalIgnoreCase))
                .Sum(i => i.CpuLimit),
            await db.Conversations.CountAsync(c => c.PendingKickoff, ct),
            latency,
            errored / (double)total);

        var alerts = new List<string>();
        if (errored > 0) alerts.Add($"{errored} runtime(s) in error");
        if (capacity.QueueDepth > 0) alerts.Add($"{capacity.QueueDepth} queued conversation(s)");
        foreach (var pool in await GetPoolsAsync(ct))
        {
            if (pool.Health != "healthy")
                alerts.Add($"warm pool degraded for {pool.Image}: {pool.Available}/{pool.Desired}");
        }
        return new RuntimeFleetResponse(capacity, items, alerts);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RuntimePoolInfo>> GetPoolsAsync(CancellationToken ct)
    {
        var free = await engine.ListPoolAsync(ct);
        var targets = ConfiguredTargets();
        var images = targets.Keys.Union(free.Select(c =>
            c.Labels.TryGetValue(EngineLabels.PoolKey, out var key) ? key : ""))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Union(await db.Sandboxes.Where(s => s.PoolKey != null)
                .Select(s => s.PoolKey!).Distinct().ToListAsync(ct));
        var result = new List<RuntimePoolInfo>();
        foreach (var image in images.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var matching = free.Where(c =>
                c.Labels.TryGetValue(EngineLabels.PoolKey, out var key)
                && key.Equals(image, StringComparison.OrdinalIgnoreCase)).ToList();
            var stale = matching.Count(c => c.CreatedAt is { } at
                && DateTimeOffset.UtcNow - at > TimeSpan.FromMinutes(30));
            var claimed = await db.Sandboxes.CountAsync(
                s => s.Pool == "warm" && s.PoolKey == image, ct);
            var desired = targets.GetValueOrDefault(image, 0);
            result.Add(new RuntimePoolInfo(image, desired, matching.Count,
                claimed, stale, PoolHealth(desired, matching.Count, stale)));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RuntimeImageInfo>> GetImagesAsync(CancellationToken ct)
    {
        var specs = await db.SandboxSpecs.AsNoTracking().ToListAsync(ct);
        var rows = await db.Sandboxes.AsNoTracking().ToListAsync(ct);
        var pools = await GetPoolsAsync(ct);
        return specs.GroupBy(s => s.Image).Select(g =>
        {
            var specIds = g.Select(s => s.Id).ToHashSet();
            var sandboxesForImage = rows.Where(r => specIds.Contains(r.SpecId)).ToList();
            var pool = pools.FirstOrDefault(p => p.Image.Equals(g.Key,
                StringComparison.OrdinalIgnoreCase));
            var failed = sandboxesForImage.Count(r => r.BlueprintStatus == "failed");
            return new RuntimeImageInfo(
                g.Key, sandboxesForImage.Count, pool?.Available ?? 0,
                sandboxesForImage.Sum(r => (long)(g.First(s => s.Id == r.SpecId).MemoryMb)),
                sandboxesForImage.Sum(r => g.First(s => s.Id == r.SpecId).CpuLimit),
                failed > 0 ? "pull_or_provision_error" : "ready");
        }).OrderByDescending(i => i.SandboxCount).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DrainOutpostAsync(
        Guid outpostId, string actorUserId, CancellationToken ct)
    {
        var row = await db.Outposts.FirstOrDefaultAsync(o => o.Id == outpostId, ct);
        if (row is null || row.RevokedAt is not null) return false;
        row.Status = "draining";
        await db.SaveChangesAsync(ct);
        await AuditAsync("runtime_outpost_drain", actorUserId, row.OrgId,
            "outpost", row.Id, new { row.Name }, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> TerminateSandboxAsync(
        Guid sandboxId, string actorUserId, CancellationToken ct)
    {
        var row = await db.Sandboxes.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sandboxId, ct);
        if (row is null) return false;
        await sandboxes.DeleteAsync(sandboxId, ct);
        await AuditAsync("runtime_sandbox_terminate", actorUserId, null,
            "sandbox", sandboxId, new { row.Status, row.Pool }, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RecycleSandboxAsync(
        Guid sandboxId, string actorUserId, CancellationToken ct)
    {
        var row = await db.Sandboxes.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sandboxId, ct);
        if (row is null) return false;
        var stale = row.Status.Equals("created", StringComparison.OrdinalIgnoreCase)
            || row.Status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || DateTimeOffset.UtcNow - row.UpdatedAt > TimeSpan.FromHours(12);
        if (!stale) return false;
        await sandboxes.DeleteAsync(sandboxId, ct);
        await AuditAsync("runtime_sandbox_recycle", actorUserId, null,
            "sandbox", sandboxId, new { row.Status, row.UpdatedAt }, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<(bool Changed, string? Error)> ResizePoolAsync(
        RuntimePoolResizeRequest request, string actorUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Image))
            return (false, "image required");
        if (request.Desired is < 0 or > 20)
            return (false, "desired must be between 0 and 20");
        poolOverrides.Set(request.Image.Trim(), request.Desired);
        await AuditAsync("runtime_pool_resize", actorUserId, null,
            "runtime_pool", request.Image, new { request.Desired }, ct);
        return (true, null);
    }

    private async Task AuditAsync(
        string action, string actorUserId, Guid? orgId, string targetType,
        object targetId, object metadata, CancellationToken ct)
    {
        if (audit is not null)
        {
            await audit.RecordAsync(action, actorUserId, orgId, targetType,
                targetId.ToString(), metadata: metadata, cancellationToken: ct);
        }
    }
}
