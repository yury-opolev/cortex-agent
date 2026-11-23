using Cortex.Contained.Agent.Host.Evaluation.Orchestration;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Orchestration;

public sealed class EvaluationRunPlanTests
{
    private static EvaluationRunState At(string phase) => new() { Phase = phase, HistoryKappaItems = 50, MemoryKappaItems = 25, Version = "v3" };

    [Fact]
    public void Jobs_PhasesRunTheirCommandsForBothTools()
    {
        Assert.Equal(["history-eval label --kappa 50"], EvaluationRunPlan.Jobs(At(EvaluationRunPlan.LabelHistory)).Select(j => j.Label));
        Assert.Equal(["history-eval ablate --split dev", "memory-eval ablate --split dev"], EvaluationRunPlan.Jobs(At(EvaluationRunPlan.AblateDev)).Select(j => j.Label));
        Assert.Equal(
            ["history-eval holdout --corpus frozen:v3", "memory-eval holdout --corpus frozen:v3"],
            EvaluationRunPlan.Jobs(At(EvaluationRunPlan.HoldoutFrozen)).Select(j => j.Label));
        Assert.Equal(
            ["eval-corpus compare --version v3", "eval-corpus compare --version v3 --tool memory-eval"],
            EvaluationRunPlan.Jobs(At(EvaluationRunPlan.Compare)).Select(j => j.Label));
        Assert.Empty(EvaluationRunPlan.Jobs(At(EvaluationRunPlan.Grading)));
        Assert.Equal(RunWait.Passphrase, EvaluationRunPlan.WaitOf(EvaluationRunPlan.Passphrase));
        Assert.True(EvaluationRunPlan.IsInternal(EvaluationRunPlan.Select));
    }

    [Fact]
    public void Next_WithoutReranker_SkipsTheGateRPhases()
    {
        var state = At(EvaluationRunPlan.Select);
        state.Selected["history-eval"] = "hybrid-rrf20";

        Assert.Equal(EvaluationRunPlan.Register, EvaluationRunPlan.Next(state));
        state.Phase = EvaluationRunPlan.HoldoutRaw;
        Assert.Equal(EvaluationRunPlan.Suggest, EvaluationRunPlan.Next(state));
        state.Phase = EvaluationRunPlan.Summary;
        Assert.Equal(EvaluationRunPlan.Done, EvaluationRunPlan.Next(state));
    }

    [Fact]
    public void Next_WithReranker_RunsTheCurveAndGateR()
    {
        var state = At(EvaluationRunPlan.Select);
        state.Reranker = true;
        state.Selected["history-eval"] = "hybrid-rrf20";

        Assert.Equal(EvaluationRunPlan.RerankCurve, EvaluationRunPlan.Next(state));
        state.Phase = EvaluationRunPlan.RerankCurve;
        Assert.Equal(["history-eval rerank-curve --condition normal --base hybrid-rrf20"], EvaluationRunPlan.Jobs(state).Select(j => j.Label));
        state.RerankConfigs["history-eval"] = "hybrid-rrf20-rerank-top15-chars1200";
        state.Phase = EvaluationRunPlan.HoldoutRaw;
        Assert.Equal(EvaluationRunPlan.GateR, EvaluationRunPlan.Next(state));
        state.Phase = EvaluationRunPlan.GateR;
        Assert.Equal(["history-eval gate-r --config hybrid-rrf20-rerank-top15-chars1200"], EvaluationRunPlan.Jobs(state).Select(j => j.Label));
    }

    [Fact]
    public void Record_ReportPathsVersionAndLeaks()
    {
        var state = At(EvaluationRunPlan.AblateDev);

        EvaluationRunPlan.Record(state, new RunJob("history-eval", ["ablate", "--split", "dev"]), "x\nReport: /app/state/eval/hybrid/runs/ablate-dev-1/report.md\n");
        EvaluationRunPlan.Record(state, new RunJob("eval-corpus", ["compare", "--version", "v3", "--tool", "memory-eval"]), "table\nReport: /app/state/eval/compare/m.md\n");
        EvaluationRunPlan.Record(state, new RunJob("eval-corpus", ["freeze"]), "Frozen v4: 10 messages");

        Assert.Equal("/app/state/eval/hybrid/runs/ablate-dev-1/report.md", state.Reports["history-eval:ablate"]);
        Assert.Equal("/app/state/eval/compare/m.md", state.Reports["memory-eval:compare"]);
        Assert.Equal("v4", state.Version);
        Assert.Equal(["messages.Content dictionary-term ×3"], EvaluationRunPlan.Leaks("leak: messages.Content dictionary-term ×3\nFreeze refused"));
    }

    [Fact]
    public void Store_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"run-{Guid.NewGuid():N}", "run.json");
        try
        {
            var store = new EvaluationRunStore(path);
            var state = At(EvaluationRunPlan.Kappa);
            state.Log.Add(new RunLogEntry(DateTimeOffset.UnixEpoch, EvaluationRunPlan.Kappa, "kappa", null, "κ 0.700"));

            store.Save(state);

            var loaded = store.Load()!;
            Assert.Equal(EvaluationRunPlan.Kappa, loaded.Phase);
            Assert.Equal("κ 0.700", Assert.Single(loaded.Log).Note);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
