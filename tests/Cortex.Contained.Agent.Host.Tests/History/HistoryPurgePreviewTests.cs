using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>The purge preview counts exactly what the purge then deletes and quarantines, and deletes nothing itself.</summary>
public sealed class HistoryPurgePreviewTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = HistoryHarness.T0;

    private readonly HistoryHarness h = new();
    private readonly IMemoryCreationTimes times = Substitute.For<IMemoryCreationTimes>();
    private readonly HistoryPurgeService purge;

    public HistoryPurgePreviewTests()
    {
        this.times.ListCreationTimesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new MemoryCreationTime("during", T0.AddHours(2)),
            new MemoryCreationTime("later", T0.AddDays(10)),
        ]);
        var clock = new FakeTimeProvider(T0.AddDays(2));
        this.purge = new HistoryPurgeService(
            this.h.Store, this.h.Gate, this.h.Projector, [new MessagesWalErasureStep(this.h.Store)],
            this.h.Monitor, clock, NullLogger<HistoryPurgeService>.Instance,
            new MemoryQuarantine(this.h.Store, this.times, clock));
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    [Fact]
    public async Task PreviewAsync_MatchesThePurgeAndDeletesNothing()
    {
        await this.h.SayAsync("a", 0, channel: "x");
        await this.h.SayAsync("b", 60, channel: "x");
        await this.h.SayAsync("c", 0, channel: "y");
        var scope = new HistoryPurgeScope("x", null);

        var preview = await this.purge.PreviewAsync(scope, CancellationToken.None);

        Assert.Equal(new HistoryPurgePreview(2, 1, T0, T0.AddMinutes(60)), preview);
        Assert.Equal(2, (await this.h.Store.GetMessagesAsync("x")).Count);
        var result = await this.purge.PurgeAsync(scope, null, CancellationToken.None);
        Assert.Equal(preview.Messages, result.Deleted);
        Assert.Equal(preview.DependentMemories, result.QuarantinedMemories);
    }

    [Fact]
    public async Task PreviewAsync_NothingInScope_ReportsZero()
    {
        await this.h.SayAsync("a", 0, channel: "x");

        var preview = await this.purge.PreviewAsync(new HistoryPurgeScope("y", null), CancellationToken.None);

        Assert.Equal(new HistoryPurgePreview(0, 0, null, null), preview);
    }
}
