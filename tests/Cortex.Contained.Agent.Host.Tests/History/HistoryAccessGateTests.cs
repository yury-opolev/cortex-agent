using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class HistoryAccessGateTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task AcquireReadAsync_ManyReaders_RunConcurrently()
    {
        var gate = new HistoryAccessGate();

        await using var first = await gate.AcquireReadAsync(CancellationToken.None);
        await using var second = await gate.AcquireReadAsync(CancellationToken.None);

        Assert.Equal(2, gate.ActiveReaders);
    }

    [Fact]
    public async Task AcquireWriteAsync_WaitsForInFlightReaders()
    {
        var gate = new HistoryAccessGate();
        var read = await gate.AcquireReadAsync(CancellationToken.None);

        var write = gate.AcquireWriteAsync(CancellationToken.None);
        await Task.Delay(Short);
        Assert.False(write.IsCompleted);

        await read.DisposeAsync();
        await using var lease = await write.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AcquireReadAsync_WhileWriterWaits_BlocksUntilWriterReleases()
    {
        var gate = new HistoryAccessGate();
        var firstRead = await gate.AcquireReadAsync(CancellationToken.None);
        var write = gate.AcquireWriteAsync(CancellationToken.None);

        var lateRead = gate.AcquireReadAsync(CancellationToken.None);
        await Task.Delay(Short);
        Assert.False(lateRead.IsCompleted);

        await firstRead.DisposeAsync();
        var writeLease = await write.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(Short);
        Assert.False(lateRead.IsCompleted);

        await writeLease.DisposeAsync();
        await using var readLease = await lateRead.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AcquireWriteAsync_CancelledWhileWaiting_ReleasesQueuedReaders()
    {
        var gate = new HistoryAccessGate();
        var firstRead = await gate.AcquireReadAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var write = gate.AcquireWriteAsync(cts.Token);
        var queuedRead = gate.AcquireReadAsync(CancellationToken.None);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        await using var readLease = await queuedRead.WaitAsync(TimeSpan.FromSeconds(5));
        await firstRead.DisposeAsync();
    }

    [Fact]
    public async Task Lease_DisposedTwice_ReleasesOnce()
    {
        var gate = new HistoryAccessGate();
        var read = await gate.AcquireReadAsync(CancellationToken.None);
        await using var other = await gate.AcquireReadAsync(CancellationToken.None);

        await read.DisposeAsync();
        await read.DisposeAsync();

        Assert.Equal(1, gate.ActiveReaders);
    }
}
