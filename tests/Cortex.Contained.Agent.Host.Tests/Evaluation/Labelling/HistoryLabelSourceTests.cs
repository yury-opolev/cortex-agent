using System.Globalization;
using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tests.History;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Labelling;

/// <summary>The history label source on a real, embedded corpus copy (fake embedder, no Ollama).</summary>
public sealed class HistoryLabelSourceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly AblationConfig[] Configs =
    [
        new() { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword },
        new() { Name = "hybrid-rrf60" },
        new() { Name = "hybrid-rerank", RerankTopN = 10 },
    ];

    private readonly string root = Path.Combine(Path.GetTempPath(), $"history-label-{Guid.NewGuid():N}");
    private long ferry;
    private long asked;
    private long later;

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private async Task<(EvalCorpus Corpus, HistoryLabelSource Source, EvalWorkspace Workspace)> OpenAsync()
    {
        Directory.CreateDirectory(this.root);
        var source = Path.Combine(this.root, "messages.db");
        await using (var store = new MessageStore(source, NullLogger<MessageStore>.Instance))
        {
            for (var i = 0; i < 30; i++)
            {
                await store.SaveMessageAsync("u", i % 2 == 0 ? "ch" : "other", "user", $"we discussed the garden plan number {i} with the neighbours at length", T0.AddHours(i));
            }

            this.ferry = await store.SaveMessageAsync("u", "ch", "user", "we booked the night ferry to TownB for the fourteenth", T0.AddDays(3));
            await store.SaveMessageAsync("a", "ch", "assistant", "I found it: the night ferry to TownB", T0.AddDays(4), toolCalls: """[{"name":"history_search","args":"{}","ok":true}]""");
            this.asked = await store.SaveMessageAsync("u", "ch", "user", "and when did we book it?", T0.AddDays(5));
            this.later = await store.SaveMessageAsync("u", "ch", "user", "the ferry to TownB was late again", T0.AddDays(6));
            await store.AppendAsync(
                new EventDraft(EventTypes.ToolCall, AuthorKinds.Main) { ChannelId = "ch", Ts = T0.AddDays(5).AddSeconds(3), Payload = new JsonObject { ["tool"] = "history_search", ["toolCallId"] = "c1", ["args"] = """{"query":"ferry booking"}""" } },
                CancellationToken.None);
        }

        var workspace = new EvalWorkspace(Path.Combine(this.root, "eval"), EvalProtection.Plaintext);
        Directory.CreateDirectory(workspace.Directory);
        var corpus = await EvalCorpus.CreateAsync(
            source, Path.Combine(this.root, "run"), new HistoryOptions { Enabled = true, TimeZone = "UTC" }, Configs[..2], _ => new FakeHistoryEmbedder(), CancellationToken.None);
        return (corpus, new HistoryLabelSource(corpus, workspace, Configs), workspace);
    }

    [Fact]
    public async Task SampleAndMaterial_SkipRecallEchoesAndPairTheRealRequestWithItsContext()
    {
        var (corpus, source, _) = await this.OpenAsync();
        await using (corpus)
        {
            var seeds = await source.SampleAsync(10, 0.3, CancellationToken.None);

            var real = Assert.Single(seeds, s => s.Stratum == "real");
            Assert.Equal(this.asked.ToString(CultureInfo.InvariantCulture), real.RequestMessageId);
            Assert.DoesNotContain(seeds.SelectMany(s => s.SourceIds), id => id == (this.ferry + 1).ToString(CultureInfo.InvariantCulture));
            var material = (await source.MaterialAsync(real, CancellationToken.None))!;
            Assert.Equal("and when did we book it?", material.Request);
            Assert.Equal(HistoryLabelSource.ContextTurns, material.Context.Count);
            Assert.StartsWith("assistant: I found it", material.Context[^1], StringComparison.Ordinal);
            var synthetic = seeds.First(s => s.Stratum == "synthetic");
            Assert.Equal(synthetic.SourceIds.Count, (await source.MaterialAsync(synthetic, CancellationToken.None))!.Items.Count);
        }
    }

    [Fact]
    public async Task PoolAsync_UnionOfTheSystemsWithoutLaterMessages()
    {
        var (corpus, source, _) = await this.OpenAsync();
        await using (corpus)
        {
            var draft = new DraftedQuery { Id = "h0001", SeedId = "s0001", Text = "ferry TownB", Category = "lookup" };

            var all = await source.PoolAsync(draft, 10, CancellationToken.None);
            var early = await source.PoolAsync(draft with { NotAfter = T0.AddDays(5) }, 10, CancellationToken.None);

            Assert.Contains(this.ferry.ToString(CultureInfo.InvariantCulture), all);
            Assert.Contains(this.later.ToString(CultureInfo.InvariantCulture), all);
            Assert.Contains(this.ferry.ToString(CultureInfo.InvariantCulture), early);
            Assert.DoesNotContain(this.later.ToString(CultureInfo.InvariantCulture), early);
            Assert.Equal(all.Count, all.Distinct().Count());
        }
    }

    [Fact]
    public async Task WriteDatasetAndGuard_DatasetReadableAndGuardUsesTheCorpus()
    {
        var (corpus, source, workspace) = await this.OpenAsync();
        await using (corpus)
        {
            var draft = new DraftedQuery { Id = "h0001", SeedId = "s0001", Text = "overnight boat", Category = "paraphrase", GoldSourceIds = [this.ferry.ToString(CultureInfo.InvariantCulture)] };
            var grades = new Dictionary<string, int> { [this.ferry.ToString(CultureInfo.InvariantCulture)] = 2 };
            var (queries, _) = DatasetFinaliser.Finalise([(draft, grades)], (_, _) => true);

            source.WriteDataset(queries, "gpt", "claude");

            var line = Assert.Single(workspace.ReadDataset());
            Assert.Equal(this.ferry, Assert.Single(line.Judgments).MessageId);
            Assert.True(source.ParaphraseOk("overnight boat", ["we booked the night ferry to TownB"]));
            Assert.False(source.ParaphraseOk("night ferry TownB", ["we booked the night ferry to TownB"]));
        }
    }
}
