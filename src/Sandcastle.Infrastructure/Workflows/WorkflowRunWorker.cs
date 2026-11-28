using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Workflows;

namespace Sandcastle.Infrastructure.Workflows;

/// <summary>SPEC-183 RF-001: periodic reconciliation of active workflow runs.</summary>
public sealed class WorkflowRunWorker(
    IServiceProvider services,
    ILogger<WorkflowRunWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                await scope.ServiceProvider
                    .GetRequiredService<IWorkflowService>()
                    .RefreshActiveRunsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "workflow run reconciliation failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}