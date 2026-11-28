using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Observability;

namespace Sandcastle.Infrastructure.Observability;

/// <summary>SPEC-185: periodic budget reconcile + retention cleanup.</summary>
public sealed class ObservabilityWorker(
    IServiceScopeFactory scopes,
    IOptions<ObservabilityOptions> options,
    ILogger<ObservabilityWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(
            Math.Max(5, options.Value.ReconcileIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        await TickAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TickAsync(stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var svc = scope.ServiceProvider
                .GetRequiredService<IObservabilityAnalyticsService>();
            await svc.ReconcileBudgetsAsync("system", periodic: true, ct);
            await svc.CleanupRetainedDiagnosticsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "observability worker tick failed");
        }
    }
}
