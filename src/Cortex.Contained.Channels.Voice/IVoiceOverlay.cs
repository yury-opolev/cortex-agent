namespace Cortex.Contained.Channels.Voice;

/// <summary>
/// Abstraction for the voice status overlay window.
/// Allows the overlay to be replaced with a no-op implementation for testing
/// or when running without a desktop (e.g. headless service).
/// </summary>
public interface IVoiceOverlay : IDisposable
{
    /// <summary>
    /// Updates the overlay visual state based on the voice pipeline state change.
    /// Shows the overlay during active states (Listening, Processing, Speaking),
    /// hides it during idle states.
    /// </summary>
    void OnVoiceStateChanged(VoiceStateChange e);

    /// <summary>
    /// Updates the equalizer bar animation with a real-time audio level (RMS).
    /// Value range is 0.0 (silence) to ~1.0 (clipping). Typical speech is 0.01-0.15.
    /// </summary>
    void OnAudioLevelChanged(float rmsLevel);
}
