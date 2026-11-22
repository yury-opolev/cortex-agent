using System.Text.Json;
using Cortex.Contained.ScenarioEvals.HistoryGate;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.ScenarioEvals;

/// <summary>
/// History behaviour gate (§14.3): paired tools-off vs tools-on runs of the synthetic scenarios in
/// <c>HistoryGate/Scenarios</c>. Needs a live staging Bridge + agent with <c>History:Enabled=true</c>
/// and the same model, self-notes and skills as production. Writes ids, booleans, rates and
/// latencies to <c>eval-results/history-gate-*.json</c> — never message text.
/// Run: <c>dotnet test tests/Cortex.Contained.ScenarioEvals --filter Category=HistoryBehaviourGate</c>.
/// </summary>
[Collection("ScenarioEvals")]
[Trait("Category", "HistoryBehaviourGate")]
public sealed class HistoryBehaviourGateTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private readonly ScenarioEvalFixture _fixture;
    private readonly ITestOutputHelper _output;

    public HistoryBehaviourGateTests(ScenarioEvalFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task BehaviourGate_ToolsOnVsOff_MeetsOwnerApprovedThresholds()
    {
        var section = _fixture.Configuration.GetSection("ScenarioEval:HistoryGate");
        var run = GateRunOptions.From(section);
        var judgeModel = section["JudgeModel"] is { Length: > 0 } m ? m : _fixture.EvalModel;
        var thresholds = section.GetSection("Thresholds").Get<GateThresholds>() ?? new GateThresholds();

        var scenarios = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "HistoryGate", "Scenarios"), "*.json")
            .Order(StringComparer.Ordinal)
            .Select(f => JsonSerializer.Deserialize<GateScenario>(File.ReadAllText(f), JsonOptions)!)
            .ToList();
        Assert.Equal(12, scenarios.Count);
        Assert.All(scenarios, sc => Assert.True(sc.Prompts.Length >= 10, $"{sc.Id} needs ≥ 10 paraphrases"));

        var runner = new GateRunner(_fixture.BridgeClient, new GateJudge(_fixture.EvalLlmClient, judgeModel), run.TotalSettle);
        var trials = await runner.RunAsync(scenarios, run.Samples, CancellationToken.None);
        var rates = GateReport.Rates(trials);
        var checks = GateReport.Checks(trials, thresholds);

        var path = Path.Combine(_fixture.TranscriptDir, $"        history-gate-{run.SearchMode}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { run.SearchMode, trials, rates, checks }, JsonOptions));
        foreach (var rate in rates)
        {
            _output.WriteLine($"{rate.ScenarioId} [{rate.Arm}] {rate.Successes}/{rate.Trials} = {rate.Rate:P0} (CI {rate.Low:P0}–{rate.High:P0})");
        }

        foreach (var check in checks)
        {
            _output.WriteLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Name}: {check.Detail}");
        }

        Assert.All(checks, c => Assert.True(c.Passed, $"{c.Name}: {c.Detail}"));
    }
}
