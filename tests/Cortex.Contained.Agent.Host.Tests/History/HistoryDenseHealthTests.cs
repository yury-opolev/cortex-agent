using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary><c>/health → history.dense</c> reports the dense state and the embedding lag.</summary>
public sealed class HistoryDenseHealthTests : IAsyncDisposable
{
    private readonly FakeHistoryEmbedder embedder = new();
    private readonly HistoryHarness h;

    public HistoryDenseHealthTests()
    {
        var options = new HistoryOptions { Enabled = true, TimeZone = "UTC" };
        options.Dense.Enabled = true;
        this.h = new HistoryHarness(options, this.embedder);
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private Task<HistoryHealth> HealthAsync() =>
        new HistoryHealthReporter(this.h.Store, this.h.Store, this.h.Status, new HistoryToolsSwitch(this.h.Monitor), this.h.Monitor).SnapshotAsync(CancellationToken.None);

    [Fact]
    public async Task Snapshot_ReportsEmbeddingLagThenProgress()
    {
        await this.h.SayAsync("kayak trip", 0);
        await this.h.SayAsync("later topic", 180);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();

        var before = (await this.HealthAsync()).Dense;
        await this.h.Projector.EmbedPendingAsync(CancellationToken.None);
        var after = (await this.HealthAsync()).Dense;

        Assert.Equal(new HistoryDenseHealth("on", 0, 2, 2, 0, HistoryHarness.T0, null), before);
        Assert.Equal(1, after.Embedded);
        Assert.Equal(1, after.Pending);
    }

    [Fact]
    public async Task Snapshot_DenseDisabled_ReportsOff()
    {
        this.h.Options = new HistoryOptions { Enabled = true, TimeZone = "UTC" };

        Assert.Equal("off", (await this.HealthAsync()).Dense.State);
    }
}
