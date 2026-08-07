using Cortex.Contained.Bridge.Connectors;

namespace Cortex.Contained.Bridge.Tests.Connectors;

/// <summary>
/// Pins that capping untrusted connector text never splits a UTF-16 surrogate pair. This project
/// shipped exactly that bug once already in the WebSocket close-reason path, so the invariant is
/// asserted directly rather than left to inspection.
/// </summary>
public sealed class ConnectorTextTests
{
    [Fact]
    public void Truncate_ValueWithinLimit_IsReturnedUnchanged()
    {
        Assert.Equal("terminal", ConnectorText.Truncate("terminal", 256));
    }

    [Fact]
    public void Truncate_Null_ReturnsNull()
    {
        Assert.Null(ConnectorText.Truncate(null, 256));
    }

    [Fact]
    public void Truncate_PlainTextOverLimit_IsCutToTheLimit()
    {
        var result = ConnectorText.Truncate(new string('a', 300), 256);

        Assert.NotNull(result);
        Assert.Equal(256, result!.Length);
    }

    [Fact]
    public void Truncate_CutLandingInsideSurrogatePair_DropsTheWholeCharacter()
    {
        // "🎉" is U+1F389, a surrogate pair. With an odd prefix the cut at maxLength lands
        // between the two halves, and slicing naively would leave an orphaned high surrogate.
        var value = "a" + string.Concat(Enumerable.Repeat("🎉", 10));

        var result = ConnectorText.Truncate(value, 4);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Length);
        Assert.False(char.IsHighSurrogate(result[^1]), "truncation left an orphaned high surrogate");
        Assert.Equal(result, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(result)));
    }

    [Fact]
    public void Truncate_CutLandingOnPairBoundary_KeepsTheCharacter()
    {
        var value = string.Concat(Enumerable.Repeat("🎉", 10));

        var result = ConnectorText.Truncate(value, 4);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Length);
        Assert.Equal(result, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(result)));
    }
}
