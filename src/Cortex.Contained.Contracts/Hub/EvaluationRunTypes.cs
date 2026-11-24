namespace Cortex.Contained.Contracts.Hub;

/// <summary>String values of <see cref="EvaluationRunCommandDto.Action"/>.</summary>
public static class EvaluationRunActions
{
    /// <summary>Starts a run (<see cref="EvaluationRunCommandDto.Fresh"/> deletes a previous evaluation's workspace files first).</summary>
    public const string Start = "start";

    public const string Cancel = "cancel";

    /// <summary>Continues a stopped or cancelled run.</summary>
    public const string Resume = "resume";

    /// <summary>Adds <see cref="EvaluationRunCommandDto.Terms"/> to the dictionary and continues with the freeze.</summary>
    public const string ApproveDictionary = "approve-dictionary";

    /// <summary>Exports the frozen version with <see cref="EvaluationRunCommandDto.Passphrase"/>.</summary>
    public const string Export = "export";

    /// <summary>The owner is done grading: κ is computed and the grades are locked.</summary>
    public const string SubmitGrading = "submit-grading";
}

/// <summary>Body of <see cref="IEvaluationHub.ControlEvaluationRun"/>.</summary>
public sealed record EvaluationRunCommandDto
{
    /// <summary>One of <see cref="EvaluationRunActions"/>.</summary>
    public required string Action { get; init; }

    public bool Fresh { get; init; }

    public IReadOnlyList<DictionaryTermDto>? Terms { get; init; }

    /// <summary>Package passphrase for <see cref="EvaluationRunActions.Export"/>; held for the job only, never stored or echoed.</summary>
    public string? Passphrase { get; init; }
}

/// <summary>A dictionary term the owner accepted or added (person, place, org, handle, other, public-host).</summary>
public sealed record DictionaryTermDto(string Term, string Kind, string? Entity = null);

/// <summary>Outcome of <see cref="IEvaluationHub.ControlEvaluationRun"/>.</summary>
public sealed record EvaluationRunCommandResultDto
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    public required EvaluationRunDto Run { get; init; }
}

/// <summary>One phase of the run as the Recall page lists it.</summary>
/// <param name="Name">Phase name (e.g. <c>label-history</c>).</param>
/// <param name="State"><c>done</c>, <c>current</c>, <c>pending</c> or <c>skipped</c>.</param>
public sealed record EvaluationRunPhaseDto(string Name, string State);

/// <summary>One finished step of the run (ids and numbers only).</summary>
public sealed record EvaluationRunLogDto(DateTimeOffset At, string Phase, string Step, int? ExitCode, string Note);

/// <summary>The evaluation run (ids, numbers, phases and report keys only).</summary>
public sealed record EvaluationRunDto
{
    /// <summary>False when no run was ever started.</summary>
    public bool Exists { get; init; }

    public string? RunId { get; init; }

    /// <summary><c>running</c>, <c>waiting</c>, <c>stopped</c>, <c>cancelled</c> or <c>completed</c>.</summary>
    public string? Status { get; init; }

    public string? Phase { get; init; }

    /// <summary><c>grading</c>, <c>dictionary</c> or <c>passphrase</c> while the run waits for the owner.</summary>
    public string? Waiting { get; init; }

    /// <summary>Why the run stopped and what to do.</summary>
    public string? Message { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>The command running now (tool and command), or null.</summary>
    public string? CurrentJob { get; init; }

    public IReadOnlyList<EvaluationRunPhaseDto> Phases { get; init; } = [];

    public GradingProgressDto? Grading { get; init; }

    public double? Kappa { get; init; }

    public double? HistoryKappa { get; init; }

    public double? MemoryKappa { get; init; }

    /// <summary>Holdout configuration chosen on dev, per tool.</summary>
    public IReadOnlyDictionary<string, string> Selected { get; init; } = new Dictionary<string, string>();

    /// <summary>Reranked configuration of Gate R, per tool.</summary>
    public IReadOnlyDictionary<string, string> RerankConfigs { get; init; } = new Dictionary<string, string>();

    public string? Version { get; init; }

    public int FreezeRounds { get; init; }

    /// <summary>Leakage findings of the last refused freeze (location, kind, count).</summary>
    public IReadOnlyList<string> Leaks { get; init; } = [];

    /// <summary>Keys of the reports the run recorded (read with <see cref="IEvaluationHub.ReadEvaluationRunReport"/>).</summary>
    public IReadOnlyList<string> Reports { get; init; } = [];

    /// <summary>The latest steps, newest last.</summary>
    public IReadOnlyList<EvaluationRunLogDto> Log { get; init; } = [];

    public string? QueryModel { get; init; }

    public string? JudgeModel { get; init; }
}

/// <summary>Progress of the owner's hand grading.</summary>
public sealed record GradingProgressDto
{
    public int Total { get; init; }

    public int Graded { get; init; }

    public bool Complete { get; init; }

    /// <summary>True once the owner submitted the grades: they are locked and κ is shown.</summary>
    public bool Submitted { get; init; }

    public double? Kappa { get; init; }

    public double? HistoryKappa { get; init; }

    public double? MemoryKappa { get; init; }

    /// <summary>0-based indices of the items where the owner and the judge differ (indices only), or null until submitted.</summary>
    public IReadOnlyList<int>? Disagreements { get; init; }
}

/// <summary>One item of the κ sample (decrypted text: shown once, never cached or logged).</summary>
public sealed record GradingItemDto
{
    public bool Exists { get; init; }

    public int Index { get; init; }

    public int Total { get; init; }

    public string Tool { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    public string Candidate { get; init; } = string.Empty;

    public int? HumanGrade { get; init; }

    /// <summary>The judge's grade, shown only once the owner submitted (locked) the grades; null before.</summary>
    public int? LlmGrade { get; init; }

    public required GradingProgressDto Progress { get; init; }
}

/// <summary>Body of <see cref="IEvaluationHub.SubmitGrade"/>: a grade 0–2, or null to clear it.</summary>
public sealed record GradeRequestDto(int Index, int? Grade);
