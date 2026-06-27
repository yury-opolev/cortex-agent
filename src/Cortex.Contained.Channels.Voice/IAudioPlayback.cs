namespace Cortex.Contained.Channels.Voice;

/// <summary>
/// Represents an audio output playback device.
/// </summary>
public interface IAudioPlayback : IDisposable
{
    /// <summary>
    /// Play audio data through the configured output device.
    /// The implementation should block until playback is complete or cancelled.
    /// </summary>
    /// <param name="pcmData">Mono 16-bit PCM audio data.</param>
    /// <param name="sampleRate">Sample rate of the PCM data in Hz (e.g. 16000, 24000, 48000).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PlayAsync(byte[] pcmData, int sampleRate, CancellationToken cancellationToken = default);

    /// <summary>Stop any currently playing audio.</summary>
    void StopPlayback();

    /// <summary>Pause playback at the current position. No-op if not playing or already paused.</summary>
    void PausePlayback();

    /// <summary>Resume playback from the paused position. No-op if not paused.</summary>
    void ResumePlayback();

    /// <summary>Whether audio is currently playing.</summary>
    bool IsPlaying { get; }

    /// <summary>Whether audio is currently paused.</summary>
    bool IsPaused { get; }

    // GetAvailableDevices() is on concrete implementations (e.g. WindowsAudioPlayback)
    // because static abstract interface members cannot be used with DI generic type arguments.
}
