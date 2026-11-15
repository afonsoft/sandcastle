using Sandcastle.Application.Refactors;
using Sandcastle.Infrastructure.Refactors;
using Sandcastle.Infrastructure.Scans;

namespace Sandcastle.UnitTests.Infrastructure;

public sealed class DispatcherQueueTests
{
    [Fact]
    public async Task Scan_dispatcher_accepts_an_item()
    {
        var dispatcher = new ScanDispatcher();
        var scanId = Guid.NewGuid();

        await dispatcher.EnqueueAsync(scanId, CancellationToken.None);

        dispatcher.ShouldNotBeNull();
    }

    [Fact]
    public async Task Refactor_dispatcher_accepts_an_item()
    {
        var dispatcher = new RefactorDispatcher();

        await dispatcher.EnqueueAsync(
            Guid.NewGuid(), RefactorWorkKind.Plan, CancellationToken.None);

        dispatcher.ShouldNotBeNull();
    }
}
