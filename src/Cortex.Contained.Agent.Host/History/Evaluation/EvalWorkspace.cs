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
        var registration = this.Registration();
        if (!registration.IsRegistered && this.ReadPlan().Holdout is null)
        {
            throw new InvalidOperationException("ablation.json has no holdout section: choose the configuration on the dev split before registering.");
        }

        registration.Register();
    }

    /// <summary>Throws unless the registered hashes match the current files.</summary>
    public void VerifyRegistration() => this.Registration().Verify();

    private Preregistration Registration() => new(this.PreregistrationPath, new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["dataset.jsonl"] = this.DatasetPath,
        ["analysis-plan.md"] = this.AnalysisPlanPath,
        ["ablation.json"] = this.AblationPlanPath,
    });
}