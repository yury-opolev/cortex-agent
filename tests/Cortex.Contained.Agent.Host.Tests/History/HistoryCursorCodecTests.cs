using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class HistoryCursorCodecTests
{
    private static readonly HistorySearchRequest Request = new() { Query = "oslo", Limit = 8 };

    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        var position = new HistoryCursorPosition(24, 2, "2026-05-04T08:00:00.0000000Z", 17);

        Assert.True(HistoryCursorCodec.TryDecode(HistoryCursorCodec.Encode("abc", position), out var binding, out var decoded));
        Assert.Equal("abc", binding);
        Assert.Equal(position, decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!")]
    [InlineData("e30")]
    public void TryDecode_Garbage_ReturnsFalse(string cursor)
    {
        Assert.False(HistoryCursorCodec.TryDecode(cursor, out _, out _));
    }

    [Fact]
    public void Binding_ChangesWithQueryFiltersGenerationAndCheckpoint()
    {
        var baseline = HistoryCursorCodec.Binding(Request, 1, 10);

        Assert.Equal(baseline, HistoryCursorCodec.Binding(Request, 1, 10));
        Assert.NotEqual(baseline, HistoryCursorCodec.Binding(Request with { Query = "bergen" }, 1, 10));
        Assert.NotEqual(baseline, HistoryCursorCodec.Binding(Request with { IncludeInternal = true }, 1, 10));
        Assert.NotEqual(baseline, HistoryCursorCodec.Binding(Request, 2, 10));
        Assert.NotEqual(baseline, HistoryCursorCodec.Binding(Request, 1, 11));
        Assert.Equal(baseline, HistoryCursorCodec.Binding(Request with { Cursor = "x" }, 1, 10));
    }
}
