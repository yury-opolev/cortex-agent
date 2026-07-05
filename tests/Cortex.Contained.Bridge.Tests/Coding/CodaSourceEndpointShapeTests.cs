using System.Text.Json;
using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

/// <summary>
/// Shape/behavior tests for the coda-source REST endpoint's data: the <see cref="CodaSourceState"/>
/// DTO serializes with the camelCase named props the web UI expects, and <see cref="CodingCodaSourceEndpoints.ParseSource"/>
/// accepts the three sources case-insensitively and rejects garbage. The version probe is best-effort
/// I/O and is deliberately not exercised here.
/// </summary>
public sealed class CodaSourceEndpointShapeTests
{
    private static readonly JsonSerializerOptions camelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void CodaSourceState_SerializesCamelCaseNamedProps()
    {
        var state = new CodaSourceState("host", "coda", "Coda v0.1.55", false);
        var json = JsonSerializer.Serialize(state, camelCase);

        Assert.Contains("\"source\"", json, StringComparison.Ordinal);
        Assert.Contains("\"resolvedPath\"", json, StringComparison.Ordinal);
        Assert.Contains("\"version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"bundlePresent\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Item1", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, CodaSource.Auto)]
    [InlineData("", CodaSource.Auto)]
    [InlineData("auto", CodaSource.Auto)]
    [InlineData("AUTO", CodaSource.Auto)]
    [InlineData("host", CodaSource.Host)]
    [InlineData(" Host ", CodaSource.Host)]
    [InlineData("bundled", CodaSource.Bundled)]
    public void ParseSource_AcceptsKnownValues_CaseInsensitive(string? input, CodaSource expected)
    {
        var (ok, source, error) = CodingCodaSourceEndpoints.ParseSource(input);

        Assert.True(ok);
        Assert.Equal(expected, source);
        Assert.Null(error);
    }

    [Fact]
    public void ParseSource_RejectsUnknownValue()
    {
        var (ok, _, error) = CodingCodaSourceEndpoints.ParseSource("nonsense");

        Assert.False(ok);
        Assert.NotNull(error);
    }
}
