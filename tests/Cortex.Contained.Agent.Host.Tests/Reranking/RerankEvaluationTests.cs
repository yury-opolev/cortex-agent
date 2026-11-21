using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>The latency/quality curve, the Gate R verdict and the embedder-busy load (§4.3).</summary>
public sealed class RerankEvaluationTests
{
    private static AblationScore Score(string config, string query, double ndcg, double latencyMs) =>
        new(config, query, "lookup", "synthetic", ndcg, 0, 0, 1, null, null, 0, false, latencyMs);

    [Fact]
    public void Grid_IsTheNineCellsOfTheSpec()
    {
        Assert.Equal(
            ["top10-chars600", "top10-chars1200", "top10-chars2000", "top15-chars600", "top15-chars1200", "top15-chars2000", "top30-chars600", "top30-chars1200", "top30-chars2000"],
            RerankGrid.Cells().Select(c => c.Label));
    }

    [Fact]
    public async Task CurveAsync_ComparesEachCellWithNoRerank_AndCountsOutcomes()
    {
        string[] queries = ["q1", "q2", "q3", "q4"];
        RerankBudget[] cells = [new(10, 600), new(30, 2000)];

        var (rows, report) = await RerankEvaluation.CurveAsync(
            queries,
            cells,
            RerankConditions.EmbedderBusy,
            (name, budget, query, _) =>
            {
                var outcome = budget is null ? null
                    : budget.TopN == 30 && query == "q4" ? RerankOutcome.Unchanged(3, RerankStatus.Failed, "failed", 2000)
                    : RerankOutcome.Unchanged(3, RerankStatus.Applied, "on", budget.TopN * 10);
                var ndcg = budget is null ? 0.5 : 0.7;
                return Task.FromResult(new RerankRun(Score(name, query, ndcg, 100 + (outcome?.LatencyMs ?? 0)), outcome));
            },
            CancellationToken.None);

        Assert.Equal(2, rows.Count);
        var big = rows[1];
        Assert.Equal(("embedder-busy", "top30-chars2000", 4), (big.Condition, big.Cell, big.Queries));
        Assert.Equal(0.2, big.DeltaNdcg, 6);
        Assert.Equal((3, 1, 0), (big.Applied, big.Failed, big.Skipped));
        Assert.Equal(100, rows[0].P50RerankMs);
        Assert.Contains(report.Summaries, s => s.Config == RerankEvaluation.NoRerank);
    }

    [Fact]
    public void Gate_SignificantGainWithinLatency_Passes()
    {
        var verdict = RerankEvaluation.Gate(new PairedComparison("hybrid-rerank", "hybrid-rerank-no-rerank", "all", 300, 0.04, 0.01, 0.07, 0.003), 1800, 2400);

        Assert.True(verdict.Passed);
    }

    [Fact]
    public void Gate_EmbedderBusyP95AboveBudget_Fails()
    {
        var verdict = RerankEvaluation.Gate(new PairedComparison("c", "b", "all", 300, 0.04, 0.01, 0.07, 0.003), 1800, 2600);

        Assert.True(verdict.SignificantGain);
        Assert.False(verdict.LatencyOk);
        Assert.False(verdict.Passed);
    }

    [Fact]
    public void Gate_CiTouchingZero_IsNotASignificantGain()
    {
        var verdict = RerankEvaluation.Gate(new PairedComparison("c", "b", "all", 300, 0.02, -0.001, 0.05, 0.04), 900, 900);

        Assert.False(verdict.SignificantGain);
        Assert.Contains("does not pass", RerankEvaluation.Describe(verdict), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackgroundLoad_RunsUntilDisposed_CountsFailures()
    {
        var calls = 0;
        var load = BackgroundLoad.Start(_ => Interlocked.Increment(ref calls) % 2 == 0 ? throw new HttpRequestException("busy") : Task.CompletedTask);
        while (Volatile.Read(ref calls) < 6)
        {
            await Task.Delay(10);
        }

        await load.DisposeAsync();
        var after = Volatile.Read(ref calls);
        await Task.Delay(50);

        Assert.Equal(after, Volatile.Read(ref calls));
        Assert.True(load.Completed > 0);
        Assert.True(load.Failed > 0);
    }
}
