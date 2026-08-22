namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Sink for per-request synthesis outcomes reported by <see cref="RemoteTtsProvider"/>.
/// Lets the host detect a TTS sidecar that is reachable and reporting itself healthy
/// while every synthesis actually fails, and take a recovery action.
/// </summary>
/// <remarks>
/// Implementations are called on the synthesis hot path (once per sentence), so they
/// must be cheap, thread-safe and non-throwing — any real work belongs on a background
/// task owned by the implementation.
/// </remarks>
public interface ITtsFaultListener
{
    /// <summary>Reports a synthesis request that the sidecar rejected.</summary>
    /// <param name="engineName">The uni-voices engine that was asked to synthesize.</param>
    /// <param name="statusCode">The HTTP status returned by the sidecar.</param>
    void OnSynthesisFault(string engineName, int statusCode);

    /// <summary>Reports a synthesis request the sidecar accepted, clearing any fault streak.</summary>
    /// <param name="engineName">The uni-voices engine that served the request.</param>
    void OnSynthesisSuccess(string engineName);
}
