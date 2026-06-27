using Cortex.Contained.Speech.Stt;

namespace Cortex.Contained.Speech.Tests;

/// <summary>
/// <see cref="NullTurnDetector"/> is the fallback when no real model is
/// configured. It must behave neutrally so callers can treat it as
/// interchangeable with a real detector: predictable probability, a threshold
/// that <em>always</em> causes the probability to fall below it (so the voice
/// pipeline behaves as if no smart endpointing exists), and no side effects.
/// </summary>
public class NullTurnDetectorTests
{
    [Fact]
    public void IsReady_ReturnsTrue()
    {
        using var sut = new NullTurnDetector();

        Assert.True(sut.IsReady);
    }

    [Fact]
    public async Task PredictEndOfTurnAsync_Always_ReturnsZero()
    {
        // Returning 0 (not 0.5) guarantees the probability is strictly below
        // the positive threshold, so callers fall back to their silence-timeout
        // policy instead of any 'smart' early commit.
        using var sut = new NullTurnDetector();

        var p = await sut.PredictEndOfTurnAsync(
            [new TurnDetectorMessage("user", "hello")]);

        Assert.Equal(0f, p);
    }

    [Fact]
    public async Task PredictEndOfTurnAsync_WithEmptyTurns_ReturnsZero()
    {
        using var sut = new NullTurnDetector();

        var p = await sut.PredictEndOfTurnAsync([]);

        Assert.Equal(0f, p);
    }

    [Fact]
    public void GetThreshold_AnyLanguage_ReturnsPositiveValue()
    {
        // A positive threshold means Predict (which returns 0) will always
        // compare as "not yet complete" — the null detector effectively
        // disables smart endpointing without changing the caller's branch shape.
        using var sut = new NullTurnDetector();

        Assert.True(sut.GetThreshold("en") > 0);
        Assert.True(sut.GetThreshold("ru") > 0);
        Assert.True(sut.GetThreshold("") > 0);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var sut = new NullTurnDetector();

        sut.Dispose();
        sut.Dispose();
        // no throw = pass
    }
}
