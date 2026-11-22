using Cortex.Contained.Agent.Host.Memory.Evaluation;
using MemoryMcp.Core.Configuration;

namespace Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;

public class MemoryAblationRunnerTests
{
    private static MemoryEvalQuery Query(string id, string category, params string[] relevant) => new()
    {
        Id = id,
        Text = "q",
        Category = category,
        Stratum = id.EndsWith('r') ? "real" : "synthetic",
        Judgments = relevant.Select(m => new MemoryJudgment(m, 2)).ToList(),
        ExpectAbstain = category == "abstention",
    };

    private static MemoryRetrievalRun Run(params string[] memoryIds) =>
        new(memoryIds.Select(m => new ReturnedMemory(m, 5)).ToList(), 0.5, 10);

    [Fact]
    public void Grid_HasUniqueNamesADenseBaselineAndTheRegisteredAblations()
    {
        var grid = MemoryAblationGrid.Default();

        Assert.Equal(grid.Count, grid.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(grid, c => c.Name == MemoryAblationGrid.Baseline && c.Mode == MemorySearchMode.Dense && !c.UseQueryInstruction);
        Assert.Equal([5, 10, 20, 60], grid.Where(c => c.Name.StartsWith("hybrid-rrf", StringComparison.Ordinal)).Select(c => c.RrfK));
        Assert.Contains(grid, c => c.Mode == MemorySearchMode.Keyword && c.KeywordVariant == MemoryKeywordVariant.Stemmed);
        Assert.Contains(grid, c => c.Fusion == FusionMethod.Convex);
        Assert.Contains(grid, c => c.UseModelQuery);
    }

    [Fact]
    public void Apply_CopiesBaselineAndOverridesOnlyAblatedSettings()
    {
        var baseline = new MemorySearchOptions { Candidates = 17 };

        var options = new MemoryAblationConfig { Name = "x", RrfK = 5, KeywordVariant = MemoryKeywordVariant.Stemmed }.Apply(baseline);

        Assert.Equal(17, options.Candidates);
        Assert.Equal(5, options.RrfK);
        Assert.Equal(MemorySearchMode.Hybrid, options.DefaultMode);
        Assert.Equal(MemoryKeywordVariant.Stemmed, options.KeywordVariant);
        Assert.Equal(60, baseline.RrfK);
    }

    [Fact]
    public async Task RunAsync_ScoresComparesAndReportsNumbersOnly()
    {
        var queries = new[] { Query("m1", "lookup", "a"), Query("m2r", "paraphrase", "b"), Query("m3", "multi", "c", "d"), Query("m4", "abstention") };
        var configs = new[] { new MemoryAblationConfig { Name = "dense", Mode = MemorySearchMode.Dense }, new MemoryAblationConfig { Name = "better" } };

        var report = await MemoryAblationRunner.RunAsync(configs, queries, (config, query, _) => Task.FromResult(
            config.Name == "better" ? Run([.. query.Relevant.Order(StringComparer.Ordinal)]) : Run("zzz")), "dense", CancellationToken.None);

        Assert.Equal(8, report.Scores.Count);
        var better = report.Summaries.Single(s => s.Config == "better");
        Assert.Equal(0.75, better.Ndcg10, 12);
        Assert.Equal(1, better.EnumerationF1);
        Assert.Equal(5, better.MedianWordsToFirstRelevant);
        var overall = report.Comparisons.Single(c => c.Slice == "all");
        Assert.Equal(0.75, overall.Mean, 12);
        Assert.NotNull(overall.PValue);
        Assert.Null(report.Comparisons.Single(c => c.Slice == "category:lookup").PValue);
        Assert.Contains(report.Comparisons, c => c.Slice == "stratum:real");
        Assert.Contains("| better |", AblationReporting.Markdown(report, "dev"), StringComparison.Ordinal);
    }
}
