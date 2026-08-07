using Cortex.Contained.Bridge.Connectors.Replay;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class ConnectorCursorTests
{
    // ── Format ────────────────────────────────────────────────────────

    [Fact]
    public void Format_UtcValue_ProducesRoundTripString()
    {
        var value = new DateTimeOffset(2026, 3, 15, 10, 20, 30, 123, TimeSpan.Zero);
        var cursor = ConnectorCursor.Format(value);

        Assert.True(ConnectorCursor.TryParse(cursor, out var parsed));
        Assert.Equal(value.ToUniversalTime(), parsed.ToUniversalTime());
    }

    [Fact]
    public void Format_SubMillisecondPrecision_IsPreserved()
    {
        var ticks = DateTimeOffset.UtcNow.Ticks;
        var value = new DateTimeOffset(ticks, TimeSpan.Zero);
        var cursor = ConnectorCursor.Format(value);

        Assert.True(ConnectorCursor.TryParse(cursor, out var parsed));
        Assert.Equal(value.UtcTicks, parsed.UtcTicks);
    }

    [Fact]
    public void Format_NonUtcValue_ProducesUtcEquivalentString()
    {
        var value = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.FromHours(3));
        var cursor = ConnectorCursor.Format(value);

        // The "o" format with ToUniversalTime() produces +00:00 offset.
        Assert.Contains("+00:00", cursor);
        Assert.True(ConnectorCursor.TryParse(cursor, out var parsed));
        Assert.Equal(value.ToUniversalTime(), parsed.ToUniversalTime());
    }

    // ── TryParse — valid inputs ────────────────────────────────────────

    [Fact]
    public void TryParse_ZSuffixedValue_ParsesCorrectly()
    {
        var cursor = "2026-01-01T00:00:00.0000000Z";

        var result = ConnectorCursor.TryParse(cursor, out var value);

        Assert.True(result);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddYears(56), value.ToUniversalTime());
    }

    [Fact]
    public void TryParse_OffsetSuffixedValue_ParsesCorrectly()
    {
        var cursor = "2026-01-01T03:00:00.0000000+03:00";

        var result = ConnectorCursor.TryParse(cursor, out var value);

        Assert.True(result);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), value.ToUniversalTime());
    }

    [Fact]
    public void TryParse_RoundTripPreservesSubMillisecond()
    {
        var original = new DateTimeOffset(2026, 5, 20, 8, 30, 45, 123, TimeSpan.Zero).AddTicks(4567);
        var cursor = ConnectorCursor.Format(original);

        Assert.True(ConnectorCursor.TryParse(cursor, out var parsed));
        Assert.Equal(original.UtcTicks, parsed.UtcTicks);
    }

    // ── TryParse — invalid inputs ─────────────────────────────────────

    [Fact]
    public void TryParse_NotADate_ReturnsFalse()
    {
        Assert.False(ConnectorCursor.TryParse("not-a-date", out _));
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsFalse()
    {
        Assert.False(ConnectorCursor.TryParse(string.Empty, out _));
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.False(ConnectorCursor.TryParse(null, out _));
    }

    [Fact]
    public void TryParse_TenKilobyteJunkString_ReturnsFalseWithoutThrowing()
    {
        var junk = new string('x', 10_000);
        var ex = Record.Exception(() => ConnectorCursor.TryParse(junk, out _));
        Assert.Null(ex);
        Assert.False(ConnectorCursor.TryParse(junk, out _));
    }
}
