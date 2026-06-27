namespace Cortex.Contained.Channels.Voice;

/// <summary>
/// Represents an audio input capture device.
/// Implementations must produce 16kHz mono 16-bit PCM audio for Whisper compatibility.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Start capturing audio from the configured device.</summary>
    void Start();

    /// <summary>Stop capturing audio.</summary>
    void StopCapture();

    /// <summary>Whether the capture is currently active.</summary>
    bool IsCapturing { get; }

    /// <summary>
    /// Raised when a new audio buffer is available.
    /// The byte array contains 16kHz mono 16-bit PCM samples.
    /// </summary>
    event EventHandler<AudioBufferEventArgs>? AudioBufferReady;

    // GetAvailableDevices() is on concrete implementations (e.g. WindowsAudioCapture)
    // because static abstract interface members cannot be used with DI generic type arguments.
}

/// <summary>Event args for audio buffer events.</summary>
public sealed class AudioBufferEventArgs : EventArgs
{
    /// <summary>Raw 16kHz mono 16-bit PCM audio data.</summary>
    public required byte[] Buffer { get; init; }

    /// <summary>Number of valid bytes in the buffer.</summary>
    public required int BytesRecorded { get; init; }
}

/// <summary>Describes an available audio device.</summary>
public sealed record AudioDeviceInfo(int DeviceIndex, string Name);
