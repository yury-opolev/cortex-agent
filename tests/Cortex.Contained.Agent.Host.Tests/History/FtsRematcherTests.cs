using Cortex.Contained.Agent.Host.History;
using static Cortex.Contained.Agent.Host.Tests.History.HistoryRows;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class FtsRematcherTests
{
    [Fact]
    public void Match_UsesFoldingPrefixAndBooleanOperators()
    {
        var rows = new[]
        {
            Row(1, Day, "Søren took the night train"),
            Row(2, Day, "поездом в Осло"),
            Row(3, Day, "night bus", role: "assistant", toolCalls: ToolCalls("memory_search")),
        };

        Assert.Equal([1L], FtsRematcher.Match("\"soren\"", rows));
        Assert.Equal([2L], FtsRematcher.Match("\"поезд\"*", rows));
        Assert.Equal([1L], FtsRematcher.Match("\"night\" AND \"train\"", rows));
        Assert.Equal([1L, 3L], FtsRematcher.Match("\"night\" OR \"bergen\"", rows).Order());
        Assert.Equal([3L], FtsRematcher.Match("\"memory\"", rows));
    }

    [Fact]
    public void Match_NoRows_ReturnsEmpty()
    {
        Assert.Empty(FtsRematcher.Match("\"x\"", []));
    }
}
