using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cortex.Contained.Agent.Host.Evaluation.Calibration;
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Agent.Host.Evaluation.Labelling;
using Cortex.Contained.Agent.Host.Evaluation.Sanitising;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Evaluation.Orchestration;

/// <summary>Where the run works and what it consults.</summary>
/// <param name="HistoryWorkspace">The history-eval workspace.</param>
/// <param name="MemoryWorkspace">The memory-eval workspace.</param>
/// <param name="EvaluationDirectory">The evaluation root (<c>/app/state/eval</c>): corpus, frozen versions, the run's own files.</param>
/// <param name="Protection">The protection (throws while the storage key is missing).</param>
/// <param name="RerankerEnabled">Whether the Bridge granted reranking (Gate R joins the run).</param>
/// <param name="Options">The labelling options (models, counts, κ sample).</param>
public sealed record RunEnvironment(
    string HistoryWorkspace,
    string MemoryWorkspace,
    string EvaluationDirectory,
    Func<EvalProtection> Protection,
    Func<bool> RerankerEnabled,
    Func<LabellingOptions> Options)
{
    public string RunDirectory => Path.Combine(this.EvaluationDirectory, "run");

    public string CorpusDirectory => Path.Combine(this.EvaluationDirectory, "corpus");

    /// <summary>The calibration root: archived κ samples, rubric variants and calibration reports.</summary>
    public string CalibrationDirectory => Path.Combine(this.EvaluationDirectory, "calibration");

    public string Workspace(string tool) => tool == "memory-eval" ? this.MemoryWorkspace : this.HistoryWorkspace;
}

/// <summary>A dictionary term the owner accepted or added.</summary>
public sealed record DictionaryTerm(string Term, string Kind, string? Entity = null);

/// <summary>
/// "Run evaluation": one persisted state machine (<see cref="EvaluationRunPlan"/>) that runs every evaluation command as
/// a job of <see cref="EvaluationJobService"/>, carries out the internal steps (κ gate, analysis plan, dev-only
/// configuration choice, Gate R cell, summary) and stops at the owner's three inputs: the κ grading, the dictionary
/// approval and the package passphrase. One run at a time; resumable after a restart (an interrupted job runs again);
/// a failed step stops the run with guidance and <see cref="Resume"/> continues it. Logs and state hold ids and numbers only.
/// </summary>
public sealed partial class EvaluationRunService : BackgroundService
{
    /// <summary>Minimum LLM-vs-human agreement (§4.2).</summary>
    public const double MinKappa = 0.6;

    /// <summary>Non-inferiority margin written into <c>holdout.margin</c>.</summary>
    public const double Margin = 0.02;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] WorkspaceFiles =
    [
        "dataset.jsonl", "analysis-plan.md", "ablation.json", "preregistration.json", "judge-agreement.csv", "seeds.jsonl", "pool.jsonl",
        LabellingStateStore.FileName, KappaSampleFile.FileName, Labeller.ReportFileName,
    ];

    private readonly EvaluationJobService jobs;
    private readonly GradingService grading;
    private readonly RunEnvironment environment;
    private readonly EvaluationRunStore store;
    private readonly TimeProvider time;
    private readonly ILogger<EvaluationRunService> logger;
    private readonly Lock sync = new();
    private EvaluationRunState? state;

    public EvaluationRunService(EvaluationJobService jobs, GradingService grading, RunEnvironment environment, TimeProvider time, ILogger<EvaluationRunService> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        this.jobs = jobs;
        this.grading = grading;
        this.environment = environment;
        this.time = time;
        this.logger = logger;
        this.store = new EvaluationRunStore(Path.Combine(environment.RunDirectory, "run.json"));
        this.state = this.store.Load();
    }

    /// <summary>A copy of the run's state, or null when there never was a run.</summary>
    public EvaluationRunState? Snapshot()
    {
        lock (this.sync)
        {
            return this.state is null ? null : JsonSerializer.Deserialize<EvaluationRunState>(JsonSerializer.Serialize(this.state, Json), Json);
        }
    }

    /// <summary>Starts a new run; <paramref name="fresh"/> first deletes a previous evaluation's workspace files (not frozen versions or the dictionary).</summary>
    public string? Start(bool fresh)
    {
        lock (this.sync)
        {
            if (this.state is { Status: RunStatus.Running or RunStatus.Waiting })
            {
                return "An evaluation run is in progress; cancel it first.";
            }

            if (this.environment.Options().Validate() is { Count: > 0 } errors)
            {
                return string.Join(" ", errors);
            }

            var previous = GradingService.Tools
                .Any(t => File.Exists(Path.Combine(this.environment.Workspace(t), "dataset.jsonl")) || File.Exists(Path.Combine(this.environment.Workspace(t), "preregistration.json")));
            if (previous && !fresh)
            {
                return "A previous evaluation's dataset or registration exists. Start a fresh run to delete them (frozen versions and the dictionary are kept).";
            }

            if (fresh)
            {
                // The previous run's locked κ sample stays usable for judge calibration.
                if (this.state is { } prior)
                {
                    this.ArchiveCalibration(prior.RunId);
                }

                this.ResetWorkspaces();
            }

            var options = this.environment.Options();
            var history = (int)Math.Round(options.KappaSample * options.HistoryQueries / (double)(options.HistoryQueries + options.MemoryQueries), MidpointRounding.AwayFromZero);
            var now = this.time.GetUtcNow();
            this.state = new EvaluationRunState
            {
                RunId = Guid.NewGuid().ToString("N")[..12],
                StartedAt = now,
                HistoryKappaItems = history,
                MemoryKappaItems = options.KappaSample - history,
            };
            this.Save(this.state);
            this.LogStarted(this.state.RunId, fresh);
            return null;
        }
    }

    /// <summary>Cancels the run (and its running job).</summary>
    public string? Cancel()
    {
        lock (this.sync)
        {
            if (this.state is not { Status: RunStatus.Running or RunStatus.Waiting or RunStatus.Stopped } run)
            {
                return "No evaluation run to cancel.";
            }

            if (run.JobId is { } job)
            {
                this.jobs.Cancel(job);
            }

            run.Status = RunStatus.Cancelled;
            run.Waiting = null;
            run.JobId = null;
            this.Save(run);
            return null;
        }
    }

    /// <summary>Continues a stopped or cancelled run from the step it stopped at.</summary>
    public string? Resume()
    {
        lock (this.sync)
        {
            if (this.state is not { Status: RunStatus.Stopped or RunStatus.Cancelled } run)
            {
                return "Only a stopped or cancelled run can be resumed.";
            }

            run.Status = RunStatus.Running;
            run.Message = null;
            run.JobId = null;
            if (run.Phase == EvaluationRunPlan.Export)
            {
                run.Phase = EvaluationRunPlan.Passphrase;
                run.Step = 0;
            }

            this.Save(run);
            return null;
        }
    }

    /// <summary>
    /// The owner's "done" with the κ grading: computes κ, locks the grades and moves on to the κ gate. Until then the
    /// grades can change and no κ is shown.
    /// </summary>
    public string? SubmitGrading()
    {
        lock (this.sync)
        {
            if (this.state is not { Waiting: RunWait.Grading } run)
            {
                return "The run is not waiting for the grading.";
            }

            if (run.KappaRejected)
            {
                return "The grades are locked and κ is below the gate: cancel the run and start a fresh run.";
            }

            try
            {
                this.grading.Submit();
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }

            MoveTo(run, EvaluationRunPlan.Kappa);
            this.Save(run);
            return null;
        }
    }

    /// <summary>Adds the owner's accepted terms to <c>dictionary.jsonl</c> (sealed) and continues with the freeze.</summary>
    public string? ApproveDictionary(IReadOnlyList<DictionaryTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        lock (this.sync)
        {
            if (this.state is not { Waiting: RunWait.Dictionary } run)
            {
                return "The run is not waiting for the dictionary.";
            }

            if (terms.FirstOrDefault(t => string.IsNullOrWhiteSpace(t.Term) || !SanitiserLexicon.Kinds.Contains(t.Kind, StringComparer.Ordinal)) is { } bad)
            {
                return $"Term kinds are {string.Join(", ", SanitiserLexicon.Kinds)}; '{bad.Kind}' is not one of them.";
            }

            var protection = this.environment.Protection();
            var path = Path.Combine(this.environment.CorpusDirectory, "dictionary.jsonl");
            var lines = File.Exists(path) ? protection.ReadLines(path).ToList() : [];
            var known = SanitiserLexicon.Parse(lines).Entries.Select(e => SanitiserLexicon.Canonical(e.Term)).ToHashSet(StringComparer.Ordinal);
            foreach (var term in terms.Where(t => known.Add(SanitiserLexicon.Canonical(t.Term))))
            {
                lines.Add(JsonSerializer.Serialize(new LexiconEntry { Term = term.Term.Trim(), Kind = term.Kind, Entity = term.Entity }, JsonSerializerOptions.Web));
            }

            Directory.CreateDirectory(this.environment.CorpusDirectory);
            protection.WriteLines(path, lines);
            run.Log.Add(new RunLogEntry(this.time.GetUtcNow(), run.Phase, "dictionary approved", null, string.Create(CultureInfo.InvariantCulture, $"{terms.Count} terms accepted")));
            MoveTo(run, EvaluationRunPlan.Freeze);
            this.Save(run);
            return null;
        }
    }

    /// <summary>Starts the export with the owner's package passphrase (held for the job only, never stored).</summary>
    public string? Export(string passphrase)
    {
        lock (this.sync)
        {
            if (this.state is not { Waiting: RunWait.Passphrase } run)
            {
                return "The run is not waiting for the package passphrase.";
            }

            if (passphrase is not { Length: >= PassphraseKdf.MinimumPassphraseLength })
            {
                return $"The passphrase needs at least {PassphraseKdf.MinimumPassphraseLength} characters.";
            }

            MoveTo(run, EvaluationRunPlan.Export);
            var job = EvaluationRunPlan.Jobs(run)[0];
            var started = this.jobs.Start(new EvaluationStartRequestDto { Tool = job.Tool, Args = job.Args, Passphrase = passphrase });
            if (started.State == EvaluationJobStates.Refused)
            {
                MoveTo(run, EvaluationRunPlan.Passphrase);
                run.Status = RunStatus.Waiting;
                run.Waiting = RunWait.Passphrase;
                this.Save(run);
                return started.Output;
            }

            run.JobId = started.Id;
            this.Save(run);
            return null;
        }
    }

    /// <summary>A report the run recorded (Markdown or JSON, ids and numbers only), or null.</summary>
    public string? Report(string key)
    {
        lock (this.sync)
        {
            return this.state?.Reports.TryGetValue(key, out var path) == true && File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }

    /// <summary>One step of progress: checks the running job, a wait, or starts the next step.</summary>
    public void Tick()
    {
        lock (this.sync)
        {
            if (this.state is not { Status: RunStatus.Running or RunStatus.Waiting } run)
            {
                return;
            }

            var before = JsonSerializer.Serialize(run, Json);
            try
            {
                this.Advance(run);
            }
            catch (StorageLockedException)
            {
                // The key has not arrived yet (agent restart): try again on the next tick.
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or InvalidDataException)
            {
                this.Stop(run, ex.Message);
            }

            if (!string.Equals(before, JsonSerializer.Serialize(run, Json), StringComparison.Ordinal))
            {
                this.Save(run);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            this.Tick();
            await Task.Delay(TickInterval, this.time, stoppingToken).ConfigureAwait(false);
        }
    }

    private void Advance(EvaluationRunState run)
    {
        if (run.JobId is { } jobId)
        {
            var job = this.jobs.Get(jobId);
            if (job is { State: EvaluationJobStates.Running })
            {
                return;
            }

            run.JobId = null;
            if (job is not null)
            {
                this.Finished(run, job);
            }

            return;
        }

        if (run.Phase == EvaluationRunPlan.Done)
        {
            run.Status = RunStatus.Completed;
            run.FinishedAt ??= this.time.GetUtcNow();
            return;
        }

        if (EvaluationRunPlan.WaitOf(run.Phase) is { } wait)
        {
            if (wait == RunWait.Grading && !run.KappaRejected && this.grading.Progress().Submitted)
            {
                MoveTo(run, EvaluationRunPlan.Next(run));
                return;
            }

            run.Status = RunStatus.Waiting;
            run.Waiting = wait;
            if (wait == RunWait.Grading && run.KappaRejected)
            {
                run.Message = KappaGuidance(run.Kappa);
            }

            return;
        }

        if (EvaluationRunPlan.IsInternal(run.Phase))
        {
            this.Internal(run);
            return;
        }

        var jobs = EvaluationRunPlan.Jobs(run);
        if (run.Step >= jobs.Count)
        {
            MoveTo(run, EvaluationRunPlan.Next(run));
            return;
        }

        if (run.Phase == EvaluationRunPlan.Export)
        {
            // The passphrase is never stored: after a restart the owner enters it again.
            MoveTo(run, EvaluationRunPlan.Passphrase);
            return;
        }

        var next = jobs[run.Step];
        if (run.Phase == EvaluationRunPlan.Register && File.Exists(Path.Combine(this.environment.Workspace(next.Tool), "preregistration.json")))
        {
            run.Log.Add(new RunLogEntry(this.time.GetUtcNow(), run.Phase, next.Label, 0, "already registered"));
            run.Step++;
            return;
        }

        var started = this.jobs.Start(new EvaluationStartRequestDto { Tool = next.Tool, Args = next.Args });
        if (started.State == EvaluationJobStates.Refused)
        {
            // Another job (a manual one) or a missing key: wait. Anything else stops the run.
            if (!started.Output.Contains("Another evaluation job", StringComparison.Ordinal) && !started.Output.Contains("storage key", StringComparison.Ordinal))
            {
                this.Stop(run, $"{next.Label} was refused: {started.Output}");
            }

            return;
        }

        run.JobId = started.Id;
    }

    private void Finished(EvaluationRunState run, EvaluationJobDto job)
    {
        var step = EvaluationRunPlan.Jobs(run)[run.Step];
        var tail = Tail(job.Output);
        run.Log.Add(new RunLogEntry(this.time.GetUtcNow(), run.Phase, step.Label, job.ExitCode, tail));
        this.LogStep(run.RunId, run.Phase, step.Tool, step.Command, job.ExitCode ?? -1);
        if (job.State == EvaluationJobStates.Succeeded)
        {
            EvaluationRunPlan.Record(run, step, job.Output);
            this.RecordRunDirectory(run, step);
            run.Step++;
            return;
        }

        if (job.State == EvaluationJobStates.Cancelled)
        {
            run.Status = RunStatus.Cancelled;
            return;
        }

        if (run.Phase == EvaluationRunPlan.Freeze && job.ExitCode == 1)
        {
            run.Leaks = EvaluationRunPlan.Leaks(job.Output);
            if (++run.FreezeRounds >= EvaluationRunPlan.MaxFreezeRounds)
            {
                this.Stop(run, string.Create(CultureInfo.InvariantCulture,
                    $"The leakage check refused {run.FreezeRounds} freezes. Add the missing spellings to the dictionary (Edit-CortexEvalFile.ps1 -Tool eval-corpus -Name dictionary.jsonl), then Resume."));
                run.Phase = EvaluationRunPlan.Freeze;
                return;
            }

            MoveTo(run, EvaluationRunPlan.Suggest);
            return;
        }

        this.Stop(run, LabelStop(run.Phase, job.ExitCode, tail)
            ?? string.Create(CultureInfo.InvariantCulture, $"{step.Label} failed (exit {job.ExitCode}): {tail}"));
    }

    // Exit codes of the label job that keep its progress and continue with Resume (see Labeller.RunAsync).
    private static string? LabelStop(string phase, int? exitCode, string tail) =>
        phase is not (EvaluationRunPlan.LabelHistory or EvaluationRunPlan.LabelMemory) ? null : exitCode switch
        {
            3 => "The label job reached Evaluation:Labelling:MaxCalls. Raise the cap in the agent configuration and press Resume: it continues where it stopped.",
            4 => "The LLM provider kept failing (rate limit, outage or an unlisted model), so the label job stopped; nothing was marked failed. Check the provider in the Bridge, then press Resume: it continues where it stopped. " + tail,
            5 => "Some queries could not be judged yet. Press Resume to retry them (a query is given up after 3 runs); nothing is written before that. " + tail,
            6 => "Too few queries survived judging for registration. Raise Evaluation:Labelling query counts (or add data) and press Resume: the label job samples more seeds. " + tail,
            _ => null,
        };

    private void Internal(EvaluationRunState run)
    {
        switch (run.Phase)
        {
            case EvaluationRunPlan.Kappa:
                var progress = this.grading.Progress();
                (run.Kappa, run.HistoryKappa, run.MemoryKappa) = (progress.Kappa, progress.HistoryKappa, progress.MemoryKappa);
                if (!progress.Submitted)
                {
                    MoveTo(run, EvaluationRunPlan.Grading);
                    return;
                }

                // Passing or failing, the locked grades join the calibration set.
                this.ArchiveCalibration(run.RunId);
                if (progress.Kappa is not { } kappa || kappa < MinKappa)
                {
                    // The gate failed on locked grades: reopen the grading panel (read-only) with the guidance; only a fresh run helps.
                    run.KappaRejected = true;
                    run.Phase = EvaluationRunPlan.Grading;
                    run.Step = 0;
                    run.Status = RunStatus.Waiting;
                    run.Waiting = RunWait.Grading;
                    run.Message = KappaGuidance(progress.Kappa);
                    this.LogStopped(run.RunId, run.Phase);
                    return;
                }

                break;
            case EvaluationRunPlan.Plan:
                run.Reranker = this.environment.RerankerEnabled();
                this.WritePlans(run);
                break;
            case EvaluationRunPlan.Select:
                this.SelectConfigs(run);
                break;
            case EvaluationRunPlan.SelectRerank:
                this.SelectRerank(run);
                break;
            case EvaluationRunPlan.Summary:
                this.WriteSummary(run);
                break;
        }

        run.Log.Add(new RunLogEntry(this.time.GetUtcNow(), run.Phase, run.Phase, null, "done"));
        MoveTo(run, EvaluationRunPlan.Next(run));
    }

    private void WritePlans(EvaluationRunState run)
    {
        var protection = this.environment.Protection();
        var options = this.environment.Options();
        foreach (var tool in GradingService.Tools)
        {
            var directory = this.environment.Workspace(tool);
            var (categories, strata, ids) = tool == "memory-eval"
                ? Facts(new MemoryEvalWorkspace(directory, protection).ReadDataset().Select(q => (q.Category, q.Stratum, q.Id)))
                : Facts(new EvalWorkspace(directory, protection).ReadDataset().Select(q => (q.Category, q.Stratum, q.Id)));
            var facts = new PlanFacts
            {
                Tool = tool,
                Date = this.time.GetUtcNow(),
                Queries = ids.Count,
                Dev = ids.Count(id => EvalSplits.Assign(id) == EvalSplits.Dev),
                Categories = categories,
                Strata = strata,
                QueryModel = options.QueryModel,
                JudgeModel = options.JudgeModel,
                PoolDepth = options.PoolDepth,
                Kappa = run.Kappa ?? 0,
                ToolKappa = tool == "memory-eval" ? run.MemoryKappa : run.HistoryKappa,
                KappaItems = run.HistoryKappaItems + run.MemoryKappaItems,
                Baseline = tool == "memory-eval" ? MemoryAblationGrid.Baseline : AblationGrid.Baseline,
                Margin = Margin,
                Reranker = run.Reranker,
            };
            File.WriteAllText(Path.Combine(directory, "analysis-plan.md"), AnalysisPlanWriter.Render(facts));
        }
    }

    private void SelectConfigs(EvaluationRunState run)
    {
        var protection = this.environment.Protection();
        foreach (var tool in GradingService.Tools)
        {
            var report = ReadReport(run, tool + ":ablate", "results.json");
            var dev = JsonSerializer.Deserialize<AblationReport>(report, Json) ?? throw new InvalidDataException("The dev ablation results are empty.");
            if (tool == "memory-eval")
            {
                var workspace = new MemoryEvalWorkspace(this.environment.MemoryWorkspace, protection);
                var plan = workspace.ReadPlan();
                var chosen = ConfigSelector.Select(dev, plan.Baseline, plan.Configs.Where(c => !c.UseModelQuery && c.RerankBudget() is null).Select(c => c.Name).ToList())
                    ?? throw new InvalidOperationException("No memory configuration qualifies on dev (judged@10 below 0.8 everywhere): deepen the pool (Evaluation:Labelling:PoolDepth) and start a fresh run.");
                workspace.WritePlan(plan with { Holdout = new MemoryHoldoutPlan { Config = chosen, Margin = Margin } });
                run.Selected[tool] = chosen;
            }
            else
            {
                var workspace = new EvalWorkspace(this.environment.HistoryWorkspace, protection);
                var plan = workspace.ReadPlan();
                var chosen = ConfigSelector.Select(dev, plan.Baseline, plan.Configs.Where(c => !c.UseModelQuery && c.RerankBudget() is null).Select(c => c.Name).ToList())
                    ?? throw new InvalidOperationException("No history configuration qualifies on dev (judged@10 below 0.8 everywhere): deepen the pool (Evaluation:Labelling:PoolDepth) and start a fresh run.");
                workspace.WritePlan(plan with { Holdout = new HoldoutPlan { Config = chosen, Margin = Margin } });
                run.Selected[tool] = chosen;
            }
        }
    }

    private void SelectRerank(EvaluationRunState run)
    {
        var protection = this.environment.Protection();
        foreach (var (tool, selected) in run.Selected)
        {
            var curve = JsonSerializer.Deserialize<CurveFile>(ReadReport(run, tool + ":rerank-curve", "curve.json"), Json);
            if (RerankCellSelector.Select(curve?.Rows ?? []) is not { } budget)
            {
                continue;
            }

            var name = selected + "-rerank-" + budget.Label;
            if (tool == "memory-eval")
            {
                var workspace = new MemoryEvalWorkspace(this.environment.MemoryWorkspace, protection);
                var plan = workspace.ReadPlan();
                var config = plan.Configs.Single(c => c.Name == selected) with { Name = name, RerankTopN = budget.TopN, RerankMaxDocChars = budget.MaxDocChars };
                workspace.WritePlan(plan with { Configs = [.. plan.Configs.Where(c => c.Name != name), config] });
            }
            else
            {
                var workspace = new EvalWorkspace(this.environment.HistoryWorkspace, protection);
                var plan = workspace.ReadPlan();
                var config = plan.Configs.Single(c => c.Name == selected) with { Name = name, RerankTopN = budget.TopN, RerankMaxDocChars = budget.MaxDocChars };
                workspace.WritePlan(plan with { Configs = [.. plan.Configs.Where(c => c.Name != name), config] });
            }

            run.RerankConfigs[tool] = name;
        }
    }

    private void WriteSummary(EvaluationRunState run)
    {
        object? Verdict(string key) => run.Reports.TryGetValue(key, out var path) && File.Exists(path)
            ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path))
            : null;
        var summary = new
        {
            run.RunId,
            run.StartedAt,
            finishedAt = this.time.GetUtcNow(),
            run.Version,
            kappa = new { all = run.Kappa, history = run.HistoryKappa, memory = run.MemoryKappa, items = run.HistoryKappaItems + run.MemoryKappaItems },
            selected = run.Selected,
            history = new { raw = Verdict("history-eval:holdout"), sanitised = Verdict("history-eval:holdout-frozen"), gateR = Verdict("history-eval:gate-r") },
            memory = new { raw = Verdict("memory-eval:holdout"), sanitised = Verdict("memory-eval:holdout-frozen"), gateR = Verdict("memory-eval:gate-r") },
            compare = new
            {
                history = run.Reports.GetValueOrDefault("history-eval:compare"),
                memory = run.Reports.GetValueOrDefault("memory-eval:compare"),
            },
        };
        var path = Path.Combine(this.environment.RunDirectory, "summary.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, Json));
        run.Reports["summary"] = path;
    }

    // Holdout and Gate R print no path: their newest run directory holds verdict.json.
    private void RecordRunDirectory(EvaluationRunState run, RunJob step)
    {
        var (prefix, frozen) = step.Command switch
        {
            "holdout" => ("holdout-test-", step.Args.Contains("--corpus", StringComparer.Ordinal)),
            "gate-r" => ("gate-r-", false),
            _ => (null, false),
        };
        if (prefix is null)
        {
            return;
        }

        var workspace = frozen
            ? Path.Combine(new FrozenCorpusStore(this.environment.EvaluationDirectory).DirectoryOf(run.Version!), step.Tool == "memory-eval" ? "memory" : "history")
            : this.environment.Workspace(step.Tool);
        var runs = Path.Combine(workspace, "runs");
        var newest = Directory.Exists(runs)
            ? Directory.EnumerateDirectories(runs, prefix + "*").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (newest is not null)
        {
            run.Reports[step.Tool + ":" + step.Command + (frozen ? "-frozen" : string.Empty)] = Path.Combine(newest, "verdict.json");
        }
    }

    private void ResetWorkspaces()
    {
        foreach (var tool in GradingService.Tools)
        {
            var directory = this.environment.Workspace(tool);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var name in WorkspaceFiles)
            {
                File.Delete(Path.Combine(directory, name));
            }

            if (Directory.Exists(Path.Combine(directory, "runs")))
            {
                Directory.Delete(Path.Combine(directory, "runs"), recursive: true);
            }
        }

        if (Directory.Exists(this.environment.CorpusDirectory))
        {
            File.Delete(Path.Combine(this.environment.CorpusDirectory, "dictionary.suggested.jsonl"));
        }
    }

    private void ArchiveCalibration(string runId)
    {
        try
        {
            var workspaces = GradingService.Tools.ToDictionary(t => t, this.environment.Workspace, StringComparer.Ordinal);
            new CalibrationArchive(this.environment.CalibrationDirectory).Archive(runId, workspaces);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Calibration is a convenience: a failed copy never stops the run.
            this.LogArchiveFailed(runId, ex.GetType().Name);
        }
    }

    private static string KappaGuidance(double? kappa) => string.Create(CultureInfo.InvariantCulture,
        $"LLM-vs-human κ = {kappa ?? 0:F3} is below {MinKappa}: the judge's grades may not be used. Your grades are locked. Press Review grades to see the judge's grade next to yours, then calibrate the judge (docs/evaluation-runbook.md, 'Judge calibration') and start a fresh run.");

    private static void MoveTo(EvaluationRunState run, string phase)
    {
        run.Phase = phase;
        run.Step = 0;
        run.Status = RunStatus.Running;
        run.Waiting = null;
    }

    private void Stop(EvaluationRunState run, string message)
    {
        run.Status = RunStatus.Stopped;
        run.Message = message;
        run.Waiting = null;
        this.LogStopped(run.RunId, run.Phase);
    }

    private void Save(EvaluationRunState run)
    {
        run.UpdatedAt = this.time.GetUtcNow();
        this.store.Save(run);
    }

    private static string ReadReport(EvaluationRunState run, string key, string file) =>
        run.Reports.TryGetValue(key, out var path) && Path.GetDirectoryName(path) is { } directory && File.Exists(Path.Combine(directory, file))
            ? File.ReadAllText(Path.Combine(directory, file))
            : throw new InvalidOperationException($"The run has no {file} for {key}; run that step again (Resume).");

    private static (Dictionary<string, int> Categories, Dictionary<string, int> Strata, List<string> Ids) Facts(IEnumerable<(string Category, string Stratum, string Id)> queries)
    {
        var list = queries.ToList();
        return (
            list.GroupBy(q => q.Category, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            list.GroupBy(q => q.Stratum, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            list.Select(q => q.Id).ToList());
    }

    // The last non-empty lines of a job's output (ids, numbers and paths).
    private static string Tail(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(3);
        var text = string.Join(" | ", lines);
        return text.Length <= 600 ? text : text[^600..];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation run {RunId} started (fresh: {Fresh})")]
    private partial void LogStarted(string runId, bool fresh);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation run {RunId} {Phase}: {Tool} {Command} exited {ExitCode}")]
    private partial void LogStep(string runId, string phase, string tool, string command, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Evaluation run {RunId} stopped at {Phase}")]
    private partial void LogStopped(string runId, string phase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Evaluation run {RunId}: archiving the locked κ sample for calibration failed ({Error})")]
    private partial void LogArchiveFailed(string runId, string error);

    private sealed record CurveFile(IReadOnlyList<RerankCurveRow>? Rows);
}
