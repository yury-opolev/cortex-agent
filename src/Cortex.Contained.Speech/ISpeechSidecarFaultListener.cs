namespace Cortex.Contained.Speech;

/// <summary>Identifies one of the GPU speech sidecars behind the Bridge.</summary>
public enum SpeechSidecar
{
    /// <summary>The uni-voices text-to-speech sidecar.</summary>
    Tts,

    /// <summary>The whisper-stt speech-to-text sidecar.</summary>
    Stt,
}

/// <summary>
/// Sink for per-request outcomes reported by the remote speech clients. Lets the host
/// detect a sidecar that is reachable and reporting itself healthy while every request
/// actually fails, and take a recovery action.
/// </summary>
/// <remarks>
/// Implementations are called on the speech hot path (once per utterance or sentence),
/// so they must be cheap, thread-safe and non-throwing — any real work belongs on a
/// background task owned by the implementation.
/// </remarks>
public interface ISpeechSidecarFaultListener
{
    /// <summary>Reports a request the sidecar rejected.</summary>
    /// <param name="sidecar">Which sidecar failed.</param>
    /// <param name="detail">Engine name or endpoint, for logging only.</param>
    /// <param name="statusCode">The HTTP status returned by the sidecar.</param>
    void OnSidecarFault(SpeechSidecar sidecar, string detail, int statusCode);

    /// <summary>Reports a request the sidecar served, clearing its fault streak.</summary>
    /// <param name="sidecar">Which sidecar succeeded.</param>
    void OnSidecarSuccess(SpeechSidecar sidecar);
}
