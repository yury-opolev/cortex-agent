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
    public async Task CreateAsync_NoDatabase_Fails()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MemoryEvalCorpus.CreateAsync(Path.Combine(this.root, "missing"), Path.Combine(this.root, "run"), this.live, new FakeMemoryEmbedder(), CancellationToken.None));
    }
}
