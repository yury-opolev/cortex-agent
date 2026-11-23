using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Cortex.Contained.Agent.Host.Evaluation.Orchestration;

/// <summary>Values of <see cref="EvaluationRunState.Status"/>.</summary>
public static class RunStatus
{
    public const string Running = "running";

    /// <summary>Waiting for the owner (<see cref="EvaluationRunState.Waiting"/>).</summary>
    public const string Waiting = "waiting";

    /// <summary>Stopped by a failed step or a gate; <see cref="EvaluationRunState.Message"/> says why and what to do. Resumable.</summary>
    public const string Stopped = "stopped";

    public const string Cancelled = "cancelled";

    public const string Completed = "completed";
}

/// <summary>What the run waits for.</summary>
public static class RunWait
{
    public const string Grading = "grading";

    public const string Dictionary = "dictionary";

    public const string Passphrase = "passphrase";
}

/// <summary>One evaluation command of the run.</summary>
public sealed record RunJob(string Tool, IReadOnlyList<string> Args)
{
    public string Command => this.Args.Count > 0 ? this.Args[0] : string.Empty;

    public string Label => this.Tool + " " + string.Join(' ', this.Args);
}

/// <summary>One finished step (ids and numbers only).</summary>
public sealed record RunLogEntry(DateTimeOffset At, string Phase, string Step, int? ExitCode, string Note);

/// <summary>
/// The persisted state of the one evaluation run (<c>/app/state/eval/run/run.json</c>, plaintext: phases, ids, numbers
/// and report paths only). Survives restarts: a step that was running when the agent stopped runs again.
/// </summary>
public sealed class EvaluationRunState
{
    public string RunId { get; set; } = string.Empty;

    public string Status { get; set; } = RunStatus.Running;

    public string Phase { get; set; } = EvaluationRunPlan.Phases[0];

    /// <summary>Index of the next job within <see cref="Phase"/>.</summary>
    public int Step { get; set; }

    public string? Waiting { get; set; }

    /// <summary>Guidance when stopped (no text from the data).</summary>
    public string? Message { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>The evaluation job running now, or null.</summary>
    public string? JobId { get; set; }

    public int HistoryKappaItems { get; set; }

    public int MemoryKappaItems { get; set; }

    public double? Kappa { get; set; }

    public double? HistoryKappa { get; set; }

    public double? MemoryKappa { get; set; }

    /// <summary>True when the reranker was on at the planning step (Gate R is part of the run).</summary>
    public bool Reranker { get; set; }

    /// <summary>The holdout configuration chosen on dev, per tool.</summary>
    public Dictionary<string, string> Selected { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The reranked configuration added for Gate R, per tool.</summary>
    public Dictionary<string, string> RerankConfigs { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The frozen version of this run.</summary>
    public string? Version { get; set; }

    public int FreezeRounds { get; set; }

    /// <summary>Leakage findings of the last refused freeze (location, kind, count).</summary>
    public List<string> Leaks { get; set; } = [];

    /// <summary>Report paths in the container by key (<c>history-eval:ablate</c>, <c>memory-eval:holdout</c>, …).</summary>
    public Dictionary<string, string> Reports { get; set; } = new(StringComparer.Ordinal);

    public List<RunLogEntry> Log { get; set; } = [];
}

/// <summary>Reads and writes <see cref="EvaluationRunState"/> atomically.</summary>
public sealed class EvaluationRunStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path;

    public EvaluationRunState? Load() =>
        File.Exists(this.Path) ? JsonSerializer.Deserialize<EvaluationRunState>(File.ReadAllText(this.Path), Json) : null;

    public void Save(EvaluationRunState state)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(this.Path)!);
        var temp = this.Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, this.Path, overwrite: true);
    }
}

/// <summary>
/// The fixed order of an evaluation run and what each phase runs: jobs (evaluation commands), internal steps done by
/// <c>EvaluationRunService</c>, or waits for the owner. Pure: tests drive it without jobs.
/// </summary>
public static partial class EvaluationRunPlan
{
    public const string Prepare = "prepare";
    public const string LabelHistory = "label-history";
    public const string LabelMemory = "label-memory";
    public const string Validate = "validate";
    public const string Guard = "guard";
    public const string Grading = "grading";
    public const string Kappa = "kappa";
    public const string Plan = "plan";
    public const string AblateDev = "ablate-dev";
    public const string Select = "select";
    public const string RerankCurve = "rerank-curve";
    public const string SelectRerank = "select-rerank";
    public const string Register = "register";
    public const string HoldoutRaw = "holdout-raw";
    public const string GateR = "gate-r";
    public const string Suggest = "suggest";
    public const string Dictionary = "dictionary";
    public const string Freeze = "freeze";
    public const string HoldoutFrozen = "holdout-frozen";
    public const string Compare = "compare";
    public const string Passphrase = "passphrase";
    public const string Export = "export";
    public const string Summary = "summary";
    public const string Done = "done";

    /// <summary>Most freeze attempts (each refused one goes back to the dictionary review).</summary>
    public const int MaxFreezeRounds = 5;

    private static readonly string[] Order =
    [
        Prepare, LabelHistory, LabelMemory, Validate, Guard, Grading, Kappa, Plan, AblateDev, Select, RerankCurve, SelectRerank,
        Register, HoldoutRaw, GateR, Suggest, Dictionary, Freeze, HoldoutFrozen, Compare, Passphrase, Export, Summary, Done,
    ];

    private static readonly string[] Tools = ["history-eval", "memory-eval"];

    /// <summary>Every phase in order.</summary>
    public static IReadOnlyList<string> Phases => Order;

    /// <summary>The wait of <paramref name="phase"/>, or null when it is not a wait.</summary>
    public static string? WaitOf(string phase) => phase switch
    {
        Grading => RunWait.Grading,
        Dictionary => RunWait.Dictionary,
        Passphrase => RunWait.Passphrase,
        _ => null,
    };

    /// <summary>True for phases <c>EvaluationRunService</c> carries out itself.</summary>
    public static bool IsInternal(string phase) => phase is Kappa or Plan or Select or SelectRerank or Summary;

    /// <summary>The jobs of <paramref name="state"/>'s phase (empty for internal and wait phases).</summary>
    public static IReadOnlyList<RunJob> Jobs(EvaluationRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var version = state.Version ?? string.Empty;
        return state.Phase switch
        {
            Prepare => Both("init"),
            LabelHistory => [new RunJob(Tools[0], ["label", "--kappa", Number(state.HistoryKappaItems)])],
            LabelMemory => [new RunJob(Tools[1], ["label", "--kappa", Number(state.MemoryKappaItems)])],
            Validate => Both("validate"),
            Guard => Both("guard"),
            AblateDev => Both("ablate", "--split", "dev"),
            RerankCurve => state.Selected.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new RunJob(s.Key, ["rerank-curve", "--condition", "normal", "--base", s.Value])).ToList(),
            Register => Both("register"),
            HoldoutRaw => Both("holdout"),
            GateR => state.RerankConfigs.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new RunJob(s.Key, ["gate-r", "--config", s.Value])).ToList(),
            Suggest => [new RunJob("eval-corpus", ["suggest", "--llm"])],
            Freeze => [new RunJob("eval-corpus", ["freeze"])],
            HoldoutFrozen => Both("holdout", "--corpus", "frozen:" + version),
            Compare => [new RunJob("eval-corpus", ["compare", "--version", version]), new RunJob("eval-corpus", ["compare", "--version", version, "--tool", "memory-eval"])],
            Export => [new RunJob("eval-corpus", ["export", "--version", version])],
            _ => [],
        };
    }

    /// <summary>The phase after <paramref name="state"/>'s, skipping the Gate R phases when they do not apply.</summary>
    public static string Next(EvaluationRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var index = Array.IndexOf(Order, state.Phase);
        while (++index < Order.Length)
        {
            var phase = Order[index];
            var skip = phase switch
            {
                RerankCurve or SelectRerank => !state.Reranker || state.Selected.Count == 0,
                GateR => state.RerankConfigs.Count == 0,
                _ => false,
            };
            if (!skip)
            {
                return phase;
            }
        }

        return Done;
    }

    /// <summary>Records what a successful job printed: report paths, the frozen version.</summary>
    public static void Record(EvaluationRunState state, RunJob job, string output)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(output);
        var key = job.Tool == "eval-corpus" && job.Args.Contains("memory-eval", StringComparer.Ordinal) ? "memory-eval:compare"
            : job.Tool == "eval-corpus" ? "history-eval:" + job.Command
            : job.Tool + ":" + job.Command + (job.Args.Contains("--corpus", StringComparer.Ordinal) ? "-frozen" : string.Empty);
        if (ReportLine().Match(output) is { Success: true } report)
        {
            state.Reports[key] = report.Groups[1].Value.Trim();
        }

        if (job.Command == "freeze" && FrozenLine().Match(output) is { Success: true } frozen)
        {
            state.Version = frozen.Groups[1].Value;
            state.Leaks = [];
        }
    }

    /// <summary>The leakage findings a refused freeze printed (<c>leak: location kind ×count</c>).</summary>
    public static List<string> Leaks(string output) =>
        (output ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("leak: ", StringComparison.Ordinal)).Select(l => l[6..]).ToList();

    private static List<RunJob> Both(params string[] args) => Tools.Select(t => new RunJob(t, args)).ToList();

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?:Report|Curve): (.+)$", RegexOptions.Multiline)]
    private static partial Regex ReportLine();

    [GeneratedRegex(@"Frozen (v\d+):")]
    private static partial Regex FrozenLine();
}
