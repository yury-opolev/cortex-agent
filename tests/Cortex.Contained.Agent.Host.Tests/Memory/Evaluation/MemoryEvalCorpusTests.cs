using Cortex.Contained.Agent.Host.Memory.Evaluation;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;

/// <summary>A memory evaluation runs on a private copy that is deleted afterwards; the live store is never touched.</summary>
public sealed class MemoryEvalCorpusTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"memory-eval-{Guid.NewGuid():N}");
    private readonly MemoryMcpOptions live;

    public MemoryEvalCorpusTests()
    {
        this.live = new MemoryMcpOptions
        {
            DataDirectory = Path.Combine(this.root, "live"),
            Ollama = new OllamaOptions { Dimensions = FakeMemoryEmbedder.Dimensions },
        };
    }

    public void Dispose()
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
    }

    /// <summary>Seeds the "live" store the way MemoryService would (one chunk per memory).</summary>
    internal static async Task SeedAsync(MemoryMcpOptions options, params (string Id, string? Title, string Content, string[] Tags)[] memories)
    {
        using var store = new SqliteVecMemoryStore(Options.Create(options), new NullContentEncryptor(), NullLogger<SqliteVecMemoryStore>.Instance);
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, title, content, tags) in memories)
        {
            var chunk = new ChunkRecord { MemoryId = id, ChunkIndex = 0, Length = content.Length, Title = title, Tags = [.. tags], CreatedAt = now, UpdatedAt = now };
            await store.StoreMemoryAsync(id, content, [chunk], [FakeMemoryEmbedder.Embed(content)], CancellationToken.None);
        }
    }

    private Task SeedLiveAsync() => SeedAsync(
        this.live,
        ("mem-train", "Trip", "Train trip: the night train to TownB on Friday", ["travel"]),
        ("mem-invoice", null, "Invoice INV-2041 was paid in March", ["finance"]),
        ("mem-editor", null, "PersonA prefers a dark editor theme", []));

    [Fact]
    public async Task SearchAsync_RunsEveryModeOnTheCopy()
    {
        await this.SeedLiveAsync();
        var runDirectory = Path.Combine(this.root, "run");
        var query = new MemoryEvalQuery { Id = "m1", Text = "railway journey", Category = "paraphrase" };
        var exact = new MemoryEvalQuery { Id = "m2", Text = "INV-2041", Category = "identifier" };

        await using (var corpus = await MemoryEvalCorpus.CreateAsync(this.live.DataDirectory, runDirectory, this.live, new FakeMemoryEmbedder(), CancellationToken.None))
        {
            var dense = await corpus.SearchAsync(new MemoryAblationConfig { Name = "dense", Mode = MemorySearchMode.Dense }, query, CancellationToken.None);
            var keyword = await corpus.SearchAsync(new MemoryAblationConfig { Name = "keyword", Mode = MemorySearchMode.Keyword }, exact, CancellationToken.None);
            var hybrid = await corpus.SearchAsync(new MemoryAblationConfig { Name = "hybrid" }, exact, CancellationToken.None);

            Assert.Equal("mem-train", dense.Memories[0].MemoryId);
            Assert.True(dense.Confidence > 0);
            Assert.Equal(["mem-invoice"], keyword.Memories.Select(m => m.MemoryId));
            Assert.Equal(7, keyword.Memories[0].Words); // invoice inv 2041 was paid in march
            Assert.Equal("mem-invoice", hybrid.Memories[0].MemoryId);
            Assert.Equal(3, (await corpus.ListMemoriesAsync(CancellationToken.None)).Count);
        }

        Assert.False(Directory.Exists(Path.Combine(runDirectory, "corpus")));
        Assert.True(File.Exists(this.live.DatabasePath));
    }

    [Fact]
    public async Task SearchAsync_TagFilterAndModelQuery()
    {
        await this.SeedLiveAsync();
        var query = new MemoryEvalQuery
        {
            Id = "m1",
            Text = "paid",
            Category = "tag_filtered",
            Tags = ["travel"],
            ModelQuery = new MemoryModelQuery { Text = "invoice", Tags = ["finance"] },
        };

        await using var corpus = await MemoryEvalCorpus.CreateAsync(this.live.DataDirectory, Path.Combine(this.root, "run"), this.live, new FakeMemoryEmbedder(), CancellationToken.None);
        var oracle = await corpus.SearchAsync(new MemoryAblationConfig { Name = "keyword", Mode = MemorySearchMode.Keyword }, query, CancellationToken.None);
        var model = await corpus.SearchAsync(new MemoryAblationConfig { Name = "keyword-model", Mode = MemorySearchMode.Keyword, UseModelQuery = true }, query, CancellationToken.None);

        Assert.Empty(oracle.Memories);
        Assert.Equal(["mem-invoice"], model.Memories.Select(m => m.MemoryId));
    }

    [Fact]
    public async Task SweepAbandoned_RemovesCopiesLeftByKilledRuns_KeepsOpenOnesAndOutputs()
    {
        await this.SeedLiveAsync();
        var runs = Path.Combine(this.root, "runs");
        var abandoned = Path.Combine(runs, "ablate-dev-old", "corpus", "memories");
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "x.memory.data"), "raw text");
        File.WriteAllText(Path.Combine(runs, "ablate-dev-old", "report.md"), "numbers only");
        Directory.CreateDirectory(Path.Combine(runs, "holdout-test-old"));
        File.WriteAllText(Path.Combine(runs, "holdout-test-old", "verdict.json"), "{}");
        var runningDirectory = Path.Combine(runs, "ablate-dev-running");

        await using (var running = await MemoryEvalCorpus.CreateAsync(this.live.DataDirectory, runningDirectory, this.live, new FakeMemoryEmbedder(), CancellationToken.None))
        {
            Assert.Equal(1, MemoryEvalCorpus.SweepAbandoned(runs));

            Assert.True(Directory.Exists(Path.Combine(runningDirectory, "corpus")));
            Assert.Equal(3, (await running.ListMemoriesAsync(CancellationToken.None)).Count);
        }

        Assert.False(Directory.Exists(Path.Combine(runs, "ablate-dev-old", "corpus")));
        Assert.True(File.Exists(Path.Combine(runs, "ablate-dev-old", "report.md")));
        Assert.True(File.Exists(Path.Combine(runs, "holdout-test-old", "verdict.json")));
        Assert.False(Directory.Exists(Path.Combine(runningDirectory, "corpus")));
        Assert.Equal(0, MemoryEvalCorpus.SweepAbandoned(runs));
    }

    [Fact]
    public void SweepAbandoned_NoRunsDirectory_ReturnsZero()
    {
        Assert.Equal(0, MemoryEvalCorpus.SweepAbandoned(Path.Combine(this.root, "nothing")));
    }

    [Fact]
    public async Task SearchAsync_HybridQueryEmbeddingFails_FailsLoudlyInsteadOfScoringKeywordOnly()
    {
        await this.SeedLiveAsync();
        await using var corpus = await MemoryEvalCorpus.CreateAsync(
            this.live.DataDirectory, Path.Combine(this.root, "run"), this.live, new QueryEmbedder((_, _) => throw new HttpRequestException("embedder down")), CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => corpus.SearchAsync(
            new MemoryAblationConfig { Name = "hybrid-rrf60" }, new MemoryEvalQuery { Id = "m1", Text = "INV-2041", Category = "identifier" }, CancellationToken.None));

        Assert.Contains("hybrid-rrf60", error.Message, StringComparison.Ordinal);
        Assert.Contains("embedding failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_EmbedderSlowerThanTheLiveQueryTimeout_StillRunsHybrid()
    {
        this.live.Search.QueryTimeoutMs = 100;
        await this.SeedLiveAsync();
        await using var corpus = await MemoryEvalCorpus.CreateAsync(
            this.live.DataDirectory, Path.Combine(this.root, "run"), this.live,
            new QueryEmbedder(async (text, token) =>
            {
                await Task.Delay(400, token);
                return FakeMemoryEmbedder.Embed(text);
            }),
            CancellationToken.None);

        var run = await corpus.SearchAsync(
            new MemoryAblationConfig { Name = "hybrid-rrf60" }, new MemoryEvalQuery { Id = "m1", Text = "railway journey", Category = "paraphrase" }, CancellationToken.None);

        Assert.Equal("mem-train", run.Memories[0].MemoryId);
    }

    private sealed class QueryEmbedder(Func<string, CancellationToken, Task<float[]>> embed) : MemoryMcp.Core.Services.IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) => embed(text, cancellationToken);

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(FakeMemoryEmbedder.Embed).ToList());
    }

    [Fact]
    public async Task CreateAsync_NoDatabase_Fails()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MemoryEvalCorpus.CreateAsync(Path.Combine(this.root, "missing"), Path.Combine(this.root, "run"), this.live, new FakeMemoryEmbedder(), CancellationToken.None));
    }

    /// <summary>Seeds an encrypted "live" store the way <c>EncryptedMemoryStore</c> keeps it.</summary>
    internal static async Task SeedEncryptedAsync(MemoryMcpOptions options, Cortex.Contained.Agent.Host.Storage.StorageKeyRing keys, params (string Id, string? Title, string Content, string[] Tags)[] memories)
    {
        var cipher = new Cortex.Contained.Agent.Host.Storage.StorageFileCipher(
            keys.KeyFor(Cortex.Contained.Agent.Host.Storage.EncryptedMemoryStore.ContentPurpose), Cortex.Contained.Agent.Host.Storage.EncryptedMemoryStore.ContentPurpose);
        using var store = new SqliteVecMemoryStore(
            Options.Create(options), cipher, NullLogger<SqliteVecMemoryStore>.Instance, keys.PasswordFor(Cortex.Contained.Agent.Host.Storage.EncryptedMemoryStore.DatabasePurpose));
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, title, content, tags) in memories)
        {
            var chunk = new ChunkRecord { MemoryId = id, ChunkIndex = 0, Length = content.Length, Title = title, Tags = [.. tags], CreatedAt = now, UpdatedAt = now };
            await store.StoreMemoryAsync(id, content, [chunk], [FakeMemoryEmbedder.Embed(content)], CancellationToken.None);
        }
    }

    [Fact]
    public async Task CreateAsync_EncryptedStore_CopyEncryptedWithTheEvaluationRingAndSearchable()
    {
        var keys = Cortex.Contained.Agent.Host.Tests.Evaluation.EvalTestKeys.Ring();
        await SeedEncryptedAsync(this.live, keys, ("mem-invoice", null, "Invoice INV-2041 zqmemtext was paid in March", ["finance"]));
        var runDirectory = Path.Combine(this.root, "run");
        var protection = Cortex.Contained.Agent.Host.Evaluation.EvalProtection.Encrypted(keys);

        await using (var corpus = await MemoryEvalCorpus.CreateAsync(this.live.DataDirectory, runDirectory, this.live, new FakeMemoryEmbedder(), CancellationToken.None, protection: protection))
        {
            var corpusFiles = Directory.EnumerateFiles(Path.Combine(runDirectory, "corpus"), "*", SearchOption.AllDirectories).ToList();
            Assert.All(corpusFiles, f => Assert.DoesNotContain("zqmemtext", System.Text.Encoding.UTF8.GetString(ReadShared(f)), StringComparison.Ordinal));
            var keyword = await corpus.SearchAsync(
                new MemoryAblationConfig { Name = "keyword", Mode = MemorySearchMode.Keyword }, new MemoryEvalQuery { Id = "m1", Text = "INV-2041", Category = "identifier" }, CancellationToken.None);
            Assert.Equal(["mem-invoice"], keyword.Memories.Select(m => m.MemoryId));
            Assert.Contains("zqmemtext", (await corpus.ListMemoriesAsync(CancellationToken.None)).Single().Content, StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(Path.Combine(runDirectory, "corpus")));
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
