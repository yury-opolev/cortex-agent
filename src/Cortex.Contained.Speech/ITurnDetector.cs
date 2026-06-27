namespace Cortex.Contained.Speech;

/// <summary>
/// A single chat turn passed into the turn detector. Only <c>user</c> and
/// <c>assistant</c> roles are considered; anything else is filtered out to match
/// LiveKit's reference implementation.
/// </summary>
public sealed record TurnDetectorMessage(string Role, string Content);

/// <summary>
/// Predicts the probability that the current (trailing) user turn is complete.
/// Used to replace a fixed silence timeout with a dynamic "commit fast when the
/// model is confident, wait longer when the user is mid-thought" policy.
/// </summary>
/// <remarks>
/// <para>
/// A high probability (above the language-specific threshold) means "looks done"
/// and the voice pipeline can commit the utterance with a short silence; a low
/// probability means "keep listening" and callers should extend the silence
/// window. Thresholds are calibrated per-language and obtained via
/// <see cref="GetThreshold"/>.
/// </para>
/// <para>
/// Implementations are expected to be thread-safe — a single detector instance
/// is typically shared across tenants and called from multiple voice sessions.
/// </para>
/// </remarks>
public interface ITurnDetector : IDisposable
{
    /// <summary>Whether the detector is loaded and ready to serve predictions.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Predict P(end-of-turn) for the last message in <paramref name="turns"/>.
    /// </summary>
    /// <param name="turns">The conversation so far, oldest first. Only the last few
    /// user/assistant messages are actually used by the model.</param>
    /// <param name="language">ISO 639-1 language code (e.g. "en", "ru"); controls
    /// which threshold <see cref="GetThreshold"/> returns, not the model itself
    /// (the multilingual model handles all languages).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Probability in [0, 1].</returns>
    Task<float> PredictEndOfTurnAsync(
        IReadOnlyList<TurnDetectorMessage> turns,
        string language = "en",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the calibrated decision threshold for the given language.
    /// Probabilities at or above this value are treated as "turn complete".
    /// Falls back to the English threshold for unknown languages.
    /// </summary>
    float GetThreshold(string language);
}
