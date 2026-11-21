using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>Embedding jobs run outside locks and publish only through the generation, purge and revision fences.</summary>
public sealed class HistoryEmbeddingJobsTests : IAsyncDisposable
{
    private readonly FakeHistoryEmbedder embedder = new();
    private readonly HistoryHarness h;

    public HistoryEmbeddingJobsTests()
    {
        var options = new HistoryOptions { Enabled = true, TimeZone = "UTC" };
        options.Dense.Enabled = true;
        this.h = new HistoryHarness(options, this.embedder);
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private async Task<long> SeedTwoSegmentsAsync()
    {
        var first = await this.h.SayAsync("the night train to TownB leaves at ten", 0);
        await this.h.SayAsync("evening chat about coffee", 120);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        return first;
    }

    private IReadOnlyList<StoredChunk> Chunks()
    {
        using var reader = this.h.Projector.Current!.OpenReader();
        return reader.ReadChunks();
    }

    [Fact]
    public async Task EmbedPendingAsync_ClosedSegmentNow_OpenSegmentAfterDebounce()
    {
        await this.SeedTwoSegmentsAsync();

        var first = await this.h.Projector.EmbedPendingAsync(CancellationToken.None);
        this.h.Time.Advance(TimeSpan.FromMinutes(2));
        var second = await this.h.Projector.EmbedPendingAsync(CancellationToken.None);

        Assert.Equal(1, first.Published);
        Assert.Equal(1, second.Published);
        Assert.Equal("user: the night train to TownB leaves at ten", this.embedder.Documents[0]);
        Assert.All(this.Chunks(), c => Assert.True(c.HasVector));
        Assert.Equal("on", this.h.Status.Snapshot().DenseState);
        Assert.Equal(2, this.h.Status.Snapshot().Chunks!.Embedded);
    }

    [Fact]
    public async Task EmbedPendingAsync_PurgeWhileEmbedding_DiscardsVectors()
    {
        await this.SeedTwoSegmentsAsync();
        this.h.Projector.BeforeVectorPublishHook = () => this.h.Store.DeleteChannelMessagesAsync("ch");

        var round = await this.h.Projector.EmbedPendingAsync(CancellationToken.None);

        Assert.Equal(new HistoryEmbeddingRound(0, 1, 0), round);
        Assert.DoesNotContain(this.Chunks(), c => c.HasVector);
    }

    [Fact]
    public async Task EmbedPendingAsync_ChunkReprojectedWhileEmbedding_RevisionFenceDiscards()
    {
        var id = await this.SeedTwoSegmentsAsync();
        this.h.Projector.BeforeVectorPublishHook = async () =>
        {
            await this.h.Store.UpdateContentAsync(id, "the night train to TownC leaves at ten");
            await this.h.Projector.ApplyPendingAsync(CancellationToken.None);
        };

        var round = await this.h.Projector.EmbedPendingAsync(CancellationToken.None);

        Assert.Equal(0, round.Published);
        Assert.Equal(1, round.Discarded);
        Assert.DoesNotContain(this.Chunks(), c => c.HasVector);
    }

    [Fact]
    public async Task EmbedPendingAsync_GenerationRetiredWhileEmbedding_Discards()
    {
        await this.SeedTwoSegmentsAsync();
        this.h.Projector.BeforeVectorPublishHook = () => this.h.Projector.RetireCurrentAsync(writeLeaseHeld: false, CancellationToken.None);

        var round = await this.h.Projector.EmbedPendingAsync(CancellationToken.None);

        Assert.Equal(new HistoryEmbeddingRound(0, 1, 0), round);
    }

    [Fact]
    public async Task EmbedPendingAsync_RejectedChunk_CountsAttemptAndOthersStillPublish()
    {
        this.embedder.RejectMarker = "TownB";
        await this.SeedTwoSegmentsAsync();
        this.h.Time.Advance(TimeSpan.FromMinutes(2));

        var round = await this.h.Projector.EmbedPendingAsync(CancellationToken.None);

        Assert.Equal(new HistoryEmbeddingRound(1, 0, 1), round);
        using var reader = this.h.Projector.Current!.OpenReader();
        Assert.Equal(1, reader.ReadChunkCounts().Embedded);
    }

    [Fact]
    public async Task EmbedPendingAsync_EmbedderNotAllowed_DoesNothingAndReportsReason()
    {
        this.embedder.UnavailableReason = "endpoint not allowed";
        await this.SeedTwoSegmentsAsync();

        var round = await this.h.Projector.EmbedPendingAsync(CancellationToken.None);

        Assert.Same(HistoryEmbeddingRound.Idle, round);
        Assert.Empty(this.embedder.Documents);
        Assert.Equal("unavailable: endpoint not allowed", this.h.Status.Snapshot().DenseState);
    }

    [Fact]
    public async Task EmbedderFingerprintChange_ForcesRebuild()
    {
        await this.SeedTwoSegmentsAsync();
        this.embedder.Fingerprint = "fake-embedder-v2";

        Assert.Equal(ProjectorApplyResult.RebuildRequired, await this.h.Projector.ApplyPendingAsync(CancellationToken.None));
    }
}
