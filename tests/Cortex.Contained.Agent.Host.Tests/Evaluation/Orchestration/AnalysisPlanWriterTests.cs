using Cortex.Contained.Agent.Host.Evaluation.Orchestration;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Orchestration;

public sealed class AnalysisPlanWriterTests
{
    private static readonly CalibrationScore NoCalibration = new(0, 0);

    private static ConfigSummary Summary(string config, double judged) => new(config, 50, 0.5, 0.5, 0.5, judged, null, null, NoCalibration, 100);

    private static PairedComparison Compare(string config, double low, double mean, string slice = "all") => new(config, "keyword-plain", slice, 50, mean, low, mean + 0.1, 0.01);

    [Fact]
    public void Select_HighestLowerBoundAmongEligibleCandidates()
    {
        var report = new AblationReport(
            [],
            [Summary("keyword-plain", 1), Summary("hybrid-rrf60", 0.95), Summary("hybrid-rrf20", 0.95), Summary("hybrid-model-queries", 1), Summary("hybrid-poorly-judged", 0.5)],
            [
                Compare("hybrid-rrf60", 0.01, 0.05),
                Compare("hybrid-rrf20", 0.01, 0.06),
                Compare("hybrid-model-queries", 0.2, 0.3),
                Compare("hybrid-poorly-judged", 0.3, 0.4),
                Compare("hybrid-rrf60", 0.5, 0.6, "stratum:real"),
            ]);

        var chosen = ConfigSelector.Select(report, "keyword-plain", ["hybrid-rrf60", "hybrid-rrf20", "hybrid-poorly-judged"]);

        Assert.Equal("hybrid-rrf20", chosen);
        Assert.Null(ConfigSelector.Select(report, "keyword-plain", ["hybrid-model-queries-not-listed"]));
    }

    [Fact]
    public void SelectCell_FastestQualifyingBestLowerBound()
    {
        RerankCurveRow[] rows =
        [
            new("normal", "top10-chars600", 50, 0.02, 0.01, 0.03, 0.01, 80, 300, 900, 50, 0, 0),
            new("normal", "top30-chars2000", 50, 0.06, 0.04, 0.08, 0.01, 900, 2900, 3100, 50, 0, 0),
            new("normal", "top15-chars1200", 50, 0.04, 0.02, 0.06, 0.01, 120, 600, 1300, 50, 0, 0),
        ];

        Assert.Equal(RerankGrid.Cells().First(c => c.Label == "top15-chars1200"), RerankCellSelector.Select(rows));
        Assert.Null(RerankCellSelector.Select([rows[0] with { Low = -0.01 }]));
    }

    [Fact]
    public void Render_StatesCountsModelsKappaAndTheSelectionRule()
    {
        var plan = AnalysisPlanWriter.Render(new PlanFacts
        {
            Tool = "history-eval",
            Date = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero),
            Queries = 480,
            Dev = 150,
            Categories = new Dictionary<string, int> { ["lookup"] = 200, ["temporal"] = 80 },
            Strata = new Dictionary<string, int> { ["real"] = 120, ["synthetic"] = 360 },
            QueryModel = "gpt-6.1-sol",
            JudgeModel = "claude-opus-5.5",
            PoolDepth = 10,
            Kappa = 0.71,
            ToolKappa = 0.68,
            KappaItems = 75,
            Baseline = "keyword-plain",
            Margin = 0.02,
            Reranker = false,
        });

        Assert.Contains("480 queries, 150 dev / 330 test", plan, StringComparison.Ordinal);
        Assert.Contains("lookup 200, temporal 80", plan, StringComparison.Ordinal);
        Assert.Contains("κ = 0.710 on 75", plan, StringComparison.Ordinal);
        Assert.Contains("(this dataset: 0.680)", plan, StringComparison.Ordinal);
        Assert.Contains("highest lower bound of the paired 95 % CI", plan, StringComparison.Ordinal);
        Assert.Contains("margin 0.02", plan, StringComparison.Ordinal);
        Assert.Contains("not part of this run", plan, StringComparison.Ordinal);
        Assert.Contains("pre-registered", plan, StringComparison.Ordinal);
    }
}
