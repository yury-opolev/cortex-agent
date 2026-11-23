using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Evaluation.Orchestration;

/// <summary>The hub surface of the evaluation run: maps state to DTOs and commands to <see cref="EvaluationRunService"/>.</summary>
public sealed class EvaluationRunApi(EvaluationRunService runs, GradingService grading, Func<Labelling.LabellingOptions> options)
{
    /// <summary>Steps shown in the run's log.</summary>
    public const int LogEntries = 30;

    /// <summary>The run as the Recall page shows it.</summary>
    public EvaluationRunDto Run()
    {
        var state = runs.Snapshot();
        var labelling = options();
        if (state is null)
        {
            return new EvaluationRunDto { Exists = false, QueryModel = labelling.QueryModel, JudgeModel = labelling.JudgeModel };
        }

        GradingProgressDto? progress = null;
        try
        {
            progress = Progress(grading.Progress());
        }
        catch (Storage.StorageLockedException)
        {
            // The sample is sealed and the key has not arrived: progress is shown once it has.
        }

        var current = Array.IndexOf(EvaluationRunPlan.Phases.ToArray(), state.Phase);
        var jobs = state.JobId is null ? null : EvaluationRunPlan.Jobs(state);
        return new EvaluationRunDto
        {
            Exists = true,
            RunId = state.RunId,
            Status = state.Status,
            Phase = state.Phase,
            Waiting = state.Waiting,
            Message = state.Message,
            StartedAt = state.StartedAt,
            UpdatedAt = state.UpdatedAt,
            FinishedAt = state.FinishedAt,
            CurrentJob = jobs is not null && state.Step < jobs.Count ? jobs[state.Step].Tool + " " + jobs[state.Step].Command : null,
            Phases = EvaluationRunPlan.Phases
                .Where(p => p != EvaluationRunPlan.Done)
                .Select((p, i) => new EvaluationRunPhaseDto(p, PhaseState(state, p, i, current)))
                .ToList(),
            Grading = progress,
            Kappa = state.Kappa,
            HistoryKappa = state.HistoryKappa,
            MemoryKappa = state.MemoryKappa,
            Selected = state.Selected,
            RerankConfigs = state.RerankConfigs,
            Version = state.Version,
            FreezeRounds = state.FreezeRounds,
            Leaks = state.Leaks,
            Reports = state.Reports.Keys.Order(StringComparer.Ordinal).ToList(),
            Log = state.Log.TakeLast(LogEntries).Select(e => new EvaluationRunLogDto(e.At, e.Phase, e.Step, e.ExitCode, e.Note)).ToList(),
            QueryModel = labelling.QueryModel,
            JudgeModel = labelling.JudgeModel,
        };
    }

    /// <summary>Carries out <paramref name="command"/>; the result holds the run afterwards.</summary>
    public EvaluationRunCommandResultDto Control(EvaluationRunCommandDto command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var error = command.Action switch
        {
            EvaluationRunActions.Start => runs.Start(command.Fresh),
            EvaluationRunActions.Cancel => runs.Cancel(),
            EvaluationRunActions.Resume => runs.Resume(),
            EvaluationRunActions.ApproveDictionary => runs.ApproveDictionary((command.Terms ?? []).Select(t => new DictionaryTerm(t.Term, t.Kind, t.Entity)).ToList()),
            EvaluationRunActions.Export => runs.Export(command.Passphrase ?? string.Empty),
            _ => $"Unknown action '{command.Action}'.",
        };
        if (error is null && command.Action != EvaluationRunActions.Cancel)
        {
            runs.Tick();
        }

        return new EvaluationRunCommandResultDto { Ok = error is null, Error = error, Run = this.Run() };
    }

    /// <summary>Item <paramref name="index"/> of the κ sample.</summary>
    public GradingItemDto Item(int index)
    {
        var item = grading.Item(index);
        var progress = Progress(grading.Progress());
        return item is null
            ? new GradingItemDto { Exists = false, Index = index, Total = progress.Total, Progress = progress }
            : new GradingItemDto
            {
                Exists = true,
                Index = item.Index,
                Total = item.Total,
                Tool = item.Tool,
                Query = item.Query,
                Candidate = item.Candidate,
                HumanGrade = item.HumanGrade,
                Progress = progress,
            };
    }

    /// <summary>Stores a grade; the run continues on its next tick once every item is graded.</summary>
    public GradingProgressDto Grade(GradeRequestDto grade)
    {
        ArgumentNullException.ThrowIfNull(grade);
        return Progress(grading.Grade(grade.Index, grade.Grade));
    }

    /// <summary>A recorded report, or null.</summary>
    public string? Report(string key) => runs.Report(key);

    private static GradingProgressDto Progress(GradingProgress progress) => new()
    {
        Total = progress.Total,
        Graded = progress.Graded,
        Complete = progress.Complete,
        Kappa = progress.Kappa,
        HistoryKappa = progress.HistoryKappa,
        MemoryKappa = progress.MemoryKappa,
    };

    private static string PhaseState(EvaluationRunState state, string phase, int index, int current)
    {
        var skipped = phase switch
        {
            EvaluationRunPlan.RerankCurve or EvaluationRunPlan.SelectRerank => index < current && !state.Reranker,
            EvaluationRunPlan.GateR => index < current && state.RerankConfigs.Count == 0,
            _ => false,
        };
        return skipped ? "skipped"
            : state.Status == RunStatus.Completed || index < current ? "done"
            : index == current ? "current"
            : "pending";
    }
}
