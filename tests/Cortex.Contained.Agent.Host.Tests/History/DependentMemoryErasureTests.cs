using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>The owner's per-purge choice to erase dependent memories instead of quarantining them.</summary>
public sealed class DependentMemoryErasureTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = HistoryHarness.T0;

    private readonly HistoryHarness h = new();
    private readonly IMemoryCreationTimes times = Substitute.For<IMemoryCreationTimes>();
    private readonly IMemoryManagementService memories = Substitute.For<IMemoryManagementService>();
    private readonly MemoryQuarantine quarantine;
    private readonly HistoryPurgeService purge;

    public DependentMemoryErasureTests()
    {
        this.times.ListCreationTimesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new MemoryCreationTime("during", T0.AddHours(2)),
            new MemoryCreationTime("kept", T0.AddHours(3)),
            new MemoryCreationTime("later", T0.AddDays(10)),
        ]);
        var clock = new FakeTimeProvider(T0.AddDays(2));
        this.quarantine = new MemoryQuarantine(this.h.Store, this.times, clock);
        this.purge = new HistoryPurgeService(
            this.h.Store, this.h.Gate, this.h.Projector,
            [new MessagesWalErasureStep(this.h.Store), new DependentMemoryErasureStep(this.quarantine, this.memories)],
            this.h.Monitor, clock, NullLogger<HistoryPurgeService>.Instance, this.quarantine);
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private async Task SeedAsync()
    {
        await this.h.SayAsync("a", 0, channel: "x");
        await this.h.SayAsync("b", 60, channel: "x");

        // Kept after an earlier purge of the same period: never deleted by a later one.
        await this.h.Store.RecordKeptMemoryAsync("kept", T0, CancellationToken.None);
    }

    [Fact]
    public async Task PurgeAsync_Delete_ErasesDependentMemoriesInOneBatch()
    {
        await this.SeedAsync();

        var result = await this.purge.PurgeAsync(new HistoryPurgeScope("x", null), null, CancellationToken.None, dependentMemories: DependentMemoryAction.Delete);

        await this.memories.Received(1).DeleteManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 1 && ids.Contains("during")), Arg.Any<CancellationToken>());
        Assert.Equal(1, result.DeletedMemories);
        Assert.Equal(0, result.QuarantinedMemories);
        Assert.True(result.ErasureComplete);
    }

    [Fact]
    public async Task PurgeAsync_Review_QuarantinesAndDeletesNothing()
    {
        await this.SeedAsync();

        var result = await this.purge.PurgeAsync(new HistoryPurgeScope("x", null), null, CancellationToken.None);

        await this.memories.DidNotReceive().DeleteManyAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, result.QuarantinedMemories);
        Assert.Equal(0, result.DeletedMemories);
    }

    [Fact]
    public async Task PurgeAsync_DeleteFails_StaysQuarantinedAndResumes()
    {
        await this.SeedAsync();
        this.memories.DeleteManyAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new IOException("memory.db busy"), _ => 1);

        var result = await this.purge.PurgeAsync(new HistoryPurgeScope("x", null), null, CancellationToken.None, dependentMemories: DependentMemoryAction.Delete);

        Assert.False(result.ErasureComplete);
        Assert.Contains(ErasureSteps.DependentMemories, result.CleanupPending);
        Assert.Equal(1, result.QuarantinedMemories);
        Assert.Equal(0, result.DeletedMemories);

        Assert.Empty(await this.purge.ResumePendingAsync(CancellationToken.None));
        await this.memories.Received(2).DeleteManyAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeAsync_KeepIssuedMidErasure_IsHonouredForTheRemainingBatches()
    {
        var count = (DependentMemoryErasureStep.ChunkSize * 2) + 1;
        var ids = Enumerable.Range(0, count).Select(i => $"d{i:D5}").ToList();
        this.times.ListCreationTimesAsync(Arg.Any<CancellationToken>())
            .Returns(ids.Select(id => new MemoryCreationTime(id, T0.AddHours(2))).ToList());
        await this.SeedAsync();
        var lastId = ids[^1];
        var deleted = new List<string>();
        Task<(IReadOnlyList<string> Kept, IReadOnlyList<string> Skipped)>? keep = null;
        this.memories.DeleteManyAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var batch = call.Arg<IReadOnlyCollection<string>>();
            deleted.AddRange(batch);
            if (keep is null)
            {
                // The owner presses Keep while the first batch is being erased; it queues behind the batch.
                keep = this.quarantine.KeepManyAsync([lastId], CancellationToken.None);
                await Task.Delay(200);
            }

            return batch.Count;
        });

        await this.purge.PurgeAsync(new HistoryPurgeScope("x", null), null, CancellationToken.None, dependentMemories: DependentMemoryAction.Delete);

        Assert.NotNull(keep);
        Assert.Equal([lastId], (await keep).Kept);
        Assert.DoesNotContain(lastId, deleted);
        Assert.Equal(count - 1, deleted.Count);
    }

    [Fact]
    public void ForPurge_Delete_AddsTheStepBeforeVacuum()
    {
        Assert.Equal(
            [ErasureSteps.Index, ErasureSteps.MessagesWal, ErasureSteps.ToolOutput, ErasureSteps.Subagents, ErasureSteps.SmokeArtifacts, ErasureSteps.DependentMemories, ErasureSteps.Vacuum],
            ErasureSteps.ForPurge(HistoryPurgeScope.All, DependentMemoryAction.Delete));
    }
}
