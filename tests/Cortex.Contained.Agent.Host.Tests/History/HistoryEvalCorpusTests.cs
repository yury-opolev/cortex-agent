using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>A hybrid configuration must be scored as hybrid: a degraded search fails the run instead of being counted as keyword-only.</summary>
public sealed class HistoryEvalCorpusTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"history-eval-corpus-{Guid.NewGuid():N}");

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(this.root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }

        await Task.CompletedTask;
    }

    private async Task<string> SeedAsync()
    {
        Directory.CreateDirectory(this.root);
        var source = Path.Combine(this.root, "messages.db");
        await using var store = new MessageStore(source, NullLogger<MessageStore>.Instance);
        await store.SaveMessageAsync("u", "ch", "user", "we booked the railway journey to TownB", T0);
        await store.SaveMessageAsync("u", "ch", "user", "invoice INV2041 is paid", T0.AddHours(5));
        return source;
    }

    private static readonly EvalQuery Query = new() { Id = "e001", Text = "train trip", Category = "paraphrase" };

    [Fact]
    public async Task SearchAsync_HybridQueryEmbeddingFails_FailsLoudlyInsteadOfScoringKeywordOnly()
    {
        var source = await this.SeedAsync();
        var embedder = new FakeHistoryEmbedder();
        var hybrid = new AblationConfig { Name = "hybrid-rrf60" };
        await using var corpus = await EvalCorpus.CreateAsync(
            source, Path.Combine(this.root, "run"), new HistoryOptions { Enabled = true, TimeZone = "UTC" }, [hybrid], _ => embedder, CancellationToken.None);
        embedder.Failure = new HttpRequestException("embedder down");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => corpus.SearchAsync(hybrid, Query, CancellationToken.None));

        Assert.Contains("hybrid-rrf60", error.Message, StringComparison.Ordinal);
        Assert.Contains("embedder failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_EmbedderSlowerThanTheLiveQueryTimeout_StillRunsHybrid()
    {
        var source = await this.SeedAsync();
        var embedder = new SlowQueryEmbedder(new FakeHistoryEmbedder(new Dictionary<string, string> { ["train"] = "railway", ["trip"] = "journey" }));
        var baseline = new HistoryOptions { Enabled = true, TimeZone = "UTC" };
        baseline.Dense.QueryTimeoutMs = 100;
        var hybrid = new AblationConfig { Name = "hybrid-rrf60" };
        await using var corpus = await EvalCorpus.CreateAsync(source, Path.Combine(this.root, "run"), baseline, [hybrid], _ => embedder, CancellationToken.None);
        embedder.QueryDelay = TimeSpan.FromMilliseconds(400);

        var run = await corpus.SearchAsync(hybrid, Query, CancellationToken.None);

        Assert.NotEmpty(run.Hits);
    }

    private sealed class SlowQueryEmbedder(FakeHistoryEmbedder inner) : IHistoryEmbedder
    {
        public TimeSpan QueryDelay { get; set; }

        public string Fingerprint => inner.Fingerprint;

        public string? UnavailableReason => inner.UnavailableReason;

        public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            inner.EmbedDocumentsAsync(texts, cancellationToken);

        public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken)
        {
            await Task.Delay(this.QueryDelay, cancellationToken);
            return await inner.EmbedQueryAsync(query, cancellationToken);
        }
    }
}
