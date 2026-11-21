using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Memory.Evaluation;

/// <summary>Which configuration the held-out run tests against the baseline, and the non-inferiority margin.</summary>
public sealed record MemoryHoldoutPlan
{
    /// <summary>The configuration chosen on the dev split.</summary>
    public required string Config { get; init; }

    /// <summary>Non-inferiority margin on nDCG@10 (e.g. 0.02).</summary>
    public double Margin { get; init; } = 0.02;
}

/// <summary>The pre-registered memory grid (<c>ablation.json</c>).</summary>
public sealed record MemoryAblationPlan
{
    /// <summary>Configuration every other one is compared with (today's semantic search).</summary>
    public string Baseline { get; init; } = MemoryAblationGrid.Baseline;

    /// <summary>Configurations run on the dev split.</summary>
    public IReadOnlyList<MemoryAblationConfig> Configs { get; init; } = MemoryAblationGrid.Default();

    /// <summary>The single held-out comparison (filled in before <c>register</c>).</summary>
    public MemoryHoldoutPlan? Holdout { get; init; }
}

/// <summary>
/// Files of one memory evaluation workspace (default <c>/app/state/eval/memory</c>, never in the
/// repository, D6): query seeds, the labelled dataset, judging pools, the analysis plan, the ablation
/// plan, the registration and run outputs (ids and numbers only).
/// </summary>
public sealed class MemoryEvalWorkspace
{
    /// <summary>Default workspace inside the agent container.</summary>
    public const string DefaultDirectory = "/app/state/eval/memory";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public MemoryEvalWorkspace(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.Directory = directory;
    }

    /// <summary>Workspace root.</summary>
    public string Directory { get; }

    /// <summary>Labelled queries (<see cref="MemoryEvalQuery"/> per line).</summary>
    public string DatasetPath => Path.Combine(this.Directory, "dataset.jsonl");

    /// <summary>Query-authoring seeds (raw memory text; delete after authoring).</summary>
    public string SeedsPath => Path.Combine(this.Directory, "seeds.jsonl");

    /// <summary>Unjudged memory ids per query, for the judge (ids only).</summary>
    public string PoolPath => Path.Combine(this.Directory, "pool.jsonl");

    /// <summary>How to turn seeds into dataset lines.</summary>
    public string GenerationGuidePath => Path.Combine(this.Directory, "query-generation.md");

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
    public MemoryAblationPlan ReadPlan() =>
        File.Exists(this.AblationPlanPath)
            ? JsonSerializer.Deserialize<MemoryAblationPlan>(File.ReadAllText(this.AblationPlanPath), Json) ?? new MemoryAblationPlan()
            : throw new InvalidOperationException("ablation.json is missing; run 'init' first.");

    /// <summary>Writes <c>ablation.json</c>.</summary>
    public void WritePlan(MemoryAblationPlan plan) => File.WriteAllText(this.AblationPlanPath, JsonSerializer.Serialize(plan, Json));

    /// <summary>
    /// Records SHA-256 of the dataset, the analysis plan and the ablation plan. Requires a holdout choice and
    /// a dataset of at least <see cref="MemoryEvalDataset.MinimumQueries"/> valid queries. Never overwrites.
    /// </summary>
    public void Register()
    {
        var registration = this.Registration();
        if (!registration.IsRegistered)
        {
            if (this.ReadPlan().Holdout is null)
            {
                throw new InvalidOperationException("ablation.json has no holdout section: choose the configuration on the dev split before registering.");
            }

            var errors = MemoryEvalDataset.ValidateForRegistration(MemoryEvalDataset.Read(this.DatasetPath));
            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"dataset.jsonl is not ready for registration ({errors.Count} problems); run 'validate'.");
            }
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
