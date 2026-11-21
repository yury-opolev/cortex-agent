using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary><c>history-eval</c> on a synthetic corpus with the deterministic fake embedder (no Ollama).</summary>
public sealed class HistoryEvalCliTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"history-eval-{Guid.NewGuid():N}");
    private readonly string workspace;
    private readonly string source;
    private readonly StringWriter output = new();

    public HistoryEvalCliTests()
    {
        this.workspace = Path.Combine(this.root, "eval");
        this.source = Path.Combine(this.root, "messages.db");
    }

    public ValueTask DisposeAsync()
    {
        this.output.Dispose();
        try { Directory.Delete(this.root, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    private Task<int> RunAsync(params string[] args) =>
        HistoryEvalCli.RunAsync(
            [.. args, "--dir", this.workspace, "--source", this.source, "--tz", "UTC"],
            this.output,
            _ => new FakeHistoryEmbedder(new Dictionary<string, string> { ["train"] = "railway", ["trip"] = "journey" }),
            CancellationToken.None);

    private async Task<(long Railway, long Invoice)> SeedAsync()
    {
        Directory.CreateDirectory(this.root);
        await using var store = new MessageStore(this.source, NullLogger<MessageStore>.Instance);
        var railway = await store.SaveMessageAsync("u", "ch", "user", "we booked the railway journey to TownB zqrawtext", T0);
        var invoice = await store.SaveMessageAsync("u", "ch", "user", "invoice INV2041 is paid", T0.AddHours(5));
        return (railway, invoice);
    }

    private void WriteDataset(long railway, long invoice)
    {
        File.WriteAllLines(Path.Combine(this.workspace, "dataset.jsonl"),
        [
            EvalDataset.ToLine(new EvalQuery { Id = "e001", Text = "train trip", Category = "paraphrase", Judgments = [new EvalJudgment(railway, 2)] }),
            EvalDataset.ToLine(new EvalQuery { Id = "e002", Text = "invoice", Category = "identifier", Stratum = "real", Judgments = [new EvalJudgment(invoice, 2)] }),
        ]);
    }

    private IEnumerable<string> OutputFiles() => Directory.EnumerateFiles(Path.Combine(this.workspace, "runs"), "*", SearchOption.AllDirectories)
        .Where(f => Path.GetExtension(f) is ".json" or ".md");

    [Fact]
    public async Task Init_WritesTemplatesAndDefaultGrid()
    {
        Assert.Equal(0, await this.RunAsync("init"));

        Assert.Contains("not covered by purge erasure", this.output.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(this.workspace, "analysis-plan.md")));
        Assert.Contains("pre-registered", await File.ReadAllTextAsync(Path.Combine(this.workspace, "analysis-plan.md")), StringComparison.Ordinal);
        Assert.Equal(AblationGrid.Default().Count, new EvalWorkspace(this.workspace).ReadPlan().Configs.Count);
    }

    [Fact]
    public async Task Ablate_RunDirectory_HasManifestAndIsDestroyedOnlyByAnOverlappingPurge()
    {
        var (railway, invoice) = await this.SeedAsync();
        await this.RunAsync("init");
        this.WriteDataset(railway, invoice);
        new EvalWorkspace(this.workspace).WritePlan(new AblationPlan
        {
            Configs = [new AblationConfig { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword }],
        });
        Assert.Equal(0, await this.RunAsync("ablate", "--split", "all"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var runDir = Directory.GetDirectories(Path.Combine(this.workspace, "runs")).Single();
        var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "manifest.json"))).RootElement;
        Assert.Equal(T0, manifest.GetProperty("corpusFrom").GetDateTimeOffset());
        Assert.Equal(T0.AddHours(5), manifest.GetProperty("corpusTo").GetDateTimeOffset());
        var monitor = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<HistoryOptions>>();
        monitor.CurrentValue.Returns(new HistoryOptions { EvalArtifactsDirectory = this.workspace, SmokeArtifactsDirectory = Path.Combine(this.root, "none") });
        var step = new SmokeArtifactErasureStep(monitor);

        Assert.True(await step.RunAsync(
            new ErasureTask { Id = "t", CreatedAt = T0, RangeFrom = T0.AddDays(30), RangeTo = T0.AddDays(31), Steps = [ErasureSteps.SmokeArtifacts] }, CancellationToken.None));
        Assert.True(Directory.Exists(runDir));

        Assert.True(await step.RunAsync(
            new ErasureTask { Id = "t", CreatedAt = T0, RangeFrom = T0.AddHours(4), RangeTo = T0.AddHours(6), Steps = [ErasureSteps.SmokeArtifacts] }, CancellationToken.None));
        Assert.False(Directory.Exists(runDir));
        Assert.True(File.Exists(Path.Combine(this.workspace, "dataset.jsonl")));
    }

    [Fact]
    public async Task Ablate_AllSplit_WritesNumbersOnlyAndHybridBeatsKeywordOnParaphrase()
    {
        var (railway, invoice) = await this.SeedAsync();
        await this.RunAsync("init");
        this.WriteDataset(railway, invoice);
        new EvalWorkspace(this.workspace).WritePlan(new AblationPlan
        {
            Configs = [new AblationConfig { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword }, new AblationConfig { Name = "hybrid-rrf60" }],
        });

        Assert.Equal(0, await this.RunAsync("ablate", "--split", "all"));

        var results = await File.ReadAllTextAsync(this.OutputFiles().Single(f => f.EndsWith("results.json", StringComparison.Ordinal)));
        Assert.Contains("\"config\": \"hybrid-rrf60\"", results, StringComparison.Ordinal);
        Assert.All(this.OutputFiles(), f => Assert.DoesNotContain("zqrawtext", File.ReadAllText(f), StringComparison.Ordinal));
        var summaries = System.Text.Json.JsonDocument.Parse(results).RootElement.GetProperty("summaries");
        var keyword = summaries.EnumerateArray().Single(s => s.GetProperty("config").GetString() == AblationGrid.Baseline).GetProperty("ndcg10").GetDouble();
        var hybrid = summaries.EnumerateArray().Single(s => s.GetProperty("config").GetString() == "hybrid-rrf60").GetProperty("ndcg10").GetDouble();
        Assert.True(hybrid > keyword, $"hybrid {hybrid} vs keyword {keyword}");
    }

    [Fact]
    public async Task Holdout_Unregistered_Refuses()
    {
        await this.SeedAsync();
        await this.RunAsync("init");

        Assert.Equal(2, await this.RunAsync("holdout"));
        Assert.Contains("register", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_WithoutHoldoutChoice_Refuses_ThenDatasetChangeBlocksHoldout()
    {
        var (railway, invoice) = await this.SeedAsync();
        await this.RunAsync("init");
        this.WriteDataset(railway, invoice);
        Assert.Equal(2, await this.RunAsync("register"));

        var ws = new EvalWorkspace(this.workspace);
        ws.WritePlan(ws.ReadPlan() with { Holdout = new HoldoutPlan { Config = "hybrid-rrf60" } });
        Assert.Equal(0, await this.RunAsync("register"));
        await File.AppendAllTextAsync(ws.DatasetPath, "\n");

        Assert.Equal(2, await this.RunAsync("holdout"));
        Assert.Contains("changed since 'register'", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kappa_AgreementFile_PassesOrFailsAtPointSix()
    {
        Directory.CreateDirectory(this.workspace);
        var labels = Path.Combine(this.workspace, "judge-agreement.csv");
        await File.WriteAllLinesAsync(labels, ["messageId,llm,human", "1,2,2", "2,1,1", "3,0,0", "4,2,2"]);
        Assert.Equal(0, await this.RunAsync("kappa"));

        await File.WriteAllLinesAsync(labels, ["1,2,0", "2,0,2", "3,1,0", "4,0,1"]);
        Assert.Equal(1, await this.RunAsync("kappa", "--labels", labels));
        Assert.Contains("below 0.6", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guard_ParaphraseWithLowOverlap_PassesAndReportsNumbersOnly()
    {
        var (railway, invoice) = await this.SeedAsync();
        await this.RunAsync("init");
        this.WriteDataset(railway, invoice);

        Assert.Equal(0, await this.RunAsync("guard"));

        var guard = await File.ReadAllTextAsync(this.OutputFiles().Single(f => f.EndsWith("guard.json", StringComparison.Ordinal)));
        Assert.Contains("\"queryId\": \"e001\"", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("railway", guard, StringComparison.Ordinal);
    }
}
