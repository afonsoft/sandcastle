using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Outpost;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Outpost;

/// <summary>Terminal task_event or failure.</summary>
public sealed class OutpostTaskException : Exception
{
    /// <summary>Creates from a failed/cancelled task.</summary>
    public OutpostTaskException(string message, bool timedOut = false)
        : base(message) => TimedOut = timedOut;

    /// <summary>True on dispatch timeout (caller may retry on another outpost).</summary>
    public bool TimedOut { get; }
}

/// <summary>
/// SPEC-153 RF-002: plane-side channel registry — one
/// <see cref="OutpostConnection"/> per connected outpost, pending-task
/// correlation (<c>task_id</c> → TCS), container→outpost ownership and
/// the <see cref="IOutpostTaskDispatcher"/> implementation consumed by
/// the outpost sandbox engine/executor.
/// </summary>
public sealed class OutpostChannelRegistry : IOutpostTaskDispatcher
{
    private readonly ConcurrentDictionary<Guid, OutpostConnection> _connections = new();
    private readonly ConcurrentDictionary<string, Guid> _containerOwners = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutpostChannelRegistry> _logger;

    /// <summary>Creates the registry.</summary>
    public OutpostChannelRegistry(
        IServiceScopeFactory scopeFactory,
        ILogger<OutpostChannelRegistry> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsOnline(Guid outpostId) =>
        _connections.TryGetValue(outpostId, out var c) && !c.Draining;

    /// <inheritdoc />
    public IReadOnlyList<Guid> OnlineOutposts(Guid? orgId) =>
        _connections.Values
            .Where(c => !c.Draining && (orgId is null || c.OrgId == orgId))
            .Select(c => c.OutpostId).ToList();

    /// <inheritdoc />
    public Guid? ContainerOwner(string containerId) =>
        _containerOwners.TryGetValue(containerId, out var id) ? id : null;

    /// <inheritdoc />
    public void TrackContainer(Guid outpostId, string containerId) =>
        _containerOwners[containerId] = outpostId;

    /// <inheritdoc />
    public void UntrackContainer(string containerId) =>
        _containerOwners.TryRemove(containerId, out _);

    /// <inheritdoc />
    public int RunningTasks(Guid outpostId) =>
        _connections.TryGetValue(outpostId, out var c)
            ? c.RunningTaskCount : 0;

    /// <summary>True when a connection is registered for the outpost.</summary>
    public bool IsConnected(Guid outpostId) =>
        _connections.ContainsKey(outpostId);

    /// <summary>
    /// Attaches a freshly-upgraded WebSocket to an outpost row. Replaces
    /// any stale connection for the same outpost (reconnect wins).
    /// </summary>
    public OutpostConnection Attach(
        OutpostInfo outpost, WebSocket socket, string channelKey)
    {
        var conn = new OutpostConnection(
            outpost.Id, outpost.OrgId, socket, channelKey, _logger);
        if (_connections.TryRemove(outpost.Id, out var old))
        {
            _ = old.CloseAsync("replaced by new connection");
        }

        _connections[outpost.Id] = conn;
        return conn;
    }

    /// <summary>Detaches + fails pending tasks; marks row offline.</summary>
    public async Task DetachAsync(OutpostConnection conn, string reason)
    {
        if (_connections.TryRemove(
                new KeyValuePair<Guid, OutpostConnection>(conn.OutpostId, conn)))
        {
            await MarkStatusAsync(conn.OutpostId, OutpostStatuses.Offline);
            _logger.LogInformation(
                "outpost {OutpostId} disconnected ({Reason})",
                conn.OutpostId, reason);
        }
    }

    /// <inheritdoc />
    public async Task<OutpostTaskEvent> SendTaskAsync(
        Guid outpostId, OutpostTaskPayload task,
        TimeSpan timeout, CancellationToken ct)
    {
        if (!_connections.TryGetValue(outpostId, out var conn) || conn.Draining)
        {
            throw new OutpostTaskException(
                $"outpost {outpostId} is not online");
        }

        var result = await conn.DispatchAsync(task, timeout, ct);
        return result.Status == OutpostTaskStatus.Completed
            ? result
            : throw new OutpostTaskException(
                result.Error ?? $"task failed on outpost {outpostId}");
    }

    /// <inheritdoc />
    public async Task DrainAsync(Guid outpostId, CancellationToken ct)
    {
        if (_connections.TryGetValue(outpostId, out var conn))
        {
            conn.Draining = true;
            await conn.SendAsync(OutpostFrames.Create(
                OutpostFrameTypes.Drain, null,
                new { }), ct);
        }
    }

    /// <inheritdoc />
    public async Task KillAndDisconnectAsync(
        Guid outpostId, CancellationToken ct)
    {
        if (!_connections.TryGetValue(outpostId, out var conn))
        {
            return;
        }

        await conn.KillAllAsync(ct);
        await DetachAsync(conn, "revoked");
        await conn.CloseAsync("deregistered");
    }

    /// <summary>
    /// Routes an incoming frame: <c>task_event</c> completes the pending
    /// dispatch, <c>hello</c>/<c>heartbeat</c> update the row.
    /// </summary>
    public async Task<OutpostFrame?> HandleFrameAsync(
        OutpostConnection conn, OutpostFrame frame, CancellationToken ct)
    {
        switch (frame.Type)
        {
            case OutpostFrameTypes.Hello:
                var hello = frame.ReadPayload<OutpostHelloPayload>();
                await OnHelloAsync(conn, hello, ct);
                return OutpostFrames.Create(OutpostFrameTypes.Ack, null,
                    new { status = "ok" });

            case OutpostFrameTypes.Heartbeat:
                var hb = frame.ReadPayload<OutpostHeartbeatPayload>();
                conn.NoteHeartbeat(hb?.RunningTasks);
                await TouchHeartbeatAsync(conn.OutpostId, ct);
                return null;

            case OutpostFrameTypes.TaskEvent:
                var evt = frame.ReadPayload<OutpostTaskEvent>();
                if (frame.TaskId is not null && evt is not null)
                {
                    conn.CompleteTask(frame.TaskId, evt);
                }

                return null;

            case OutpostFrameTypes.Ack:
                return null;

            default:
                _logger.LogDebug("outpost {Id} sent unknown frame {Type}",
                    conn.OutpostId, frame.Type);
                return null;
        }
    }

    /// <summary>Marks status when there is no live connection guard.</summary>
    public async Task MarkStatusAsync(Guid outpostId, string status)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Outposts.IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Id == outpostId);
            if (row is not null && row.RevokedAt is null
                && row.Status != status)
            {
                row.Status = status;
                if (status == OutpostStatuses.Online)
                {
                    row.LastHeartbeat = DateTimeOffset.UtcNow;
                }

                await db.SaveChangesAsync();
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "outpost {Id} status update failed", outpostId);
        }
    }

    private async Task OnHelloAsync(
        OutpostConnection conn, OutpostHelloPayload? hello,
        CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Outposts.IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Id == conn.OutpostId, ct);
            if (row is null || row.RevokedAt is not null)
            {
                return;
            }

            row.Status = OutpostStatuses.Online;
            row.LastHeartbeat = DateTimeOffset.UtcNow;
            if (hello is not null)
            {
                row.Version = hello.Version ?? row.Version;
                row.MaxSandboxes = hello.MaxSandboxes;
                if (hello.Labels is { Count: > 0 })
                {
                    row.LabelsJson = JsonSerializer.Serialize(hello.Labels);
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "outpost {Id} hello handling failed",
                conn.OutpostId);
        }
    }

    private async Task TouchHeartbeatAsync(Guid outpostId, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Outposts.IgnoreQueryFilters()
                .FirstOrDefaultAsync(o => o.Id == outpostId, ct);
            if (row is not null && row.RevokedAt is null)
            {
                row.LastHeartbeat = DateTimeOffset.UtcNow;
                if (row.Status == OutpostStatuses.Offline
                    || row.Status == OutpostStatuses.Pending)
                {
                    row.Status = OutpostStatuses.Online;
                }

                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "outpost {Id} heartbeat persist failed",
                outpostId);
        }
    }

    /// <summary>
    /// Sweeps connections whose heartbeat/heartbeat row went stale —
    /// safety net for dead sockets that missed a close frame.
    /// </summary>
    public async Task SweepStaleAsync(TimeSpan offlineAfter, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - offlineAfter;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stale = await db.Outposts.IgnoreQueryFilters()
                .Where(o => o.RevokedAt == null
                    && o.Status == OutpostStatuses.Online
                    && (o.LastHeartbeat == null || o.LastHeartbeat < cutoff))
                .ToListAsync(ct);
            foreach (var row in stale)
            {
                if (!_connections.ContainsKey(row.Id))
                {
                    row.Status = OutpostStatuses.Offline;
                }
            }

            if (stale.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "outpost stale sweep failed");
        }
    }
}

/// <summary>
/// One live outpost channel: the accepted WebSocket, pending task
/// correlation and signed frame I/O. Outbound-only — the plane never
/// opens a listener towards the outpost.
/// </summary>
public sealed class OutpostConnection(
    Guid outpostId,
    Guid? orgId,
    WebSocket socket,
    string channelKey,
    ILogger logger)
{
    private readonly ConcurrentDictionary<string,
        TaskCompletionSource<OutpostTaskEvent>> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _runningTasks = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _seq;

    /// <summary>Outpost row id.</summary>
    public Guid OutpostId { get; } = outpostId;

    /// <summary>Owning org (org-scoped routing).</summary>
    public Guid? OrgId { get; } = orgId;

    /// <summary>Draining — no new tasks.</summary>
    public bool Draining { get; set; }

    /// <summary>Number of in-flight tasks (scheduler input).</summary>
    public int RunningTaskCount => _runningTasks.Count;

    /// <summary>Ids of tasks the outpost reported running (heartbeat).</summary>
    public IReadOnlyList<string> ReportedRunning { get; private set; } = [];

    /// <summary>
    /// Sends a <c>task_assign</c> and awaits the terminal
    /// <c>task_event</c>. Throws <see cref="OutpostTaskException"/> on
    /// timeout/disconnect/failed.
    /// </summary>
    public async Task<OutpostTaskEvent> DispatchAsync(
        OutpostTaskPayload task, TimeSpan timeout, CancellationToken ct)
    {
        var taskId = $"t-{Interlocked.Increment(ref _seq):x}-{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<OutpostTaskEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(taskId, tcs))
        {
            throw new OutpostTaskException("task id collision");
        }

        _runningTasks[taskId] = 0;
        try
        {
            await SendAsync(OutpostFrames.Create(
                OutpostFrameTypes.TaskAssign, taskId, task), ct);
            using var linked = CancellationTokenSource
                .CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            await using var reg = linked.Token.Register(
                () => tcs.TrySetResult(new OutpostTaskEvent
                {
                    Status = OutpostTaskStatus.Failed,
                    Error = "task timed out or channel closed",
                }));
            var result = await tcs.Task;
            return result;
        }
        finally
        {
            _pending.TryRemove(taskId, out _);
            _runningTasks.TryRemove(taskId, out _);
        }
    }

    /// <summary>Completes a pending dispatch from a task_event frame.</summary>
    public void CompleteTask(string taskId, OutpostTaskEvent evt)
    {
        if (evt.Status == OutpostTaskStatus.Output)
        {
            // streaming progress — keep waiting for the terminal event
            return;
        }

        if (_pending.TryGetValue(taskId, out var tcs))
        {
            tcs.TrySetResult(evt);
        }
    }

    /// <summary>Records heartbeat info.</summary>
    public void NoteHeartbeat(IReadOnlyList<string>? runningTasks) =>
        ReportedRunning = runningTasks ?? [];

    /// <summary>Serializes + signs + sends one frame (send-locked).</summary>
    public async Task SendAsync(OutpostFrame frame, CancellationToken ct)
    {
        var bytes = frame.Signed(channelKey).Serialize();
        await _sendGate.WaitAsync(ct);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                throw new OutpostTaskException("channel closed");
            }

            await socket.SendAsync(bytes, WebSocketMessageType.Text,
                endOfMessage: true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Sends kill frames for every in-flight task.</summary>
    public async Task KillAllAsync(CancellationToken ct)
    {
        foreach (var taskId in _runningTasks.Keys.Concat(_pending.Keys)
            .Distinct())
        {
            try
            {
                await SendAsync(OutpostFrames.Create(
                    OutpostFrameTypes.Kill, taskId,
                    new { reason = "outpost revoked" }), ct);
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "kill frame for {TaskId} failed", taskId);
            }

            if (_pending.TryRemove(taskId, out var tcs))
            {
                tcs.TrySetResult(new OutpostTaskEvent
                {
                    Status = OutpostTaskStatus.Failed,
                    Error = "outpost revoked",
                });
            }
        }
    }

    /// <summary>Fails all pending tasks (disconnect path).</summary>
    public void FailPending(string reason)
    {
        foreach (var (taskId, tcs) in _pending)
        {
            tcs.TrySetResult(new OutpostTaskEvent
            {
                Status = OutpostTaskStatus.Failed,
                Error = reason,
            });
        }
    }

    /// <summary>Closes the socket politely (best-effort).</summary>
    public async Task CloseAsync(string reason)
    {
        try
        {
            FailPending(reason);
            if (socket.State is WebSocketState.Open
                or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, reason,
                    CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // best-effort close
        }
    }
}
