using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Tests.History;
using Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Frozen;

/// <summary>history-eval and memory-eval on a frozen sanitised copy (<c>--corpus frozen:vN</c>).</summary>
public sealed class FrozenCorpusRunTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"frozen-run-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    internal static Task<int> RunHistoryAsync(FreezeSources sources, EvalProtection protection, StringWriter output, params string[] args) =>
        HistoryEvalCli.RunAsync(
            [.. args, "--dir", sources.History.Directory, "--source", sources.MessagesDb, "--tz", "UTC"],
            output,
            _ => new FakeHistoryEmbedder(new Dictionary<string, string> { ["train"] = "railway", ["trip"] = "journey" }),
            CancellationToken.None,
            protection: protection);

    [Fact]
    public async Task HistoryHoldout_FrozenCorpus_RunsOnTheSanitisedCopyEncrypted()
    {
        var state = Path.Combine(this.root, "state");
        var sources = await FrozenCorpusBuilderTests.SeedAsync(state, EvalTestKeys.Ring(), this.protection);
        var store = new FrozenCorpusStore(Path.Combine(state, "eval"));
        await FrozenCorpusBuilder.FreezeAsync(sources, store, this.protection, TimeProvider.System, CancellationToken.None);
        using var output = new StringWriter();

        Assert.Equal(0, await RunHistoryAsync(sources, this.protection, output, "holdout", "--corpus", "frozen:v1"));

        SqliteConnection.ClearAllPools();
        var run = Directory.GetDirectories(Path.Combine(store.DirectoryOf("v1"), "history", "runs")).Single();
        Assert.True(File.Exists(Path.Combine(run, "verdict.json")));
        Assert.Equal(Cortex.Contained.Contracts.Security.StoredFileState.Encrypted, Cortex.Contained.Contracts.Security.SqliteFileHeader.Classify(Path.Combine(run, "messages.db")));
        Assert.False(Directory.Exists(Path.Combine(sources.History.Directory, "runs")));
    }

    [Theory]
    [InlineData("frozen:v9")]
    [InlineData("v1")]
    public async Task HistoryValidate_UnknownOrMalformedCorpus_Refused(string corpus)
    {
        var sources = await FrozenCorpusBuilderTests.SeedAsync(Path.Combine(this.root, "state"), EvalTestKeys.Ring(), this.protection);
        using var output = new StringWriter();

        Assert.Equal(2, await RunHistoryAsync(sources, this.protection, output, "validate", "--corpus", corpus));
    }

    [Fact]
    public async Task MemoryCorpus_FromSanitisedSnapshot_SearchesByTheOriginalIds()
    {
        var snapshot = Path.Combine(this.root, "memories.jsonl");
        Directory.CreateDirectory(this.root);
        this.protection.WriteLines(snapshot,
        [
            System.Text.Json.JsonSerializer.Serialize(new MemorySeedSource("mem-invoice", null, ["finance"], "Invoice INV-2041 was paid by Personab3k9x2m"), System.Text.Json.JsonSerializerOptions.Web),
            System.Text.Json.JsonSerializer.Serialize(new MemorySeedSource("mem-train", "Trip", ["travel"], "Train trip to Placeq7w2e9rt"), System.Text.Json.JsonSerializerOptions.Web),
        ]);
        var live = new MemoryMcpOptions { DataDirectory = Path.Combine(this.root, "live"), Ollama = new OllamaOptions { Dimensions = FakeMemoryEmbedder.Dimensions } };
        var workspace = new MemoryEvalWorkspace(Path.Combine(this.root, "memory"), this.protection.ForFrozenSource(), snapshot);

        await using var corpus = await MemoryEvalCorpus.OpenForAsync(workspace, live, new FakeMemoryEmbedder(), Path.Combine(this.root, "run"), CancellationToken.None);
        var run = await corpus.SearchAsync(
            new MemoryAblationConfig { Name = "keyword", Mode = MemorySearchMode.Keyword }, new MemoryEvalQuery { Id = "m1", Text = "INV-2041", Category = "identifier" }, CancellationToken.None);

        Assert.Equal(["mem-invoice"], run.Memories.Select(m => m.MemoryId));
    }
}
