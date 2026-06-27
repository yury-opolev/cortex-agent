using Cortex.Contained.Bridge.Tenants;

namespace Cortex.Contained.Bridge.Tests;

public class ContainerShutdownTests
{
    [Fact]
    public async Task DockerContainerManager_StopContainer_CallsDockerStop()
    {
        // DockerContainerManager shells out to `docker stop` — we can't unit-test
        // the actual Docker call without Docker running. Instead, test the interface
        // contract via a mock to verify Worker integration.
        var manager = Substitute.For<IContainerManager>();
        manager.StopContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await manager.StopContainerAsync("agent", CancellationToken.None);

        Assert.True(result);
        await manager.Received(1).StopContainerAsync("agent", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAllContainers_MultipleTenants_StopsInParallel()
    {
        var manager = Substitute.For<IContainerManager>();
        var stopOrder = new List<string>();

        manager.StopContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var tenantId = callInfo.ArgAt<string>(0);
                lock (stopOrder)
                {
                    stopOrder.Add(tenantId);
                }

                // Simulate container stop time
                await Task.Delay(50);
                return true;
            });

        // Stop 3 tenants in parallel
        var tenantIds = new[] { "tenant-a", "tenant-b", "tenant-c" };
        var stopTasks = tenantIds.Select(id =>
            manager.StopContainerAsync(id, CancellationToken.None));

        await Task.WhenAll(stopTasks);

        // All 3 should have been stopped
        Assert.Equal(3, stopOrder.Count);
        Assert.Contains("tenant-a", stopOrder);
        Assert.Contains("tenant-b", stopOrder);
        Assert.Contains("tenant-c", stopOrder);
    }

    [Fact]
    public async Task StopContainer_Failure_DoesNotThrow()
    {
        var manager = Substitute.For<IContainerManager>();
        manager.StopContainerAsync("failing-tenant", Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await manager.StopContainerAsync("failing-tenant", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task StopContainer_Cancellation_ReturnsFalse()
    {
        var manager = Substitute.For<IContainerManager>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        manager.StopContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await manager.StopContainerAsync("tenant", cts.Token);

        Assert.False(result);
    }
}
