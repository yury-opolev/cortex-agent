using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tests.History;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary><c>history-eval rerank-curve</c> and <c>gate-r</c> on a synthetic corpus with fake embedder and reranker.</summary>
public sealed class HistoryRerankEvalTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"history-rerank-eval-{Guid.NewGuid():N}");
    private readonly string workspace;
    private readonly string source;
    private readonly StringWriter output = new();
    private readonly FakeReranker reranker = new();

    public HistoryRerankEvalTests()
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
            CancellationToken.None,
            new HistoryOptions { EvalArtifactsDirectory = this.workspace },
            _ => this.reranker);

    private async Task SeedAsync()
    {
        Directory.CreateDirectory(this.root);
        long railway, invoice;
        await using (var store = new MessageStore(this.source, NullLogger<MessageStore>.Instance))
        {
            railway = await store.SaveMessageAsync("u", "ch", "user", "we booked the railway journey to TownB", T0);
            await store.SaveMessageAsync("u", "ch", "user", "the railway strike moved the journey", T0.AddHours(3));
            invoice = await store.SaveMessageAsync("u", "ch", "user", "invoice INV2041 is paid", T0.AddHours(6));
        }

        Assert.Equal(0, await this.RunAsync("init"));
        File.WriteAllLines(Path.Combine(this.workspace, "dataset.jsonl"), Enumerable.Range(1, 8).Select(i => EvalDataset.ToLine(i % 2 == 0
            ? new EvalQuery { Id = $"e{i:000}", Text = "train trip", Category = "paraphrase", Judgments = [new EvalJudgment(railway, 2)] }
            : new EvalQuery { Id = $"e{i:000}", Text = "invoice paid", Category = "identifier", Judgments = [new EvalJudgment(invoice, 2)] })));
    }

    private IEnumerable<string> RunFiles(string name) =>
        Directory.EnumerateFiles(Path.Combine(this.workspace, "runs"), name, SearchOption.AllDirectories);

    [Fact]
    public async Task RerankCurve_WritesTheNineCellsForTheCondition_NumbersOnly()
    {
        await this.SeedAsync();

        Assert.Equal(0, await this.RunAsync("rerank-curve", "--split", "all", "--condition", "embedder-busy"));

        var curve = await File.ReadAllTextAsync(Assert.Single(this.RunFiles("curve.md")));
        Assert.Equal(9, curve.Split('\n').Count(l => l.StartsWith("| embedder-busy | top", StringComparison.Ordinal)));
        Assert.DoesNotContain("TownB", curve, StringComparison.Ordinal);
        Assert.NotEmpty(this.reranker.Calls);
    }

    [Fact]
    public async Task RerankCurve_UnknownCondition_Refuses()
    {
        await this.SeedAsync();

        Assert.Equal(2, await this.RunAsync("rerank-curve", "--condition", "sunny"));
        Assert.Contains("--condition", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GateR_Unregistered_Refuses()
    {
        await this.SeedAsync();

        Assert.Equal(2, await this.RunAsync("gate-r", "--config", "hybrid-rerank"));
        Assert.Contains("register", this.output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GateR_Registered_ComparesWithTheSameConfigWithoutReranking()
    {
        await this.SeedAsync();
        var ws = new EvalWorkspace(this.workspace);
        ws.WritePlan(ws.ReadPlan() with
        {
            Configs = [.. ws.ReadPlan().Configs, new AblationConfig { Name = "hybrid-rerank", RerankTopN = 10 }],
            Holdout = new HoldoutPlan { Config = "hybrid-rerank" },
        });
        Assert.Equal(0, await this.RunAsync("register"));

        Assert.Equal(0, await this.RunAsync("gate-r", "--config", "hybrid-rerank"));

        Assert.Contains("Gate R hybrid-rerank vs hybrid-rerank-no-rerank", this.output.ToString(), StringComparison.Ordinal);
        Assert.Single(this.RunFiles("verdict.json"));
    }

    [Fact]
    public async Task Ablate_RerankConfigWithoutAReranker_FailsLoudly()
    {
        await this.SeedAsync();
        var ws = new EvalWorkspace(this.workspace);
        ws.WritePlan(new AblationPlan { Baseline = "hybrid-rrf60", Configs = [new AblationConfig { Name = "hybrid-rrf60" }, new AblationConfig { Name = "hybrid-rerank", RerankTopN = 10 }] });

        Assert.Equal(2, await this.RunAsync("ablate", "--split", "all"));
        Assert.Contains("rerank-curve", this.output.ToString(), StringComparison.Ordinal);
    }
}
