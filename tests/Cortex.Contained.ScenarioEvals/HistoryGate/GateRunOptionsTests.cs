using Cortex.Contained.ScenarioEvals.HistoryGate;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.ScenarioEvals;

/// <summary>Pure option binding for the behaviour gate; needs no live services.</summary>
public sealed class GateRunOptionsTests
{
    private static IConfigurationSection Section(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => "ScenarioEval:HistoryGate:" + v.Key, v => (string?)v.Value))
            .Build()
            .GetSection("ScenarioEval:HistoryGate");

    [Fact]
    public void From_Defaults_KeywordModeWithoutEmbeddingWait()
    {
        var options = GateRunOptions.From(Section());

        Assert.Equal(new GateRunOptions(3, TimeSpan.FromSeconds(5), "keyword", TimeSpan.FromSeconds(20)), options);
        Assert.Equal(TimeSpan.FromSeconds(5), options.TotalSettle);
    }

    [Fact]
    public void From_HybridMode_AddsEmbeddingSettle()
    {
        var options = GateRunOptions.From(Section(("SearchMode", "Hybrid"), ("SettleSeconds", "4"), ("EmbeddingSettleSeconds", "11")));

        Assert.Equal("hybrid", options.SearchMode);
        Assert.Equal(TimeSpan.FromSeconds(15), options.TotalSettle);
    }

    [Fact]
    public void From_UnknownMode_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => GateRunOptions.From(Section(("SearchMode", "semantic"))));
    }
}
