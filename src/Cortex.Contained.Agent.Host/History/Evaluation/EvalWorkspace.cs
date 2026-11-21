using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>Which configuration the held-out run tests against which baseline, and the non-inferiority margin.</summary>
public sealed record HoldoutPlan
{
    /// <summary>The configuration chosen on the dev split.</summary>
    public required string Config { get; init; }

    /// <summary>Non-inferiority margin on nDCG@10 (e.g. 0.02).</summary>
    public double Margin { get; init; } = 0.02;
}

/// <summary>The pre-registered grid (<c>ablation.json</c>).</summary>
public sealed record AblationPlan
{
    /// <summary>Configuration every other one is compared with.</summary>
    public string Baseline { get; init; } = AblationGrid.Baseline;

    /// <summary>Configurations run on the dev split.</summary>
    public IReadOnlyList<AblationConfig> Configs { get; init; } = AblationGrid.Default();

    /// <summary>The single held-out comparison (filled in before <c>register</c>).</summary>
    public HoldoutPlan? Holdout { get; init; }
}

/// <summary>
/// Files of one evaluation workspace (default <c>/app/state/eval/hybrid</c>, never in the repository,
/// D6): the labelled dataset, the analysis plan, the ablation plan, the registration and run outputs.
/// </summary>
public sealed class EvalWorkspace
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public EvalWorkspace(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.Directory = directory;
    }

    /// <summary>Workspace root.</summary>
    public string Directory { get; }

    /// <summary>Labelled queries (<see cref="EvalQuery"/> per line).</summary>
    public string DatasetPath => Path.Combine(this.Directory, "dataset.jsonl");

    /// <summary>Pre-registered analysis plan.</summary>
    public string AnalysisPlanPath => Path.Combine(this.Directory, "analysis-plan.md");

    /// <summary>Pre-registered ablation grid and holdout comparison.</summary>
    public string AblationPlanPath => Path.Combine(this.Directory, "ablation.json");

    /// <summary>Hashes recorded by <c>register</c>.</summary>
    public string PreregistrationPath => Path.Combine(this.Directory, "preregistration.json");

    /// <summary>Run directories.</summary>
    public string RunsDirectory => Path.Combine(this.Directory, "runs");

    /// <summary>Writes indented JSON.</summary>
    public static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));

    /// <summary>Reads <c>ablation.json</c>.</summary>
    public AblationPlan ReadPlan() =>
        File.Exists(this.AblationPlanPath)
            ? JsonSerializer.Deserialize<AblationPlan>(File.ReadAllText(this.AblationPlanPath), Json) ?? new AblationPlan()
            : throw new InvalidOperationException("ablation.json is missing; run 'init' first.");

    /// <summary>Writes <c>ablation.json</c>.</summary>
    public void WritePlan(AblationPlan plan) => File.WriteAllText(this.AblationPlanPath, JsonSerializer.Serialize(plan, Json));

    /// <summary>Records SHA-256 of the dataset, the analysis plan and the ablation plan. Never overwrites.</summary>
    public void Register()
    {
        if (File.Exists(this.PreregistrationPath))
        {
            throw new InvalidOperationException("Already registered. Start a new workspace for a new, separately reported evaluation.");
        }

        if (this.ReadPlan().Holdout is null)
        {
            throw new InvalidOperationException("ablation.json has no holdout section: choose the configuration on the dev split before registering.");
        }

        WriteJson(this.PreregistrationPath, this.CurrentHashes());
    }

    /// <summary>Throws unless the registered hashes match the current files.</summary>
    public void VerifyRegistration()
    {
        if (!File.Exists(this.PreregistrationPath))
        {
            throw new InvalidOperationException("Run 'register' before 'holdout': the analysis plan must be fixed before the first held-out run.");
        }

        var registered = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(this.PreregistrationPath), Json) ?? [];
        var changed = this.CurrentHashes()
            .Where(kv => !registered.TryGetValue(kv.Key, out var hash) || !string.Equals(hash, kv.Value, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();
        if (changed.Count > 0)
        {
            throw new InvalidOperationException($"Pre-registered files changed since 'register': {string.Join(", ", changed)}.");
        }
    }

    private static string Hash(string path) =>
        File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "missing";

    private Dictionary<string, string> CurrentHashes() => new(StringComparer.Ordinal)
    {
        ["dataset.jsonl"] = Hash(this.DatasetPath),
        ["analysis-plan.md"] = Hash(this.AnalysisPlanPath),
        ["ablation.json"] = Hash(this.AblationPlanPath),
    };
}
