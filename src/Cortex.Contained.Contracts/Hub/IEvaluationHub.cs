namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Evaluation commands (<c>history-eval</c>, <c>memory-eval</c>, <c>eval-corpus</c>) run inside the agent process,
/// where the storage key lives, the owner-authored workspace files and the frozen evaluation corpora (D3, D6).
/// Part of the composed <see cref="IAgentHub"/> surface; the Bridge evaluation endpoints use it.
/// </summary>
public interface IEvaluationHub
{
    /// <summary>Starts a command as a background job (one at a time); a refusal comes back as a job in state <c>refused</c>.</summary>
    Task<EvaluationJobDto> StartEvaluation(EvaluationStartRequestDto request);

    /// <summary>The job (state, exit code, output so far), or null when unknown.</summary>
    Task<EvaluationJobDto?> GetEvaluationJob(string jobId);

    /// <summary>Recent jobs, newest first.</summary>
    Task<IReadOnlyList<EvaluationJobDto>> ListEvaluationJobs();

    /// <summary>Requests cancellation of a running job; false when it is not running.</summary>
    Task<bool> CancelEvaluationJob(string jobId);

    /// <summary>Reads an owner-authored workspace file (decrypted when sealed).</summary>
    Task<EvaluationFileDto> ReadEvaluationFile(EvaluationFileRequestDto request);

    /// <summary>Writes an owner-authored workspace file (sealed when it carries text on an encrypted install).</summary>
    Task<EvaluationFileWriteResultDto> WriteEvaluationFile(EvaluationFileWriteDto request);

    /// <summary>Frozen corpus versions, export packages and off-machine copies awaiting deletion.</summary>
    Task<FrozenCorpusStatusDto> GetFrozenCorpusStatus();

    /// <summary>Up to <paramref name="count"/> bytes of an export package from <paramref name="offset"/> (passphrase-encrypted bytes).</summary>
    Task<EvaluationPackageChunkDto> ReadEvaluationPackage(string name, long offset, int count);

    /// <summary>Records that the owner deleted the off-machine package of <paramref name="version"/> from SafeExchange.</summary>
    Task<bool> AcknowledgeOffsiteDeletion(string version);

    /// <summary>The evaluation run ("Run evaluation" on the Recall page): phases, waits, κ, verdict report keys.</summary>
    Task<EvaluationRunDto> GetEvaluationRun();

    /// <summary>Starts, cancels or resumes the run, approves the dictionary or supplies the export passphrase.</summary>
    Task<EvaluationRunCommandResultDto> ControlEvaluationRun(EvaluationRunCommandDto command);

    /// <summary>Item <paramref name="index"/> of the owner's κ sample (decrypted query and candidate text, without the judge's grade).</summary>
    Task<GradingItemDto> GetGradingItem(int index);

    /// <summary>Stores the owner's grade of one κ sample item; grades can change until the owner submits them (<see cref="EvaluationRunActions.SubmitGrading"/>); no κ is returned before that.</summary>
    Task<GradingProgressDto> SubmitGrade(GradeRequestDto grade);

    /// <summary>A report the run recorded (ids and numbers only), or null.</summary>
    Task<string?> ReadEvaluationRunReport(string key);
}

/// <summary>String values of <see cref="EvaluationJobDto.State"/>.</summary>
public static class EvaluationJobStates
{
    public const string Running = "running";

    public const string Succeeded = "succeeded";

    /// <summary>The command ran and returned a non-zero exit code (or threw).</summary>
    public const string Failed = "failed";

    public const string Cancelled = "cancelled";

    /// <summary>The command was not started (another job runs, storage locked, unknown tool, refused option).</summary>
    public const string Refused = "refused";
}

/// <summary>Body of <see cref="IEvaluationHub.StartEvaluation"/>.</summary>
public sealed record EvaluationStartRequestDto
{
    /// <summary><c>history-eval</c>, <c>memory-eval</c> or <c>eval-corpus</c>.</summary>
    public required string Tool { get; init; }

    /// <summary>The command and its options, as on the command line (e.g. <c>ablate --split dev</c>).</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Package passphrase for <c>eval-corpus export|import</c>; held in memory for the job only, never echoed or logged.</summary>
    public string? Passphrase { get; init; }
}

/// <summary>One evaluation job.</summary>
public sealed record EvaluationJobDto
{
    public required string Id { get; init; }

    public required string Tool { get; init; }

    /// <summary>The command (first argument) only; options are not echoed.</summary>
    public string? Command { get; init; }

    /// <summary>One of <see cref="EvaluationJobStates"/>.</summary>
    public required string State { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>True when the job ran in encrypted-workspace mode.</summary>
    public bool Encrypted { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>What the command printed (ids, numbers and paths only; capped).</summary>
    public string Output { get; init; } = string.Empty;
}

/// <summary>Body of <see cref="IEvaluationHub.ReadEvaluationFile"/>.</summary>
public sealed record EvaluationFileRequestDto
{
    /// <summary><c>history-eval</c>, <c>memory-eval</c> or <c>eval-corpus</c>.</summary>
    public required string Tool { get; init; }

    /// <summary>A workspace file name from the allow-list (e.g. <c>dataset.jsonl</c>).</summary>
    public required string Name { get; init; }
}

/// <summary>A workspace file's content.</summary>
public sealed record EvaluationFileDto
{
    public bool Exists { get; init; }

    /// <summary>True when the file is sealed with the storage key on disk.</summary>
    public bool Sealed { get; init; }

    public string Content { get; init; } = string.Empty;

    public string? Error { get; init; }
}

/// <summary>Body of <see cref="IEvaluationHub.WriteEvaluationFile"/>.</summary>
public sealed record EvaluationFileWriteDto
{
    public required string Tool { get; init; }

    public required string Name { get; init; }

    public required string Content { get; init; }
}

/// <summary>Outcome of <see cref="IEvaluationHub.WriteEvaluationFile"/>.</summary>
public sealed record EvaluationFileWriteResultDto
{
    public bool Written { get; init; }

    /// <summary>True when the content equals what is stored (nothing rewritten, so registration hashes stay valid).</summary>
    public bool Unchanged { get; init; }

    public bool Sealed { get; init; }

    public string? Error { get; init; }
}
