using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class HistoryOptionsTests
{
    [Fact]
    public void Validate_Defaults_ReturnsNoErrors()
    {
        Assert.Empty(new HistoryOptions().Validate());
    }

    [Fact]
    public void Validate_UnknownTimeZone_ReturnsError()
    {
        var errors = new HistoryOptions { TimeZone = "Nowhere/Atlantis" }.Validate();

        Assert.Contains(errors, e => e.Contains("TimeZone", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_OutOfRangeValues_ReturnErrors()
    {
        var errors = new HistoryOptions { IdleGapMinutes = 0, DfCapRatio = 0, SearchTokenCap = 10 }.Validate();

        Assert.Equal(3, errors.Count);
    }
}
