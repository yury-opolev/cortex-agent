using Cortex.Contained.Agent.Host.History;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>The hosted projector loop embeds after applying the journal; embedder failures never halt it.</summary>
public sealed class HistoryEmbeddingServiceTests : IAsyncDisposable
{
    private readonly FakeHistoryEmbedder embedder = new();
    private readonly HistoryHarness h;

    public HistoryEmbeddingServiceTests()
    {
        var options = new HistoryOptions { Enabled = true, TimeZone = "UTC" };
        options.Dense.Enabled = true;
        this.h = new HistoryHarness(options, this.embedder);
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private async Task RunUntilAsync(Func<HistoryStatusSnapshot, bool> done)
    {
        var service = new HistoryProjectorService(this.h.Projector, this.h.Status, this.h.Monitor, NullLogger<HistoryProjectorService>.Instance, TimeSpan.FromMilliseconds(10));
        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done(this.h.Status.Snapshot()) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ProjectorService_DenseEnabled_EmbedsClosedSegments()
    {
        await this.h.SayAsync("kayak trip on the fjord", 0);
        await this.h.SayAsync("later topic", 180);

        await this.RunUntilAsync(s => s.Chunks?.Embedded >= 1);

        var status = this.h.Status.Snapshot();
        Assert.Equal(1, status.Chunks!.Embedded);
        Assert.Equal(1, status.Chunks.Pending);
        Assert.Null(status.LastEmbeddingError);
    }

    [Fact]
    public async Task ProjectorService_EmbedderDown_ReportsErrorWithoutHalting()
    {
        this.embedder.Failure = new InvalidOperationException("embedder down");
        await this.h.SayAsync("kayak trip on the fjord", 0);
        await this.h.SayAsync("later topic", 180);

        await this.RunUntilAsync(s => s.LastEmbeddingError is not null);

        var status = this.h.Status.Snapshot();
        Assert.Equal("embedder down", status.LastEmbeddingError);
        Assert.False(status.Halted);
        Assert.True(status.BuildComplete);
    }
}
