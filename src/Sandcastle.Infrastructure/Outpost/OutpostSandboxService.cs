using Microsoft.Extensions.Logging;

using Sandcastle.Application.Outpost;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Persistence;
using Sandcastle.Infrastructure.Sandbox;

namespace Sandcastle.Infrastructure.Outpost;

/// <summary>
/// SPEC-153 RF-002: <see cref="ISandboxService"/> decorator — routes
/// container lifecycle to a connected outpost over the outbound channel
/// when the router picks one; falls back to the local Docker engine for
/// <c>cloud</c>/<c>prefer-outpost</c> without an online outpost; FAILS
/// (never silently re-routes to cloud) for <c>outpost-only</c>.
/// Spec CRUD/search/sweep stay local — only container ops cross the wire.
/// </summary>
public sealed class OutpostSandboxService(
    SandboxService inner,
    IOutpostRouter router,
    IOutpostTaskDispatcher dispatch,
    AppDbContext db,
    ILogger<OutpostSandboxService> logger) : ISandboxService
{
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public async Task<Domain.Sandbox.Sandbox> StartAsync(
        Guid specId, Guid? conversationId, CancellationToken ct)
    {
        var decision = await router.ResolveAsync(conversationId, ct);
        if (decision.OutpostId is not { } outpostId)
        {
            if (decision.Required)
            {
                throw new InvalidOperationException(
                    "outpost required but none is online");
            }

            return await inner.StartAsync(specId, conversationId, ct);
        }

        var spec = await inner.GetSpecAsync(specId, ct)
            ?? throw new InvalidOperationException(
                $"Sandbox spec {specId} not found");

        var record = new SandboxRecord
        {
            Id = Guid.NewGuid(),
            SpecId = specId,
            ConversationId = conversationId,
            Status = SandboxStatus.Starting.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(spec.TimeoutMinutes),
            Pool = "outpost",
            PoolKey = spec.Image,
        };
        db.Sandboxes.Add(record);
        await db.SaveChangesAsync(ct);

        try
        {
            var evt = await dispatch.SendTaskAsync(outpostId,
                new OutpostTaskPayload
                {
                    Op = OutpostTaskOps.Provision,
                    Name = $"sc-sandbox-{record.Id:N}",
                    Image = spec.Image,
                    Env = spec.Env?.Select(kv => $"{kv.Key}={kv.Value}")
                        .ToList(),
                    MemoryBytes = (long)spec.MemoryMb * 1024 * 1024,
                    TimeoutMs = (int)TaskTimeout.TotalMilliseconds,
                }, TaskTimeout, ct);

            record.ContainerId = evt.Data is { } d
                && d.TryGetProperty("container_id", out var cid)
                ? cid.GetString() : null;
            record.Status = evt.Status == OutpostTaskStatus.Completed
                ? SandboxStatus.Running.ToString()
                : SandboxStatus.Error.ToString();
            record.LastError = evt.Error;
            if (record.ContainerId is not null)
            {
                dispatch.TrackContainer(outpostId, record.ContainerId);
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Sandbox {Id} provisioned on outpost {OutpostId}",
                record.Id, outpostId);
            return record.ToDomain();
        }
        catch (Exception ex)
        {
            record.Status = SandboxStatus.Error.ToString();
            record.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Domain.Sandbox.Sandbox> PauseAsync(
        Guid sandboxId, CancellationToken ct) =>
        RemoteOrLocal(sandboxId, OutpostTaskOps.Pause,
            SandboxStatus.Paused, () => inner.PauseAsync(sandboxId, ct), ct);

    /// <inheritdoc />
    public Task<Domain.Sandbox.Sandbox> StopAsync(
        Guid sandboxId, CancellationToken ct) =>
        RemoteOrLocal(sandboxId, OutpostTaskOps.Stop,
            SandboxStatus.Stopped, () => inner.StopAsync(sandboxId, ct), ct);

    /// <inheritdoc />
    public Task<Domain.Sandbox.Sandbox> ResumeAsync(
        Guid sandboxId, CancellationToken ct) =>
        RemoteOrLocal(sandboxId, OutpostTaskOps.Unpause,
            SandboxStatus.Running, () => inner.ResumeAsync(sandboxId, ct), ct);

    /// <inheritdoc />
    public async Task DeleteAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await db.Sandboxes.FindAsync([sandboxId], ct);
        if (record?.ContainerId is { } cid
            && dispatch.ContainerOwner(cid) is { } owner)
        {
            try
            {
                await dispatch.SendTaskAsync(owner, new OutpostTaskPayload
                {
                    Op = OutpostTaskOps.Destroy,
                    ContainerId = cid,
                }, TaskTimeout, ct);
            }
            finally
            {
                dispatch.UntrackContainer(cid);
                db.Sandboxes.Remove(record);
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        await inner.DeleteAsync(sandboxId, ct);
    }

    /// <inheritdoc />
    public Task<Domain.Sandbox.Sandbox?> GetAsync(
        Guid sandboxId, CancellationToken ct) =>
        inner.GetAsync(sandboxId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<Domain.Sandbox.Sandbox>> SearchAsync(
        SandboxStatus? status, Guid? conversationId, CancellationToken ct) =>
        inner.SearchAsync(status, conversationId, ct);

    /// <inheritdoc />
    public Task<SandboxSpec> UpsertSpecAsync(
        SandboxSpec spec, CancellationToken ct) =>
        inner.UpsertSpecAsync(spec, ct);

    /// <inheritdoc />
    public Task<SandboxSpec?> GetSpecAsync(
        Guid specId, CancellationToken ct) =>
        inner.GetSpecAsync(specId, ct);

    /// <inheritdoc />
    public Task SweepOrphansAsync(CancellationToken ct) =>
        inner.SweepOrphansAsync(ct);

    /// <summary>
    /// Remote op when the record's container is outpost-owned (updates
    /// local status mirror); delegates to the local engine otherwise.
    /// </summary>
    private async Task<Domain.Sandbox.Sandbox> RemoteOrLocal(
        Guid sandboxId, string op, SandboxStatus after,
        Func<Task<Domain.Sandbox.Sandbox>> local, CancellationToken ct)
    {
        var record = await db.Sandboxes.FindAsync([sandboxId], ct);
        if (record?.ContainerId is not { } cid
            || dispatch.ContainerOwner(cid) is not { } owner)
        {
            return await local();
        }

        var evt = await dispatch.SendTaskAsync(owner, new OutpostTaskPayload
        {
            Op = op,
            ContainerId = cid,
        }, TaskTimeout, ct);

        record.Status = evt.Status == OutpostTaskStatus.Completed
            ? after.ToString() : SandboxStatus.Error.ToString();
        record.LastError = evt.Error;
        await db.SaveChangesAsync(ct);
        return record.ToDomain();
    }
}