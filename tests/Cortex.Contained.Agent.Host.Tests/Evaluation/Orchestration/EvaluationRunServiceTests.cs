using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Calibration;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Evaluation.Orchestration;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Orchestration;

/// <summary>The whole run against fake evaluation commands: no corpus, no LLM, no embedder.</summary>
public sealed class EvaluationRunServiceTests : IDisposable
{
    private const string Passphrase = "correct horse battery";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"eval-run-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
    private readonly RunEnvironment environment;
    private readonly List<string> calls = [];
    private EvaluationJobService jobs;
    private EvaluationRunService service;
    private int freezes;
    private readonly Queue<int> labelExits = new();
    private TaskCompletionSource<int>? initGate;

    public EvaluationRunServiceTests()
    {
        this.environment = new RunEnvironment(
            Path.Combine(this.root, "hybrid"),
            Path.Combine(this.root, "memory"),
            Path.Combine(this.root, "eval"),
            () => this.protection,
            () => false,
            () => new LabellingOptions { HistoryQueries = 500, MemoryQueries = 250 });
        (this.jobs, this.service) = this.Create();
    }

    public void Dispose()
    {
        this.service.Dispose();
        this.jobs.Dispose();
        Directory.Delete(this.root, recursive: true);
    }

    private (EvaluationJobService Jobs, EvaluationRunService Service) Create()
    {
        var commands = new Dictionary<string, EvaluationCommand>(StringComparer.Ordinal)
        {
            ["history-eval"] = (run, ct) => this.FakeAsync("history-eval", run, ct),
            ["memory-eval"] = (run, ct) => this.FakeAsync("memory-eval", run, ct),
            ["eval-corpus"] = (run, ct) => this.FakeAsync("eval-corpus", run, ct),
        };
        var jobs = new EvaluationJobService(commands, () => this.protection, TimeProvider.System, NullLogger<EvaluationJobService>.Instance);
        var grading = new GradingService(
            new Dictionary<string, string> { ["history-eval"] = this.environment.HistoryWorkspace, ["memory-eval"] = this.environment.MemoryWorkspace },
            () => this.protection);
        return (jobs, new EvaluationRunService(jobs, grading, this.environment, TimeProvider.System, NullLogger<EvaluationRunService>.Instance));
    }

    private GradingService Grading() => new(
        new Dictionary<string, string> { ["history-eval"] = this.environment.HistoryWorkspace, ["memory-eval"] = this.environment.MemoryWorkspace },
        () => this.protection);

    // Each fake command writes what the real one writes (files and the lines the run parses).
    private async Task<int> FakeAsync(string tool, EvaluationRun run, CancellationToken cancellationToken)
    {
        var command = string.Join(' ', run.Args);
        this.calls.Add(tool + " " + command);
        var memory = tool == "memory-eval";
        var directory = tool == "eval-corpus" ? this.environment.CorpusDirectory : this.environment.Workspace(tool);
        Directory.CreateDirectory(directory);
        var runs = Path.Combine(directory, "runs");
        switch (run.Args[0])
        {
            case "init" when this.initGate is { } gate:
                return await gate.Task.WaitAsync(cancellationToken);
            case "init" when memory:
                new MemoryEvalWorkspace(directory).WritePlan(new MemoryAblationPlan { Configs = [new() { Name = "dense", Mode = MemorySearchMode.Dense }, new() { Name = "hybrid-rrf20", RrfK = 20 }] });
                return 0;
            case "init":
                new EvalWorkspace(directory).WritePlan(new AblationPlan { Configs = [new() { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword }, new() { Name = "hybrid-rrf20", RrfK = 20 }, new() { Name = "hybrid-model-queries", UseModelQuery = true }] });
                return 0;
            case "label" when this.labelExits.TryDequeue(out var exit):
                await run.Output.WriteLineAsync("label stopped: exit " + exit);
                return exit;
            case "label":
                var prefix = memory ? "m" : "h";
                run.Protection.WriteLines(Path.Combine(directory, "dataset.jsonl"), Enumerable.Range(1, 4).Select(i => memory
                    ? MemoryEvalDataset.ToLine(new MemoryEvalQuery { Id = $"m{i}", Text = "zq", Category = "lookup", Stratum = i == 1 ? "real" : "synthetic", Judgments = [new MemoryJudgment("a", 2)] })
                    : EvalDataset.ToLine(new EvalQuery { Id = $"h{i}", Text = "zq", Category = "lookup", Judgments = [new EvalJudgment(i, 2)] })));
                KappaSampleFile.Write(directory, run.Protection, Enumerable.Range(0, memory ? 2 : 3)
                    .Select(i => new KappaItem { Tool = tool, QueryId = $"{prefix}{i}", CandidateId = $"{i}", LlmGrade = i % 3, Query = "zqquery", Candidate = "zqcandidate" }).ToList());
                await run.Output.WriteLineAsync("Labelled 4 queries");
                return 0;
            case "ablate":
                var ablate = Path.Combine(runs, "ablate-dev-1");
                Directory.CreateDirectory(ablate);
                var baseline = memory ? "dense" : AblationGrid.Baseline;
                var calibration = new CalibrationScore(0, 0);
                var report = new AblationReport(
                    [],
                    [new(baseline, 4, 0.4, 0.4, 0.4, 1, null, null, calibration, 10), new("hybrid-rrf20", 4, 0.5, 0.5, 0.5, 1, null, null, calibration, 12), new("hybrid-model-queries", 4, 0.9, 0.9, 0.9, 1, null, null, calibration, 12)],
                    [new("hybrid-rrf20", baseline, "all", 4, 0.1, 0.02, 0.2, 0.04), new("hybrid-model-queries", baseline, "all", 4, 0.5, 0.4, 0.6, 0.01)]);
                await File.WriteAllTextAsync(Path.Combine(ablate, "results.json"), JsonSerializer.Serialize(report, JsonSerializerOptions.Web), cancellationToken);
                await run.Output.WriteLineAsync("Report: " + Path.Combine(ablate, "report.md"));
                return 0;
            case "register":
                await File.WriteAllTextAsync(Path.Combine(directory, "preregistration.json"), "{}", cancellationToken);
                return 0;
            case "holdout":
                var frozen = run.Args.Contains("--corpus");
                var holdout = frozen
                    ? Path.Combine(this.environment.EvaluationDirectory, "frozen", "v1", memory ? "memory" : "history", "runs", "holdout-test-1")
                    : Path.Combine(runs, "holdout-test-1");
                Directory.CreateDirectory(holdout);
                await File.WriteAllTextAsync(Path.Combine(holdout, "verdict.json"), string.Create(CultureInfo.InvariantCulture, $"{{\"config\":\"hybrid-rrf20\",\"mean\":{(frozen ? 0.03 : 0.04)},\"nonInferior\":true}}"), cancellationToken);
                return 0;
            case "freeze" when ++this.freezes == 1:
                await run.Output.WriteLineAsync("leak: messages.Content dictionary-term ×2");
                return 1;
            case "freeze":
                await run.Output.WriteLineAsync("Frozen v1: 10 messages, 3 memories");
                return 0;
            case "compare":
                var compare = Path.Combine(this.environment.EvaluationDirectory, "compare", (memory || run.Args.Contains("memory-eval") ? "memory" : "history") + ".md");
                Directory.CreateDirectory(Path.GetDirectoryName(compare)!);
                await File.WriteAllTextAsync(compare, "| Δ |", cancellationToken);
                await run.Output.WriteLineAsync("Report: " + compare);
                return 0;
            case "export":
                return run.Passphrase == Passphrase ? 0 : 2;
            default:
                return 0;
        }
    }

    private async Task<EvaluationRunState> UntilAsync(Func<EvaluationRunState, bool> done)
    {
        for (var i = 0; i < 300; i++)
        {
            this.service.Tick();
            await this.jobs.WhenIdleAsync();
            var state = this.service.Snapshot()!;
            if (done(state))
            {
                return state;
            }
        }

        throw new InvalidOperationException("The run did not get there: " + JsonSerializer.Serialize(this.service.Snapshot()));
    }

    private void GradeAll(Func<int, int> grade)
    {
        var grading = this.Grading();
        for (var i = 0; grading.Item(i) is not null; i++)
        {
            grading.Grade(i, grade(i));
        }
    }

    [Fact]
    public async Task Run_WholeProtocol_StopsOnlyForTheOwnersThreeInputs()
    {
        Assert.Null(this.service.Start(fresh: false));

        var state = await this.UntilAsync(s => s.Waiting == RunWait.Grading);
        Assert.Equal((50, 25), (state.HistoryKappaItems, state.MemoryKappaItems));
        Assert.Contains("history-eval label --kappa 50", this.calls);
        Assert.Contains("memory-eval guard", this.calls);

        this.GradeAll(i => i % 3);
        Assert.Null(this.service.SubmitGrading());
        state = await this.UntilAsync(s => s.Waiting == RunWait.Dictionary);
        Assert.Equal(1.0, state.Kappa);
        Assert.Contains("pre-registered", await File.ReadAllTextAsync(Path.Combine(this.environment.HistoryWorkspace, "analysis-plan.md")), StringComparison.Ordinal);
        Assert.Contains("(rubric `default`)", await File.ReadAllTextAsync(Path.Combine(this.environment.MemoryWorkspace, "analysis-plan.md")), StringComparison.Ordinal);
        Assert.Equal("hybrid-rrf20", new EvalWorkspace(this.environment.HistoryWorkspace).ReadPlan().Holdout!.Config);
        Assert.Equal("hybrid-rrf20", new MemoryEvalWorkspace(this.environment.MemoryWorkspace).ReadPlan().Holdout!.Config);
        Assert.True(this.calls.IndexOf("history-eval register") < this.calls.IndexOf("history-eval holdout"));
        Assert.True(this.calls.IndexOf("history-eval ablate --split dev") < this.calls.IndexOf("history-eval register"));
        Assert.EndsWith("verdict.json", state.Reports["history-eval:holdout"], StringComparison.Ordinal);

        Assert.Null(this.service.ApproveDictionary([new DictionaryTerm("PersonA", "person"), new DictionaryTerm("TownB", "place")]));
        state = await this.UntilAsync(s => s.Waiting == RunWait.Dictionary && s.FreezeRounds == 1);
        Assert.Equal(["messages.Content dictionary-term ×2"], state.Leaks);
        var dictionary = this.protection.ReadLines(Path.Combine(this.environment.CorpusDirectory, "dictionary.jsonl"));
        Assert.Equal(2, dictionary.Count);
        Assert.Contains("PersonA", dictionary[0], StringComparison.Ordinal);

        Assert.Null(this.service.ApproveDictionary([new DictionaryTerm("personA", "person")]));
        state = await this.UntilAsync(s => s.Waiting == RunWait.Passphrase);
        Assert.Equal(2, this.protection.ReadLines(Path.Combine(this.environment.CorpusDirectory, "dictionary.jsonl")).Count);
        Assert.Equal("v1", state.Version);
        Assert.Contains("history-eval holdout --corpus frozen:v1", this.calls);
        Assert.Contains("eval-corpus compare --version v1 --tool memory-eval", this.calls);

        Assert.NotNull(this.service.Export("short"));
        Assert.Null(this.service.Export(Passphrase));
        state = await this.UntilAsync(s => s.Status == RunStatus.Completed);
        var summary = this.service.Report("summary")!;
        Assert.Contains("\"nonInferior\": true", summary, StringComparison.Ordinal);
        Assert.Contains("hybrid-rrf20", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("zq", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, await File.ReadAllTextAsync(Path.Combine(this.environment.RunDirectory, "run.json")), StringComparison.Ordinal);
        Assert.DoesNotContain(this.calls, c => c.Contains("rerank", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_AllItemsGraded_WaitsForTheOwnersDoneAndShowsNoKappaMeanwhile()
    {
        this.service.Start(fresh: false);
        await this.UntilAsync(s => s.Waiting == RunWait.Grading);
        Assert.Contains("Grade every item", this.service.SubmitGrading(), StringComparison.Ordinal);

        this.GradeAll(i => i % 3);
        for (var i = 0; i < 5; i++)
        {
            this.service.Tick();
        }

        var state = this.service.Snapshot()!;
        Assert.Equal((RunStatus.Waiting, EvaluationRunPlan.Grading, RunWait.Grading), (state.Status, state.Phase, state.Waiting));
        Assert.Null(state.Kappa);
        Assert.Null(this.Grading().Progress().Kappa);
        this.Grading().Grade(0, 1);

        Assert.Null(this.service.SubmitGrading());
        Assert.Equal(EvaluationRunPlan.Dictionary, (await this.UntilAsync(s => s.Waiting == RunWait.Dictionary)).Phase);
        Assert.Contains("not waiting for the grading", this.service.SubmitGrading(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_KappaBelowTheGate_LocksTheGradesAndKeepsTheGradingPanelOpenWithGuidance()
    {
        this.service.Start(fresh: false);
        await this.UntilAsync(s => s.Waiting == RunWait.Grading);

        this.GradeAll(i => (i + 1) % 3);
        Assert.Null(this.service.SubmitGrading());
        var state = await this.UntilAsync(s => s.Kappa is not null);

        Assert.Equal((RunStatus.Waiting, EvaluationRunPlan.Grading, RunWait.Grading), (state.Status, state.Phase, state.Waiting));
        Assert.Contains("below 0.6", state.Message, StringComparison.Ordinal);
        Assert.Contains("fresh run", state.Message, StringComparison.Ordinal);
        Assert.Contains("Review grades", state.Message, StringComparison.Ordinal);
        Assert.Contains("Judge calibration", state.Message, StringComparison.Ordinal);
        Assert.True(this.Grading().Progress().Submitted);
        Assert.Throws<InvalidOperationException>(() => this.Grading().Grade(0, 2));
        Assert.DoesNotContain(this.calls, c => c.Contains("ablate", StringComparison.Ordinal));

        // The same locked grades never pass the gate: after cancel + resume the run waits again with the same guidance.
        for (var i = 0; i < 5; i++)
        {
            this.service.Tick();
        }

        Assert.Equal(RunStatus.Waiting, this.service.Snapshot()!.Status);
        Assert.Null(this.service.Cancel());
        Assert.Null(this.service.Resume());
        state = await this.UntilAsync(s => s.Status == RunStatus.Waiting);
        Assert.Equal(RunWait.Grading, state.Waiting);
        Assert.Contains("below 0.6", state.Message, StringComparison.Ordinal);
    }
    [Fact]
    public async Task Run_LabelJobStopsResumable_StaysInTheLabelPhaseWithTheReasonAndResumeRetriesIt()
    {
        this.labelExits.Enqueue(4);
        this.labelExits.Enqueue(5);
        this.labelExits.Enqueue(6);
        this.service.Start(fresh: false);

        var state = await this.UntilAsync(s => s.Status == RunStatus.Stopped);
        Assert.Equal(EvaluationRunPlan.LabelHistory, state.Phase);
        Assert.Contains("provider", state.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Resume", state.Message, StringComparison.Ordinal);

        Assert.Null(this.service.Resume());
        state = await this.UntilAsync(s => s.Status == RunStatus.Stopped);
        Assert.Equal(EvaluationRunPlan.LabelHistory, state.Phase);
        Assert.Contains("judged", state.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Null(this.service.Resume());
        state = await this.UntilAsync(s => s.Status == RunStatus.Stopped);
        Assert.Contains("Too few queries", state.Message, StringComparison.Ordinal);

        Assert.Null(this.service.Resume());
        state = await this.UntilAsync(s => s.Waiting == RunWait.Grading);
        Assert.Equal(4, this.calls.Count(c => c.StartsWith("history-eval label", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Start_FreshAfterALockedSample_ArchivesItBeforeTheReset()
    {
        this.service.Start(fresh: false);
        await this.UntilAsync(s => s.Waiting == RunWait.Grading);
        this.GradeAll(i => (i + 1) % 3);
        Assert.Null(this.service.SubmitGrading());
        var state = await this.UntilAsync(s => s.Kappa is not null);
        var archive = new CalibrationArchive(this.environment.CalibrationDirectory);

        // The κ gate archived the locked sample (passing or failing).
        Assert.True(File.Exists(Path.Combine(archive.SetsDirectory, state.RunId, "history-eval", KappaSampleFile.FileName)));
        Assert.True(File.Exists(Path.Combine(archive.SetsDirectory, state.RunId, "memory-eval", "judge-agreement.csv")));

        // Without that copy, a fresh start archives the previous run's sample before it wipes the workspaces.
        Directory.Delete(this.environment.CalibrationDirectory, recursive: true);
        Assert.Null(this.service.Cancel());
        Assert.Null(this.service.Start(fresh: true));

        Assert.False(File.Exists(Path.Combine(this.environment.HistoryWorkspace, KappaSampleFile.FileName)));
        var archived = archive.Load(this.protection);
        Assert.Equal(5, archived.Count);
        Assert.All(archived, i => Assert.NotNull(i.HumanGrade));
    }

    private Task<bool> PurgeAsync()
    {
        var monitor = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<HistoryOptions>>();
        monitor.CurrentValue.Returns(new HistoryOptions { SmokeArtifactsDirectory = Path.Combine(this.root, "smoke"), EvalArtifactsDirectory = this.environment.HistoryWorkspace });
        var now = DateTimeOffset.UtcNow;
        return new SmokeArtifactErasureStep(monitor, memoryEvalWorkspace: this.environment.MemoryWorkspace, calibrationDirectory: this.environment.CalibrationDirectory)
            .RunAsync(new ErasureTask { Id = "p", CreatedAt = now, RangeFrom = now.AddDays(-1), RangeTo = now, Steps = ["x"] }, CancellationToken.None);
    }

    [Fact]
    public async Task Purge_AfterALockedSampleWasArchived_FreshStartArchivesNothingAndTheCalibrationSetIsEmpty()
    {
        this.service.Start(fresh: false);
        await this.UntilAsync(s => s.Waiting == RunWait.Grading);
        this.GradeAll(i => (i + 1) % 3);
        Assert.Null(this.service.SubmitGrading());
        await this.UntilAsync(s => s.Kappa is not null);
        var archive = new CalibrationArchive(this.environment.CalibrationDirectory);
        Assert.Equal(5, archive.Load(this.protection).Count);

        Assert.True(await this.PurgeAsync());
        Assert.Null(this.service.Cancel());
        Assert.Null(this.service.Start(fresh: true));

        Assert.False(Directory.Exists(archive.SetsDirectory));
        var workspaces = new Dictionary<string, string> { ["history-eval"] = this.environment.HistoryWorkspace, ["memory-eval"] = this.environment.MemoryWorkspace };
        Assert.Empty(archive.CalibrationSet(workspaces, this.protection));
    }

    [Fact]
    public async Task Purge_WhileWaitingForTheGrading_RunKeepsWaitingAndSubmitRefuses()
    {
        this.service.Start(fresh: false);
        await this.UntilAsync(s => s.Waiting == RunWait.Grading);
        this.GradeAll(i => i % 3);

        Assert.True(await this.PurgeAsync());
        for (var i = 0; i < 3; i++)
        {
            this.service.Tick();
        }

        var state = this.service.Snapshot()!;
        Assert.Equal((RunStatus.Waiting, RunWait.Grading), (state.Status, state.Waiting));
        Assert.Equal(0, this.Grading().Progress().Total);
        Assert.Contains("Grade every item", this.service.SubmitGrading(), StringComparison.Ordinal);
        Assert.Null(this.service.Cancel());
        Assert.Null(this.service.Start(fresh: true));
    }

    [Fact]
    public async Task Start_PreviousRegistration_RefusedUnlessFresh()
    {
        Directory.CreateDirectory(this.environment.HistoryWorkspace);
        await File.WriteAllTextAsync(Path.Combine(this.environment.HistoryWorkspace, "preregistration.json"), "{}", CancellationToken.None);

        Assert.Contains("fresh run", this.service.Start(fresh: false), StringComparison.Ordinal);
        Assert.Null(this.service.Start(fresh: true));

        Assert.False(File.Exists(Path.Combine(this.environment.HistoryWorkspace, "preregistration.json")));
        Assert.Contains("in progress", this.service.Start(fresh: true), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_InterruptedJob_RunsTheStepAgain()
    {
        this.initGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.service.Start(fresh: false);
        this.service.Tick();
        Assert.NotNull(this.service.Snapshot()!.JobId);

        // The agent restarts: the job is gone, the persisted run is picked up by a new service.
        this.initGate.SetResult(0);
        await this.jobs.WhenIdleAsync();
        this.service.Dispose();
        this.jobs.Dispose();
        this.initGate = null;
        this.calls.Clear();
        (this.jobs, this.service) = this.Create();

        var state = await this.UntilAsync(s => s.Phase == EvaluationRunPlan.LabelHistory);

        Assert.Equal(["history-eval init", "memory-eval init"], this.calls.Take(2));
        Assert.Null(state.Message);
    }

    [Fact]
    public async Task CancelThenResume_ContinuesTheSameStep()
    {
        this.initGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.service.Start(fresh: false);
        this.service.Tick();

        Assert.Null(this.service.Cancel());
        await this.jobs.WhenIdleAsync();
        Assert.Equal(RunStatus.Cancelled, this.service.Snapshot()!.Status);
        this.initGate = null;

        Assert.Null(this.service.Resume());
        var state = await this.UntilAsync(s => s.Phase == EvaluationRunPlan.LabelHistory);
        Assert.Equal(RunStatus.Running, state.Status);
    }
}
