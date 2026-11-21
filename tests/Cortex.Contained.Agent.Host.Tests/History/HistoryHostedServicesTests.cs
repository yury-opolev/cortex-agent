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

    private HistoryMaintenanceService NewMaintenance(IErasureStep vacuumStep)
    {
        var purge = new HistoryPurgeService(
            this.h.Store, this.h.Gate, this.h.Projector, [new MessagesWalErasureStep(this.h.Store), vacuumStep],
            this.h.Monitor, TimeProvider.System, NullLogger<HistoryPurgeService>.Instance);
        return new HistoryMaintenanceService(purge, this.h.Store, this.h.Store, this.h.Monitor, TimeProvider.System, NullLogger<HistoryMaintenanceService>.Instance);
    }

    private static IErasureStep SpyVacuumStep()
    {
        var step = Substitute.For<IErasureStep>();
        step.Name.Returns(ErasureSteps.Vacuum);
        step.RunAsync(Arg.Any<ErasureTask>(), Arg.Any<CancellationToken>()).Returns(true);
        return step;
    }

    [Fact]
    public async Task MaintenanceRunOnce_FreshDatabase_DoesNotVacuumUntilTheIntervalElapses()
    {
        var vacuum = SpyVacuumStep();

        var pending = await this.NewMaintenance(vacuum).RunOnceAsync(CancellationToken.None);

        Assert.Empty(pending);
        await vacuum.DidNotReceive().RunAsync(Arg.Any<ErasureTask>(), Arg.Any<CancellationToken>());
        Assert.NotNull(await this.h.Store.ReadLastVacuumAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MaintenanceRunOnce_VacuumOlderThanInterval_Vacuums()
    {
        await this.h.Store.VacuumAsync(DateTimeOffset.UtcNow.AddDays(-30), CancellationToken.None);
        var vacuum = SpyVacuumStep();

        await this.NewMaintenance(vacuum).RunOnceAsync(CancellationToken.None);

        await vacuum.Received(1).RunAsync(Arg.Is<ErasureTask>(t => t.Id.StartsWith(VacuumErasureStep.MaintenanceTaskPrefix, StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MaintenanceRunOnce_HistoryDisabled_SkipsTheRoutineVacuumButStillResumesErasure()
    {
        this.h.Options = new HistoryOptions { Enabled = false };
        await this.h.Store.VacuumAsync(DateTimeOffset.UtcNow.AddDays(-30), CancellationToken.None);
        await this.h.Store.AppendErasureTaskAsync(
            new ErasureTask { Id = "left-over", CreatedAt = HistoryHarness.T0, Steps = [ErasureSteps.MessagesWal] }, CancellationToken.None);
        var vacuum = SpyVacuumStep();

        await this.NewMaintenance(vacuum).RunOnceAsync(CancellationToken.None);

        await vacuum.DidNotReceive().RunAsync(Arg.Any<ErasureTask>(), Arg.Any<CancellationToken>());
        Assert.Empty(await this.h.Store.ReadPendingErasureAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MaintenanceRunOnce_HistoryDisabled_PrunesTheJournalUpToItsHead()
    {
        this.h.Options = new HistoryOptions { Enabled = false };
        await this.h.SayAsync("one", 0);
        await this.h.SayAsync("two", 1);

        await this.NewMaintenance(SpyVacuumStep()).RunOnceAsync(CancellationToken.None);

        await using var snapshot = await this.h.Store.OpenSnapshotAsync(CancellationToken.None);
        var meta = await snapshot.ReadMetaAsync(CancellationToken.None);
        Assert.True(meta.JournalHead > 0);
        Assert.Equal(meta.JournalHead, meta.JournalPrunedThrough);
    }

    [Fact]
    public async Task MaintenanceRunOnce_HistoryEnabled_LeavesTheJournalForTheProjector()
    {
        await this.h.SayAsync("one", 0);

        await this.NewMaintenance(SpyVacuumStep()).RunOnceAsync(CancellationToken.None);

        await using var snapshot = await this.h.Store.OpenSnapshotAsync(CancellationToken.None);
        var meta = await snapshot.ReadMetaAsync(CancellationToken.None);
        Assert.Equal(0, meta.JournalPrunedThrough);
    }

    [Fact]
    public async Task HealthReporter_MaintenanceOnlyPending_IsNotAnErasureGap()
    {
        await this.h.Store.AppendErasureTaskAsync(
            new ErasureTask { Id = VacuumErasureStep.MaintenanceTaskPrefix + "x", CreatedAt = HistoryHarness.T0, Steps = [ErasureSteps.Vacuum] }, CancellationToken.None);
        var reporter = new HistoryHealthReporter(this.h.Store, this.h.Store, this.h.Status, new HistoryToolsSwitch(this.h.Monitor), this.h.Monitor);

        var health = await reporter.SnapshotAsync(CancellationToken.None);

        Assert.Equal(0, health.PendingErasureTasks);
        Assert.Empty(health.PendingErasureSteps);
        Assert.Equal(1, health.MaintenancePendingTasks);
    }

    [Fact]
    public async Task VacuumStep_BusyCheckpoint_RetriesWithoutRepeatingTheVacuum()
    {
        var store = Substitute.For<IHistoryErasureStore>();
        store.CheckpointWalAsync(Arg.Any<CancellationToken>()).Returns(
            new WalCheckpointResult(1, 5, 0), new WalCheckpointResult(1, 5, 0), new WalCheckpointResult(0, 0, 0));
        var step = new VacuumErasureStep(store, this.h.Projector, this.h.Monitor, TimeProvider.System);
        var task = new ErasureTask { Id = VacuumErasureStep.MaintenanceTaskPrefix + "y", CreatedAt = HistoryHarness.T0, Steps = [ErasureSteps.Vacuum] };

        Assert.True(await step.RunAsync(task, CancellationToken.None));

        await store.Received(1).VacuumAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await store.Received(3).CheckpointWalAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VacuumStep_CheckpointStaysBusy_FailsThenResumesWithoutVacuumingAgain()
    {
        var store = Substitute.For<IHistoryErasureStore>();
        store.CheckpointWalAsync(Arg.Any<CancellationToken>()).Returns(new WalCheckpointResult(1, 5, 0));
        var step = new VacuumErasureStep(store, this.h.Projector, this.h.Monitor, TimeProvider.System);
        var task = new ErasureTask { Id = VacuumErasureStep.MaintenanceTaskPrefix + "z", CreatedAt = HistoryHarness.T0, Steps = [ErasureSteps.Vacuum] };

        Assert.False(await step.RunAsync(task, CancellationToken.None));
        store.CheckpointWalAsync(Arg.Any<CancellationToken>()).Returns(new WalCheckpointResult(0, 0, 0));
        Assert.True(await step.RunAsync(task, CancellationToken.None));

        await store.Received(1).VacuumAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MaintenanceRunOnce_ResumesPendingAndVacuumsWhenDue()
    {
        await this.h.Store.VacuumAsync(DateTimeOffset.UtcNow.AddDays(-30), CancellationToken.None);
        var purge = new HistoryPurgeService(
            this.h.Store, this.h.Gate, this.h.Projector,
            [new MessagesWalErasureStep(this.h.Store), new VacuumErasureStep(this.h.Store, this.h.Projector, this.h.Monitor, TimeProvider.System)],
            this.h.Monitor, TimeProvider.System, NullLogger<HistoryPurgeService>.Instance);
        await this.h.Store.AppendErasureTaskAsync(
            new ErasureTask { Id = "left-over", CreatedAt = HistoryHarness.T0, Steps = [ErasureSteps.MessagesWal] }, CancellationToken.None);
        var maintenance = new HistoryMaintenanceService(purge, this.h.Store, this.h.Store, this.h.Monitor, TimeProvider.System, NullLogger<HistoryMaintenanceService>.Instance);

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
