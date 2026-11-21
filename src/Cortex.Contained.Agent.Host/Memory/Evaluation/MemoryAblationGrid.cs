using System.Text.Json;
using MemoryMcp.Core.Configuration;

namespace Cortex.Contained.Agent.Host.Memory.Evaluation;

/// <summary>One memory search configuration in the ablation grid.</summary>
public sealed record MemoryAblationConfig
{
    /// <summary>Unique name used in reports and in the pre-registration.</summary>
    public required string Name { get; init; }

    /// <summary>Dense, keyword or hybrid.</summary>
    public MemorySearchMode Mode { get; init; } = MemorySearchMode.Hybrid;

    /// <summary>Keyword matching variant.</summary>
    public MemoryKeywordVariant KeywordVariant { get; init; } = MemoryKeywordVariant.Plain;

    /// <summary>Qwen3 query instruction on or off.</summary>
    public bool UseQueryInstruction { get; init; }

    /// <summary>Fusion method.</summary>
    public FusionMethod Fusion { get; init; } = FusionMethod.Rrf;

    /// <summary>RRF k.</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>Keyword weight (weighted RRF / convex).</summary>
    public double KeywordWeight { get; init; } = 1;

    /// <summary>Dense weight (weighted RRF / convex).</summary>
    public double DenseWeight { get; init; } = 1;

    /// <summary>Use the model-written query and tags instead of the oracle text and tags.</summary>
    public bool UseModelQuery { get; init; }

    /// <summary>A copy of <paramref name="baseline"/> with this configuration applied.</summary>
    public MemorySearchOptions Apply(MemorySearchOptions baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var options = JsonSerializer.Deserialize<MemorySearchOptions>(JsonSerializer.Serialize(baseline))!;
        options.DefaultMode = this.Mode;
        options.KeywordVariant = this.KeywordVariant;
        options.UseQueryInstruction = this.UseQueryInstruction;
        options.Fusion = this.Fusion;
        options.RrfK = this.RrfK;
        options.KeywordWeight = this.KeywordWeight;
        options.DenseWeight = this.DenseWeight;
        options.QueryTimeoutMs = EvaluationQueryTimeoutMs;
        return options;
    }

    /// <summary>
    /// Query-embedding budget of evaluation runs: effectively unlimited, so a slow embedder never turns a
    /// hybrid configuration into keyword-only (the live 3 s budget is a latency guard, not a quality setting).
    /// </summary>
    public const int EvaluationQueryTimeoutMs = 600_000;
}

/// <summary>The default memory ablation grid, fixed before the first dev run (pre-registered with the plan).</summary>
public static class MemoryAblationGrid
{
    /// <summary>The baseline: today's semantic memory search.</summary>
    public const string Baseline = "dense";

    /// <summary>Dense (± instruction), keyword (plain, stemmed), RRF k ∈ {5, 10, 20, 60}, weighted RRF, convex, stemmed and instruction hybrids, model-written queries.</summary>
    public static IReadOnlyList<MemoryAblationConfig> Default() =>
    [
        new() { Name = Baseline, Mode = MemorySearchMode.Dense },
        new() { Name = "dense-instruction", Mode = MemorySearchMode.Dense, UseQueryInstruction = true },
        new() { Name = "keyword-plain", Mode = MemorySearchMode.Keyword },
        new() { Name = "keyword-stemmed", Mode = MemorySearchMode.Keyword, KeywordVariant = MemoryKeywordVariant.Stemmed },
        new() { Name = "hybrid-rrf5", RrfK = 5 },
        new() { Name = "hybrid-rrf10", RrfK = 10 },
        new() { Name = "hybrid-rrf20", RrfK = 20 },
        new() { Name = "hybrid-rrf60", RrfK = 60 },
        new() { Name = "hybrid-wrrf-dense1.5", Fusion = FusionMethod.WeightedRrf, DenseWeight = 1.5 },
        new() { Name = "hybrid-wrrf-keyword1.5", Fusion = FusionMethod.WeightedRrf, KeywordWeight = 1.5 },
        new() { Name = "hybrid-convex-0.3", Fusion = FusionMethod.Convex, KeywordWeight = 0.3, DenseWeight = 0.7 },
        new() { Name = "hybrid-convex-0.5", Fusion = FusionMethod.Convex },
        new() { Name = "hybrid-convex-0.7", Fusion = FusionMethod.Convex, KeywordWeight = 0.7, DenseWeight = 0.3 },
        new() { Name = "hybrid-stemmed", KeywordVariant = MemoryKeywordVariant.Stemmed },
        new() { Name = "hybrid-instruction", UseQueryInstruction = true },
        new() { Name = "hybrid-model-queries", UseModelQuery = true },
        new() { Name = "dense-model-queries", Mode = MemorySearchMode.Dense, UseModelQuery = true },
    ];
}
