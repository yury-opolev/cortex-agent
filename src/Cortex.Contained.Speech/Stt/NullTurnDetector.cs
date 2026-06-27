namespace Cortex.Contained.Speech.Stt;

/// <summary>
/// Fallback <see cref="ITurnDetector"/> that always reports "not done". Used
/// when no real model is configured (or fails to load) so callers can treat
/// the detector as unconditionally available and fall back to their silence-
/// timeout policy without branch-shape changes.
/// </summary>
public sealed class NullTurnDetector : ITurnDetector
{
    // A small positive threshold — anything > 0 works. The Predict* method
    // always returns 0, so the comparison probability >= threshold will
    // always be false and callers use their default silence timeout.
    private const float DefaultThreshold = 0.5f;

    /// <inheritdoc />
    public bool IsReady => true;

    /// <inheritdoc />
    public Task<float> PredictEndOfTurnAsync(
        IReadOnlyList<TurnDetectorMessage> turns,
        string language = "en",
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0f);

    /// <inheritdoc />
    public float GetThreshold(string language) => DefaultThreshold;

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release.
    }
}
