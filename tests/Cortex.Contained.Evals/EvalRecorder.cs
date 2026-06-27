using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Evals;

/// <summary>
/// Collects structured eval results across all scenarios in a test run
/// and writes them to a JSON file for historical comparison.
/// <para>
/// Results are written to <c>tests/Cortex.Contained.Evals/eval-results/{timestamp}.json</c>
/// relative to the repository root.
/// </para>
/// </summary>
public sealed class EvalRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly List<EvalScenarioResult> _scenarios = [];
    private readonly object _lock = new();

    /// <summary>Model used for this eval run.</summary>
    public string? Model { get; set; }

    /// <summary>API type (e.g. "anthropic-messages", "openai-completions").</summary>
    public string? Api { get; set; }

    /// <summary>Embedding model used.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Records a completed scenario result.</summary>
    public void RecordScenario(EvalScenarioResult result)
    {
        lock (_lock)
        {
            _scenarios.Add(result);
        }
    }

    /// <summary>
    /// Writes the eval run report to <c>eval-results/{timestamp}.json</c>.
    /// The directory is relative to the eval project source directory (not the build output).
    /// </summary>
    public string WriteReport()
    {
        var report = new EvalRunReport
        {
            Timestamp = DateTimeOffset.UtcNow,
            Model = Model,
            Api = Api,
            EmbeddingModel = EmbeddingModel,
            Scenarios = [.. _scenarios],
            Summary = new EvalSummary
            {
                TotalScenarios = _scenarios.Count,
                Passed = _scenarios.Count(s => s.Passed),
                Failed = _scenarios.Count(s => !s.Passed),
                TotalLlmCalls = _scenarios.Sum(s => s.LlmCalls?.Count ?? 0),
                TotalDurationMs = _scenarios.Sum(s => s.DurationMs),
            },
        };

        // Find the eval-results directory relative to the source project
        var resultsDir = FindEvalResultsDir();
        Directory.CreateDirectory(resultsDir);

        var filename = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) + ".json";
        var filePath = Path.Combine(resultsDir, filename);

        var json = JsonSerializer.Serialize(report, JsonOptions);
        File.WriteAllText(filePath, json);

        return filePath;
    }

    /// <summary>
    /// Locates <c>tests/Cortex.Contained.Evals/eval-results/</c> by walking up from <see cref="AppContext.BaseDirectory"/>
    /// until we find the solution root (contains <c>cortex-contained.sln</c>).
    /// </summary>
    private static string FindEvalResultsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        // Walk up from bin/Debug/net10.0-windows/ to find the repo root
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cortex-contained.sln")))
            {
                return Path.Combine(dir.FullName, "tests", "Cortex.Contained.Evals", "eval-results");
            }
            dir = dir.Parent;
        }

        // Fallback: write next to the test assembly
        return Path.Combine(AppContext.BaseDirectory, "eval-results");
    }
}

// ── Result DTOs ──────────────────────────────────────────────────────────

/// <summary>Top-level eval run report (one per <c>dotnet test</c> invocation).</summary>
public sealed class EvalRunReport
{
    public required DateTimeOffset Timestamp { get; init; }
    public string? Model { get; init; }
    public string? Api { get; init; }
    public string? EmbeddingModel { get; init; }
    public required EvalSummary Summary { get; init; }
    public required List<EvalScenarioResult> Scenarios { get; init; }
}

/// <summary>Aggregated summary across all scenarios.</summary>
public sealed class EvalSummary
{
    public required int TotalScenarios { get; init; }
    public required int Passed { get; init; }
    public required int Failed { get; init; }
    public required int TotalLlmCalls { get; init; }
    public required double TotalDurationMs { get; init; }
}

/// <summary>Result of a single eval scenario.</summary>
public sealed class EvalScenarioResult
{
    /// <summary>Scenario display name (e.g. "Extracts basic user facts from conversation").</summary>
    public required string Name { get; init; }

    /// <summary>Whether all assertions passed.</summary>
    public required bool Passed { get; init; }

    /// <summary>Failure message if any assertion failed.</summary>
    public string? FailureMessage { get; init; }

    /// <summary>Total scenario duration in milliseconds.</summary>
    public required double DurationMs { get; init; }

    /// <summary>Number of memories in the store after the scenario completed.</summary>
    public required int MemoryCount { get; init; }

    /// <summary>Contents of all memories after the scenario completed.</summary>
    public List<string>? FinalMemories { get; init; }

    /// <summary>All LLM calls made during this scenario, in order.</summary>
    public List<LlmCallRecord>? LlmCalls { get; init; }

    /// <summary>Inputs to the extraction pipeline.</summary>
    public List<EvalExtractionInput>? ExtractionInputs { get; init; }
}

/// <summary>A user+assistant message pair fed into the extraction pipeline.</summary>
public sealed class EvalExtractionInput
{
    public required string UserMessage { get; init; }
    public required string AssistantResponse { get; init; }
}
