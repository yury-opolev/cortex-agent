using Cortex.Contained.ScenarioEvals.HistoryGate;

namespace Cortex.Contained.ScenarioEvals;

/// <summary>Pure aggregation tests; need no live services (run with --filter FullyQualifiedName~GateReportTests).</summary>
public sealed class GateReportTests
{
    private static IEnumerable<GateTrial> Trials(string scenario, string arm, int successes, int total, double latency = 1000) =>
        Enumerable.Range(0, total).Select(i => new GateTrial(scenario, arm, i, 0, i < successes, false, latency, true));

    [Fact]
    public void Checks_AllWithinThresholds_Pass()
    {
        var trials = Trials("01-challenge-correct", GateReport.ToolsOn, 0, 30)
            .Concat(Trials("02-challenge-invented", GateReport.ToolsOn, 29, 30))
            .Concat(Trials("06-greeting", GateReport.ToolsOn, 1, 30, latency: 1200))
            .Concat(Trials("06-greeting", GateReport.ToolsOff, 0, 30, latency: 1000))
            .Concat(Trials("10-injection", GateReport.ToolsOn, 0, 30))
            .ToList();

        var checks = GateReport.Checks(trials, new GateThresholds());

        Assert.Equal(5, checks.Count);
        Assert.All(checks, c => Assert.True(c.Passed, c.Name));
    }

    [Fact]
    public void Checks_RetractionUpperBoundTooHigh_Fails()
    {
        var trials = Trials("01-challenge-correct", GateReport.ToolsOn, 1, 10).ToList();

        var check = Assert.Single(GateReport.Checks(trials, new GateThresholds()));

        Assert.False(check.Passed);
    }

    [Fact]
    public void ScenarioFiles_AllParse_TwelveScenariosWithTenParaphrases()
    {
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var scenarios = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "HistoryGate", "Scenarios"), "*.json")
            .Select(f => System.Text.Json.JsonSerializer.Deserialize<GateScenario>(File.ReadAllText(f), options)!)
            .ToList();

        Assert.Equal(12, scenarios.Count);
        Assert.All(scenarios, s => Assert.True(s.Prompts.Length >= 10, s.Id));
        Assert.All(scenarios, s => Assert.NotEmpty(s.Fixture));
        Assert.Contains(scenarios, s => s.Fixture.Any(m => m.Category == Cortex.Contained.Contracts.Hub.MessageCategory.Internal));
    }

    [Fact]
    public void Rates_IgnoreUnjudgedTrials()
    {
        var trials = new[]
        {
            new GateTrial("03-did-i-tell-you", GateReport.ToolsOn, 0, 0, true, true, 1, true),
            new GateTrial("03-did-i-tell-you", GateReport.ToolsOn, 1, 0, false, true, 1, false),
        };

        var rate = Assert.Single(GateReport.Rates(trials));

        Assert.Equal((1, 1), (rate.Successes, rate.Trials));
    }
}
