using Cortex.Contained.Agent.Host.History;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.History;

public sealed class HistoryHostedServicesTests : IAsyncDisposable
{
    private readonly HistoryHarness h = new();

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    [Fact]
    public async Task ProjectorService_ThreeConsecutiveFailures_HaltsWithError()
    {
        await this.h.Store.DisposeAsync();
        var service = new HistoryProjectorService(this.h.Projector, this.h.Status, this.h.Monitor, NullLogger<HistoryProjectorService>.Instance, TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        var status = this.h.Status.Snapshot();
        Assert.True(status.Halted);
        Assert.NotNull(status.LastError);
    }

    [Fact]
    public async Task ProjectorService_Disabled_DoesNothing()
    {
        this.h.Options = new HistoryOptions { Enabled = false };
        var service = new HistoryProjectorService(this.h.Projector, this.h.Status, this.h.Monitor, NullLogger<HistoryProjectorService>.Instance, TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(this.h.Projector.IsInitialized);
        Assert.Empty(this.h.Generations.List());
    }

    [Fact]
    public async Task ProjectorService_Enabled_BuildsAndAppliesJournal()
    {
        await this.h.SayAsync("kayak trip", 0);
        var service = new HistoryProjectorService(this.h.Projector, this.h.Status, this.h.Monitor, NullLogger<HistoryProjectorService>.Instance, TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (this.h.Status.Snapshot().Generation is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await service.StopAsync(CancellationToken.None);
        Assert.True(this.h.Status.Snapshot().BuildComplete);
    }

    [Fact]
    public async Task MaintenanceRunOnce_ResumesPendingAndVacuumsWhenDue()
    {
        var purge = new HistoryPurgeService(
            this.h.Store, this.h.Gate, this.h.Projector,
            [new MessagesWalErasureStep(this.h.Store), new VacuumErasureStep(this.h.Store, this.h.Projector, this.h.Monitor, TimeProvider.System)],
            this.h.Monitor, TimeProvider.System, NullLogger<HistoryPurgeService>.Instance);
        await this.h.Store.AppendErasureTaskAsync(
            new ErasureTask { Id = "left-over", CreatedAt = HistoryHarness.T0, Steps = [ErasureSteps.MessagesWal] }, CancellationToken.None);
        var maintenance = new HistoryMaintenanceService(purge, this.h.Store, this.h.Monitor, TimeProvider.System, NullLogger<HistoryMaintenanceService>.Instance);

        var pending = await maintenance.RunOnceAsync(CancellationToken.None);

        Assert.Empty(pending);
        Assert.Empty(await this.h.Store.ReadPendingErasureAsync(CancellationToken.None));
        Assert.NotNull(await this.h.Store.ReadLastVacuumAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HealthReporter_ReportsLagBacklogAndPendingErasure()
    {
        await this.h.SayAsync("a", 0);
        await this.h.Store.AppendErasureTaskAsync(
            new ErasureTask { Id = "t", CreatedAt = HistoryHarness.T0, Steps = [ErasureSteps.Index] }, CancellationToken.None);
        var reporter = new HistoryHealthReporter(this.h.Store, this.h.Store, this.h.Status, new HistoryToolsSwitch(this.h.Monitor), this.h.Monitor);

        var health = await reporter.SnapshotAsync(CancellationToken.None);

        Assert.True(health.Enabled);
        Assert.Equal(1, health.CheckpointLag);
        Assert.Equal(1, health.JournalBacklog);
        Assert.Equal([ErasureSteps.Index], health.PendingErasureSteps);
        Assert.False(string.IsNullOrEmpty(health.SqliteVersion));
    }
}
