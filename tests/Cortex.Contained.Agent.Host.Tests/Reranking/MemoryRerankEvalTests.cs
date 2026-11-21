using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary><c>memory-eval rerank-curve</c> and <c>gate-r</c> on a seeded store copy with fake embedder and reranker.</summary>
public sealed class MemoryRerankEvalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"memory-rerank-eval-{Guid.NewGuid():N}");
    private readonly MemoryMcpOptions live;
    private readonly MemoryEvalWorkspace workspace;
    private readonly FakeReranker reranker = new();

    public MemoryRerankEvalTests()
    {
        this.live = new MemoryMcpOptions
        {
            DataDirectory = Path.Combine(this.root, "live"),
            Ollama = new OllamaOptions { Dimensions = FakeMemoryEmbedder.Dimensions },
        };
        this.workspace = new MemoryEvalWorkspace(Path.Combine(this.root, "workspace"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private async Task<(int Code, string Output)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var code = await MemoryEvalCli.RunAsync(
            [.. args, "--dir", this.workspace.Directory], output, this.live, _ => new FakeMemoryEmbedder(), CancellationToken.None, _ => this.reranker);
        return (code, output.ToString());
    }

    private async Task SeedAsync(int lookups)
    {
        await MemoryEvalCorpusTests.SeedAsync(
            this.live,
            ("mem-train", "Trip", "Train trip: the night train to TownB on Friday", ["travel"]),
            ("mem-invoice", null, "Invoice INV-2041 was paid in March", ["finance"]),
            ("mem-editor", null, "PersonA prefers a dark editor theme", []));
        Assert.Equal(0, (await this.RunAsync("init")).Code);
        File.WriteAllLines(this.workspace.DatasetPath, Enumerable.Range(0, lookups)
            .Select(i => new MemoryEvalQuery { Id = $"m{i:D3}", Text = "invoice paid", Category = "lookup", Judgments = [new MemoryJudgment("mem-invoice", 2)] })
            .Select(MemoryEvalDataset.ToLine));
    }

    [Fact]
    public async Task RerankCurve_WritesTheNineCells_AndDeletesTheCopy()
    {
        await this.SeedAsync(lookups: 6);

        var run = await this.RunAsync("rerank-curve", "--split", "all");

        Assert.Equal(0, run.Code);
        var curve = File.ReadAllText(Assert.Single(Directory.GetFiles(this.workspace.RunsDirectory, "curve.md", SearchOption.AllDirectories)));
        Assert.Equal(9, curve.Split('\n').Count(l => l.StartsWith("| normal | top", StringComparison.Ordinal)));
        Assert.DoesNotContain("INV-2041", curve, StringComparison.Ordinal);
        Assert.NotEmpty(this.reranker.Calls);
        Assert.Empty(Directory.GetDirectories(this.workspace.RunsDirectory, "corpus", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task GateR_Registered_ComparesWithTheSameConfigWithoutReranking()
    {
        await this.SeedAsync(lookups: 250);
        var hybrid = MemoryAblationGrid.Default().Single(c => c.Name == "hybrid-rrf60");
        this.workspace.WritePlan(new MemoryAblationPlan
        {
            Baseline = "hybrid-rrf60",
            Configs = [hybrid, hybrid with { Name = "hybrid-rerank", RerankTopN = 10 }],
            Holdout = new MemoryHoldoutPlan { Config = "hybrid-rerank" },
        });
        Assert.Equal(0, (await this.RunAsync("register")).Code);

        var gate = await this.RunAsync("gate-r", "--config", "hybrid-rerank");

        Assert.Equal(0, gate.Code);
        Assert.Contains("Gate R hybrid-rerank vs hybrid-rerank-no-rerank", gate.Output, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(this.workspace.RunsDirectory, "verdict.json", SearchOption.AllDirectories));
    }
}
