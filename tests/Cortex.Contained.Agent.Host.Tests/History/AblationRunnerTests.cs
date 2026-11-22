using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class AblationRunnerTests
{
    private static EvalQuery Query(string id, string category, params long[] relevant) => new()
    {
        Id = id,
        Text = "q",
        Category = category,
        Stratum = id.EndsWith('r') ? "real" : "synthetic",
        Judgments = relevant.Select(m => new EvalJudgment(m, 2)).ToList(),
        ExpectAbstain = category == "abstention",
    };

    private static RetrievalRun Run(params long[] messageIds) =>
        new([new ReturnedHit(messageIds, messageIds.Select(m => new ReturnedLine(m, 5)).ToList())], 0.5, 10);

    [Fact]
    public void Splits_AreDeterministicAndRoughlyProportional()
    {
        var ids = Enumerable.Range(0, 1000).Select(i => $"e{i:D4}").ToList();

        var dev = ids.Count(id => EvalSplits.Assign(id) == EvalSplits.Dev);

        Assert.InRange(dev, 250, 350);
        Assert.Equal(ids.Select(id => EvalSplits.Assign(id)), ids.Select(id => EvalSplits.Assign(id)));
        Assert.All(ids, id => Assert.InRange(EvalSplits.Fold(id, 5), 0, 4));
        Assert.Equal(5, ids.Select(id => EvalSplits.Fold(id, 5)).Distinct().Count());
    }

    [Fact]
    public void Grid_HasUniqueNamesAndCoversThePreRegisteredAblations()
    {
        var grid = AblationGrid.Default();

        Assert.Equal(grid.Count, grid.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(grid, c => c.Name == AblationGrid.Baseline && c.Mode == HistorySearchMode.Keyword);
        Assert.Equal([5, 10, 20, 60], grid.Where(c => c.Name.StartsWith("hybrid-rrf", StringComparison.Ordinal)).Select(c => c.RrfK));
        Assert.Contains(grid, c => !c.UseQueryInstruction);
        Assert.Contains(grid, c => c.KeywordVariant == HistoryKeywordVariant.Trigram);
        Assert.Contains(grid, c => c.Fusion == HistoryFusionMethod.Convex);
    }

    [Fact]
    public void Apply_CopiesBaselineAndOverridesOnlyAblatedSettings()
    {
        var baseline = new HistoryOptions { TimeZone = "UTC", IdleGapMinutes = 17 };

        var options = new AblationConfig { Name = "x", RrfK = 5, KeywordVariant = HistoryKeywordVariant.Stemmed }.Apply(baseline);

        Assert.Equal(17, options.IdleGapMinutes);
        Assert.Equal(5, options.Ranking.RrfK);
        Assert.True(options.Dense.Enabled);
        Assert.Equal(HistoryKeywordVariant.Stemmed, options.KeywordVariant);
        Assert.Equal(60, baseline.Ranking.RrfK);
    }

    [Fact]
    public async Task RunAsync_ScoresComparesAndReportsNumbersOnly()
    {
        var queries = new[] { Query("e1", "lookup", 1), Query("e2r", "paraphrase", 2), Query("e3", "enumeration", 3, 4), Query("e4", "abstention") };
        var configs = new[] { new AblationConfig { Name = "base", Mode = HistorySearchMode.Keyword }, new AblationConfig { Name = "better" } };

        var report = await AblationRunner.RunAsync(configs, queries, (config, query, _) => Task.FromResult(
            config.Name == "better" ? Run([.. query.Relevant]) : Run(99)), "base", CancellationToken.None);

        Assert.Equal(8, report.Scores.Count);
        var better = report.Summaries.Single(s => s.Config == "better");
        Assert.Equal(0.75, better.Ndcg10, 12);
        Assert.Equal(1, better.EnumerationF1);
        var overall = report.Comparisons.Single(c => c.Slice == "all");
        Assert.Equal(0.75, overall.Mean, 12);
        Assert.NotNull(overall.PValue);
        Assert.Null(report.Comparisons.Single(c => c.Slice == "category:lookup").PValue);
        Assert.Contains(report.Comparisons, c => c.Slice == "stratum:real");
        var markdown = AblationRunner.Markdown(report, "dev");
        Assert.Contains("| better |", markdown, StringComparison.Ordinal);
        Assert.Contains("CI only", markdown, StringComparison.Ordinal);
    }
}
