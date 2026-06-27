using System.Text.Json;
using Cortex.Contained.ScenarioEvals.Model;

namespace Cortex.Contained.ScenarioEvals;

/// <summary>
/// xUnit test entry point. Each scenario JSON file becomes a separate test case.
/// Discovers all Scenarios/*.json files via [MemberData].
/// </summary>
[Collection("ScenarioEvals")]
[Trait("Category", "ScenarioEval")]
public sealed class ScenarioEvalTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ScenarioEvalFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ScenarioEvalTests(ScenarioEvalFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public static IEnumerable<object[]> GetScenarios()
    {
        var scenariosDir = Path.Combine(AppContext.BaseDirectory, "Scenarios");
        if (!Directory.Exists(scenariosDir))
            yield break;

        foreach (var file in Directory.GetFiles(scenariosDir, "*.json"))
        {
            yield return [Path.GetFileName(file)];
        }
    }

    [Theory(DisplayName = "Scenario")]
    [MemberData(nameof(GetScenarios))]
    public async Task RunScenario(string scenarioFile)
    {
        // Load scenario definition
        var path = Path.Combine(AppContext.BaseDirectory, "Scenarios", scenarioFile);
        var json = await File.ReadAllTextAsync(path);
        var scenario = JsonSerializer.Deserialize<ScenarioDefinition>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize scenario: {scenarioFile}");

        _output.WriteLine($"=== Scenario: {scenario.Id} ({scenario.Persona.Name}) ===");
        _output.WriteLine($"  Phases: {scenario.Phases.Length}");
        _output.WriteLine($"  Facts: {scenario.Facts.Sum(f => f.Value.Length)} across {scenario.Facts.Count} categories");

        // Run the scenario
        var orchestrator = _fixture.CreateOrchestrator();
        var result = await orchestrator.RunScenarioAsync(scenario, CancellationToken.None);

        // Report results
        _output.WriteLine($"\n--- Results ---");
        _output.WriteLine($"  Duration: {result.DurationMs / 1000.0:F1}s");
        _output.WriteLine($"  Exchanges: {result.TotalExchanges}");
        _output.WriteLine($"  Final memories: {result.FinalMemoryCount}");

        foreach (var phase in result.Phases)
        {
            _output.WriteLine($"\n  Phase: {phase.PhaseName}");
            _output.WriteLine($"    Exchanges: {phase.Exchanges.Count}");
            _output.WriteLine($"    Memories after: {phase.MemoriesAfter.Count}");

            foreach (var score in phase.Scores)
            {
                _output.WriteLine($"    Score [{score.Dimension}] ({score.Label}): {score.Value:F2}");
                if (score.Details is not null)
                    _output.WriteLine($"      {score.Details}");
            }
        }

        // Apply thresholds for pass/fail
        var thresholds = _fixture.Configuration.GetSection("ScenarioEval:Thresholds");
        var recallThreshold = double.TryParse(thresholds["RecallPrecision"], out var rt) ? rt : 0.5;
        var naturalnessThreshold = double.TryParse(thresholds["Naturalness"], out var nt) ? nt : 3.0;
        var memoryThreshold = double.TryParse(thresholds["MemoryRecall"], out var mt) ? mt : 0.5;

        var allScores = result.Phases.SelectMany(p => p.Scores).ToList();

        foreach (var score in allScores)
        {
            switch (score.Dimension)
            {
                case "recall_precision":
                    Assert.True(score.Value >= recallThreshold,
                        $"Recall precision {score.Value:F2} below threshold {recallThreshold:F2}. {score.Details}");
                    break;

                case "naturalness":
                    Assert.True(score.Value >= naturalnessThreshold,
                        $"Naturalness {score.Value:F1} below threshold {naturalnessThreshold:F1}. {score.Details}");
                    break;

                case "memory_recall":
                    Assert.True(score.Value >= memoryThreshold,
                        $"Memory recall {score.Value:F2} below threshold {memoryThreshold:F2}. {score.Details}");
                    break;

                case "no_hallucination":
                    Assert.True(score.Value >= 1.0,
                        $"Hallucination detected. {score.Details}");
                    break;

                case "task_completed":
                    Assert.True(score.Value >= 1.0,
                        $"Task not completed. {score.Details}");
                    break;
            }
        }

        _output.WriteLine($"\n=== Scenario {scenario.Id}: PASSED ===");
    }
}
