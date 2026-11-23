using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Evaluation.Orchestration;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Orchestration;

public sealed class EvaluationRunApiTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"run-api-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
    private readonly EvaluationJobService jobs;
    private readonly EvaluationRunService runs;
    private readonly GradingService grading;
    private readonly EvaluationRunApi api;

    public EvaluationRunApiTests()
    {
        var workspaces = new Dictionary<string, string> { ["history-eval"] = Path.Combine(this.root, "hybrid"), ["memory-eval"] = Path.Combine(this.root, "memory") };
        Directory.CreateDirectory(workspaces["history-eval"]);
        Directory.CreateDirectory(workspaces["memory-eval"]);
        var commands = new Dictionary<string, EvaluationCommand>(StringComparer.Ordinal)
        {
            ["history-eval"] = (_, _) => Task.FromResult(0),
            ["memory-eval"] = (_, _) => Task.FromResult(0),
        };
        this.jobs = new EvaluationJobService(commands, () => this.protection, TimeProvider.System, NullLogger<EvaluationJobService>.Instance);
        this.grading = new GradingService(workspaces, () => this.protection);
        var environment = new RunEnvironment(workspaces["history-eval"], workspaces["memory-eval"], Path.Combine(this.root, "eval"), () => this.protection, () => false, () => new LabellingOptions());
        this.runs = new EvaluationRunService(this.jobs, this.grading, environment, TimeProvider.System, NullLogger<EvaluationRunService>.Instance);
        this.api = new EvaluationRunApi(this.runs, this.grading, () => new LabellingOptions());
        KappaSampleFile.Write(workspaces["history-eval"], this.protection,
            [new KappaItem { Tool = "history-eval", QueryId = "h1", CandidateId = "7", LlmGrade = 2, Query = "zqquery", Candidate = "zqcandidate" }, new KappaItem { Tool = "history-eval", QueryId = "h2", CandidateId = "8", LlmGrade = 0, Query = "zqquery2", Candidate = "zqcandidate2" }]);
    }

    public void Dispose()
    {
        this.runs.Dispose();
        this.jobs.Dispose();
        Directory.Delete(this.root, recursive: true);
    }

    [Fact]
    public void Run_NoRunYet_ShowsTheConfiguredModels()
    {
        var run = this.api.Run();

        Assert.False(run.Exists);
        Assert.Equal(("gpt-6.1-sol", "claude-opus-5.5"), (run.QueryModel, run.JudgeModel));
    }

    [Fact]
    public void Control_Start_RunsAndListsThePhases()
    {
        var result = this.api.Control(new EvaluationRunCommandDto { Action = EvaluationRunActions.Start });

        Assert.True(result.Ok);
        Assert.Equal(RunStatus.Running, result.Run.Status);
        Assert.Equal(("prepare", "current"), (result.Run.Phases[0].Name, result.Run.Phases[0].State));
        Assert.Equal("pending", result.Run.Phases[^1].State);
        Assert.Equal("history-eval init", result.Run.CurrentJob);
        Assert.DoesNotContain(result.Run.Phases, p => p.Name == EvaluationRunPlan.Done);
    }

    [Fact]
    public void Control_WrongMomentOrUnknownAction_ExplainsWithoutChangingTheRun()
    {
        var export = this.api.Control(new EvaluationRunCommandDto { Action = EvaluationRunActions.Export, Passphrase = "long enough passphrase" });
        var unknown = this.api.Control(new EvaluationRunCommandDto { Action = "skip-kappa" });

        Assert.False(export.Ok);
        Assert.Contains("not waiting", export.Error, StringComparison.Ordinal);
        Assert.Contains("Unknown action", unknown.Error, StringComparison.Ordinal);
        Assert.False(unknown.Run.Exists);
    }

    [Fact]
    public void ItemAndGrade_MapTheSampleAndProgress()
    {
        var item = this.api.Item(0);
        var missing = this.api.Item(2);
        this.api.Grade(new GradeRequestDto(0, 2));
        var progress = this.api.Grade(new GradeRequestDto(1, 0));

        Assert.Equal((true, "zqquery", "zqcandidate", 2), (item.Exists, item.Query, item.Candidate, item.Progress.Total));
        Assert.False(missing.Exists);
        Assert.True(progress.Complete);
        Assert.Equal(1.0, progress.Kappa);
    }
}
