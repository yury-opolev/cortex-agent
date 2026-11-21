using System.Text.Json;

namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>One system configuration in the ablation grid (§4.2).</summary>
public sealed record AblationConfig
{
    /// <summary>Unique name used in reports and in the pre-registration.</summary>
    public required string Name { get; init; }

    /// <summary>Keyword only or hybrid.</summary>
    public HistorySearchMode Mode { get; init; } = HistorySearchMode.Hybrid;

    /// <summary>Keyword index variant (changes the index: one generation per variant).</summary>
    public HistoryKeywordVariant KeywordVariant { get; init; } = HistoryKeywordVariant.Plain;

    /// <summary>Qwen3 query instruction on or off.</summary>
    public bool UseQueryInstruction { get; init; } = true;

    /// <summary>Fusion method.</summary>
    public HistoryFusionMethod Fusion { get; init; } = HistoryFusionMethod.Rrf;

    /// <summary>RRF k.</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>Keyword weight (weighted RRF / convex).</summary>
    public double KeywordWeight { get; init; } = 1;

    /// <summary>Dense weight (weighted RRF / convex).</summary>
    public double DenseWeight { get; init; } = 1;

    /// <summary>Near-duplicate worker collapse on or off.</summary>
    public bool CollapseWorkerDuplicates { get; init; } = true;

    /// <summary>Recency prior weight (0 = off).</summary>
    public double RecencyWeight { get; init; }

    /// <summary>Use the model-written query and filters instead of the oracle text and gold filters.</summary>
    public bool UseModelQuery { get; init; }

    /// <summary>A copy of <paramref name="baseline"/> with this configuration applied.</summary>
    public HistoryOptions Apply(HistoryOptions baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var options = JsonSerializer.Deserialize<HistoryOptions>(JsonSerializer.Serialize(baseline))!;
        options.KeywordVariant = this.KeywordVariant;
        options.Dense.Enabled = baseline.Dense.Enabled || this.Mode != HistorySearchMode.Keyword;
        options.Dense.UseQueryInstruction = this.UseQueryInstruction;
        options.Ranking.Fusion = this.Fusion;
        options.Ranking.RrfK = this.RrfK;
        options.Ranking.KeywordWeight = this.KeywordWeight;
        options.Ranking.DenseWeight = this.DenseWeight;
        options.Ranking.CollapseWorkerDuplicates = this.CollapseWorkerDuplicates;
        options.Ranking.RecencyWeight = this.RecencyWeight;
        return options;
    }
}

/// <summary>The default ablation grid, fixed before the first dev run (pre-registered with the plan).</summary>
public static class AblationGrid
{
    /// <summary>The baseline every configuration is compared with.</summary>
    public const string Baseline = "keyword-plain";

    /// <summary>Keyword variants, RRF k ∈ {5, 10, 20, 60}, weighted RRF, convex, instruction off, recency on, collapse off, model-written queries.</summary>
    public static IReadOnlyList<AblationConfig> Default() =>
    [
        new() { Name = Baseline, Mode = HistorySearchMode.Keyword },
        new() { Name = "keyword-stemmed", Mode = HistorySearchMode.Keyword, KeywordVariant = HistoryKeywordVariant.Stemmed },
        new() { Name = "keyword-trigram", Mode = HistorySearchMode.Keyword, KeywordVariant = HistoryKeywordVariant.Trigram },
        new() { Name = "hybrid-rrf5", RrfK = 5 },
        new() { Name = "hybrid-rrf10", RrfK = 10 },
        new() { Name = "hybrid-rrf20", RrfK = 20 },
        new() { Name = "hybrid-rrf60", RrfK = 60 },
        new() { Name = "hybrid-wrrf-dense1.5", Fusion = HistoryFusionMethod.WeightedRrf, DenseWeight = 1.5 },
        new() { Name = "hybrid-wrrf-keyword1.5", Fusion = HistoryFusionMethod.WeightedRrf, KeywordWeight = 1.5 },
        new() { Name = "hybrid-convex-0.3", Fusion = HistoryFusionMethod.Convex, KeywordWeight = 0.3, DenseWeight = 0.7 },
        new() { Name = "hybrid-convex-0.5", Fusion = HistoryFusionMethod.Convex },
        new() { Name = "hybrid-convex-0.7", Fusion = HistoryFusionMethod.Convex, KeywordWeight = 0.7, DenseWeight = 0.3 },
        new() { Name = "hybrid-no-instruction", UseQueryInstruction = false },
        new() { Name = "hybrid-stemmed", KeywordVariant = HistoryKeywordVariant.Stemmed },
        new() { Name = "hybrid-recency", RecencyWeight = 0.3 },
        new() { Name = "hybrid-no-collapse", CollapseWorkerDuplicates = false },
        new() { Name = "hybrid-model-queries", UseModelQuery = true },
        new() { Name = "keyword-model-queries", Mode = HistorySearchMode.Keyword, UseModelQuery = true },
    ];
}
