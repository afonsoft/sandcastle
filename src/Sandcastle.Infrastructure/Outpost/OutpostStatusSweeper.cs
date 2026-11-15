using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Outpost;

namespace Sandcastle.Infrastructure.Outpost;

/// <summary>
/// SPEC-153 RF-002: periodic liveness sweep — outposts whose heartbeat
/// aged past <c>Outpost:OfflineAfterSec</c> without a live connection go
/// <c>offline</c> (kill-switch for half-open sockets).
/// </summary>
public sealed class OutpostStatusSweeper(
    OutpostChannelRegistry registry,
    IOptions<OutpostOptions> options,
    ILogger<OutpostStatusSweeper> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(
            Math.Max(5, options.Value.OfflineAfterSec / 3));
        var offlineAfter = TimeSpan.FromSeconds(
            Math.Max(10, options.Value.OfflineAfterSec));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await registry.SweepStaleAsync(offlineAfter, ct);
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "outpost status sweep failed");
            }

            await Task.Delay(interval, ct);
        }
    }
}
