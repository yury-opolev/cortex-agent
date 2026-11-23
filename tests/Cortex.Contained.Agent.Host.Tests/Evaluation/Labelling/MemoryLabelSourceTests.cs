using System.Globalization;
using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

/// <summary>The memory label source on a private copy of a seeded store (fake embedder, no Ollama).</summary>
public sealed class MemoryLabelSourceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"memory-label-{Guid.NewGuid():N}");
    private readonly MemoryMcpOptions live;
    private readonly string messages;

    public MemoryLabelSourceTests()
    {
        this.live = new MemoryMcpOptions { DataDirectory = Path.Combine(this.root, "live"), Ollama = new OllamaOptions { Dimensions = FakeMemoryEmbedder.Dimensions } };
        this.messages = Path.Combine(this.root, "messages.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private async Task<(MemoryEvalCorpus Corpus, MemoryLabelSource Source, MemoryEvalWorkspace Workspace, long Asked)> OpenAsync()
    {
        await MemoryEvalCorpusTests.SeedAsync(
            this.live,
            [
                .. Enumerable.Range(0, 20).Select(i => ($"mem-{i:00}", (string?)null, $"note {i} about the garden and the neighbours", new[] { i % 2 == 0 ? "home" : "garden" })),
                ("mem-train", "Trip", "Train trip: the night train to TownB on Friday", ["travel"]),
                ("mem-invoice", null, "Invoice INV-2041 was paid in March", ["finance"]),
            ]);
        long asked;
        await using (var store = new MessageStore(this.messages, NullLogger<MessageStore>.Instance))
        {
            asked = await store.SaveMessageAsync("u", "ch", "user", "which train did I take to TownB?", T0);
            await store.AppendAsync(
                new EventDraft(EventTypes.ToolCall, AuthorKinds.Main) { ChannelId = "ch", Ts = T0.AddSeconds(2), Payload = new JsonObject { ["tool"] = "memory_search", ["toolCallId"] = "c1", ["args"] = """{"query":"train TownB","tags":"[\"travel\"]"}""" } },
                CancellationToken.None);
        }

        var workspace = new MemoryEvalWorkspace(Path.Combine(this.root, "eval"), EvalProtection.Plaintext);
        Directory.CreateDirectory(workspace.Directory);
        var corpus = await MemoryEvalCorpus.CreateAsync(this.live.DataDirectory, Path.Combine(this.root, "run"), this.live, new FakeMemoryEmbedder(), CancellationToken.None);
        MemoryAblationConfig[] configs = [new() { Name = "dense", Mode = MemorySearchMode.Dense }, new() { Name = "keyword-plain", Mode = MemorySearchMode.Keyword }];
        var source = new MemoryLabelSource(corpus, workspace, configs, () =>
        {
            var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(this.messages, null, SqliteOpenMode.ReadOnly, pooling: false));
            connection.Open();
            return connection;
        });
        return (corpus, source, workspace, asked);
    }

    [Fact]
    public async Task SampleMaterialPoolAndWrite_WorkOnTheCopy()
    {
        var (corpus, source, workspace, asked) = await this.OpenAsync();
        await using (corpus)
        {
            var seeds = await source.SampleAsync(12, 0.3, CancellationToken.None);

            var real = Assert.Single(seeds, s => s.Stratum == "real");
            Assert.Equal(asked.ToString(CultureInfo.InvariantCulture), real.RequestMessageId);
            Assert.Equal("which train did I take to TownB?", (await source.MaterialAsync(real, CancellationToken.None))!.Request);
            Assert.Equal("train TownB", MainModelQueryWriter.FromArgs(real.ModelArgs)!.Text);
            Assert.Equal(["travel"], MainModelQueryWriter.FromArgs(real.ModelArgs)!.Tags);

            var draft = new DraftedQuery { Id = "m0001", SeedId = "s0001", Text = "railway journey", Category = "paraphrase", GoldSourceIds = ["mem-train"] };
            var pool = await source.PoolAsync(draft, 3, CancellationToken.None);
            Assert.Equal("mem-train", pool[0]);
            Assert.True(source.ParaphraseOk("railway journey", ["Train trip: the night train to TownB on Friday"]));
            Assert.False(source.ParaphraseOk("night train TownB", ["Train trip: the night train to TownB on Friday"]));

            var (queries, _) = DatasetFinaliser.Finalise([(draft, new Dictionary<string, int> { ["mem-train"] = 2, ["mem-00"] = 0 })], (_, _) => true);
            source.WriteDataset(queries, "gpt", "claude");
            var line = Assert.Single(workspace.ReadDataset());
            Assert.Equal(2, line.Judgments.Count);
            Assert.Equal("mem-train", line.SourceMemoryId);
        }
    }
}
