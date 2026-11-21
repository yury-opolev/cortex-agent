using Cortex.Contained.Agent.Host.History;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class HistoryOptionsTests
{
    [Fact]
    public void Validate_Defaults_ReturnsNoErrors()
    {
        Assert.Empty(new HistoryOptions().Validate());
    }

    [Fact]
    public void ResolveTimeZone_DefaultIsEmpty_UsesTheMachineLocalZone()
    {
        var options = new HistoryOptions();

        Assert.Equal(string.Empty, options.TimeZone);
        Assert.Equal(TimeZoneInfo.Local.Id, options.ResolveTimeZone().Id);
        Assert.Equal(TimeZoneInfo.Local.Id, options.EffectiveTimeZoneId);
    }

    [Fact]
    public void ResolveTimeZone_ExplicitZone_OverridesTheLocalZone()
    {
        var options = new HistoryOptions { TimeZone = "Asia/Tokyo" };

        Assert.Equal("Asia/Tokyo", options.ResolveTimeZone().Id);
        Assert.Equal("Asia/Tokyo", options.EffectiveTimeZoneId);
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

    [Fact]
    public void Defaults_HybridSettings_MatchSpec()
    {
        var options = new HistoryOptions();

        Assert.False(options.Dense.Enabled);
        Assert.Equal(HistoryKeywordVariant.Plain, options.KeywordVariant);
        Assert.Equal(60, options.Ranking.RrfK);
        Assert.Equal(["embeddings"], options.Dense.AllowedHosts());
    }

    [Fact]
    public void Validate_OutOfRangeHybridValues_ReturnErrors()
    {
        var options = new HistoryOptions();
        options.Dense.ChunkWords = 10;
        options.Ranking.RrfK = 0;
        options.Ranking.MmrLambda = 2;

        var errors = options.Validate();

        Assert.Contains(errors, e => e.Contains("ChunkWords", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("RrfK", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("MmrLambda", StringComparison.Ordinal));
    }

    [Fact]
    public void Bind_HybridSections_FromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["History:KeywordVariant"] = "stemmed",
                ["History:Dense:Enabled"] = "true",
                ["History:Dense:AllowedEndpointHosts"] = "embeddings, gpu-box",
                ["History:Ranking:Fusion"] = "Convex",
            })
            .Build();

        var options = new HistoryOptions();
        configuration.GetSection(HistoryOptions.SectionName).Bind(options);

        Assert.Equal(HistoryKeywordVariant.Stemmed, options.KeywordVariant);
        Assert.True(options.Dense.Enabled);
        Assert.Equal(["embeddings", "gpu-box"], options.Dense.AllowedHosts());
        Assert.Equal(HistoryFusionMethod.Convex, options.Ranking.Fusion);
    }
}
