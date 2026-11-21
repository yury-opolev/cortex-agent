using Cortex.Contained.Agent.Host.Memory.Evaluation;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;

public sealed class MemoryEvalCliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"memory-eval-cli-{Guid.NewGuid():N}");
    private readonly MemoryMcpOptions live;
    private readonly MemoryEvalWorkspace workspace;

    public MemoryEvalCliTests()
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
        try
        {
            Directory.Delete(this.root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private async Task<(int Code, string Output)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var code = await MemoryEvalCli.RunAsync([.. args, "--dir", this.workspace.Directory], output, this.live, _ => new FakeMemoryEmbedder(), CancellationToken.None);
        return (code, output.ToString());
    }

    private Task SeedAsync() => MemoryEvalCorpusTests.SeedAsync(
        this.live,
        ("mem-train", "Trip", "Train trip: the night train to TownB on Friday", ["travel"]),
        ("mem-invoice", null, "Invoice INV-2041 was paid in March", ["finance"]),
        ("mem-editor", null, "PersonA prefers a dark editor theme", []));

    private void WriteDataset(int lookups)
    {
        var lines = Enumerable.Range(0, lookups)
            .Select(i => new MemoryEvalQuery { Id = $"m{i:D3}", Text = "invoice paid", Category = "lookup", Judgments = [new MemoryJudgment("mem-invoice", 2)] })
            .Append(new MemoryEvalQuery { Id = "p001", Text = "railway journey", Category = "paraphrase", Judgments = [new MemoryJudgment("mem-train", 2)] })
            .Select(MemoryEvalDataset.ToLine);
        File.WriteAllLines(this.workspace.DatasetPath, lines);
    }

    [Fact]
    public async Task Workflow_InitSampleValidateRegisterAblateHoldoutPoolGuard()
    {
        await this.SeedAsync();

        Assert.Equal(0, (await this.RunAsync("init")).Code);
        Assert.True(File.Exists(this.workspace.AnalysisPlanPath));
        Assert.True(File.Exists(this.workspace.GenerationGuidePath));

        var sample = await this.RunAsync("sample", "--count", "20");
        Assert.Equal(0, sample.Code);
        Assert.Equal(5, File.ReadAllLines(this.workspace.SeedsPath).Length);

        this.WriteDataset(lookups: 250);
        Assert.Equal(0, (await this.RunAsync("validate")).Code);

        var withoutHoldout = await this.RunAsync("register");
        Assert.Equal(2, withoutHoldout.Code);
        Assert.Contains("holdout", withoutHoldout.Output, StringComparison.Ordinal);

        var grid = MemoryAblationGrid.Default().Where(c => c.Name is "dense" or "hybrid-rrf60").ToList();
        this.workspace.WritePlan(new MemoryAblationPlan { Configs = grid, Holdout = new MemoryHoldoutPlan { Config = "hybrid-rrf60" } });
        Assert.Equal(0, (await this.RunAsync("register")).Code);

        Assert.Equal(0, (await this.RunAsync("ablate", "--split", "dev")).Code);
        var holdout = await this.RunAsync("holdout");
        Assert.Equal(0, holdout.Code);
        Assert.Contains("non-inferior", holdout.Output, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(this.workspace.RunsDirectory, "verdict.json", SearchOption.AllDirectories));

        Assert.Equal(0, (await this.RunAsync("pool")).Code);
        Assert.Equal(251, File.ReadAllLines(this.workspace.PoolPath).Length);
        var guard = await this.RunAsync("guard");
        Assert.Contains("Paraphrase guard: 1 passed", guard.Output, StringComparison.Ordinal);

        Assert.Empty(Directory.GetDirectories(this.workspace.RunsDirectory, "corpus", SearchOption.AllDirectories));
        var report = File.ReadAllText(Directory.GetFiles(this.workspace.RunsDirectory, "report.md", SearchOption.AllDirectories)[0]);
        Assert.DoesNotContain("invoice", report, StringComparison.OrdinalIgnoreCase);

        File.AppendAllText(this.workspace.DatasetPath, "\n");
        var changed = await this.RunAsync("holdout");
        Assert.Equal(2, changed.Code);
        Assert.Contains("dataset.jsonl", changed.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_FewerThan250Queries_Refuses()
    {
        await this.RunAsync("init");
        this.WriteDataset(lookups: 10);
        this.workspace.WritePlan(new MemoryAblationPlan { Holdout = new MemoryHoldoutPlan { Config = "hybrid-rrf60" } });

        var register = await this.RunAsync("register");
        var validate = await this.RunAsync("validate");

        Assert.Equal(2, register.Code);
        Assert.Equal(1, validate.Code);
        Assert.Contains("at least 250", validate.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(this.workspace.PreregistrationPath));
    }

    [Fact]
    public async Task Kappa_ReadsGradePairs()
    {
        var labels = Path.Combine(this.root, "labels.csv");
        Directory.CreateDirectory(this.root);
        File.WriteAllLines(labels, ["memoryId,llm,human", "a,2,2", "b,0,0", "c,1,1", "d,2,0"]);

        var result = await this.RunAsync("kappa", "--labels", labels);

        Assert.Equal(0, result.Code);
        Assert.Contains("usable", result.Output, StringComparison.Ordinal);
    }
}
