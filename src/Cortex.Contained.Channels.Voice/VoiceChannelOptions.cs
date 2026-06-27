namespace Cortex.Contained.Channels.Voice;

using Cortex.Contained.Contracts.Recording;
using Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Configuration options for the Voice channel.
/// </summary>
public sealed record VoiceChannelOptions
{
    /// <summary>
    /// Audio input device index. -1 for system default.
    /// Use <see cref="WindowsAudioCapture.GetAvailableDevices"/> to enumerate devices.
    /// </summary>
    public int InputDeviceIndex { get; init; } = -1;

    /// <summary>
    /// Audio output device index. -1 for system default.
    /// Use <see cref="WindowsAudioPlayback.GetAvailableDevices"/> to enumerate devices.
    /// </summary>
    public int OutputDeviceIndex { get; init; } = -1;

    /// <summary>
    /// Whether push-to-talk mode is enabled. When true (the default), the hotkey
    /// specified by <see cref="PushToTalkHotkey"/> — or the overlay button — starts
    /// and stops listening. When false, the channel is open-mic (always listening,
    /// VAD-segmented). Wake-word activation was removed.
    /// </summary>
    public bool PushToTalk { get; init; } = true;

    /// <summary>
    /// Global hotkey for push-to-talk activation, e.g. "Ctrl+Space", "Alt+Shift+V", "F5".
    /// Press once to start listening, press again to stop.
    /// Only used when <see cref="PushToTalk"/> is true.
    /// </summary>
    public string PushToTalkHotkey { get; init; } = "Ctrl+Space";

    /// <summary>
    /// Duration of silence (in milliseconds) after speech that triggers
    /// end-of-utterance detection.
    /// </summary>
    public int SilenceTimeoutMs { get; init; } = 1500;

    /// <summary>
    /// Minimum audio energy level (RMS) to consider as speech activity.
    /// Helps filter background noise from triggering Whisper transcription.
    /// </summary>
    public float VoiceActivityThreshold { get; init; } = 0.01f;

    /// <summary>
    /// Unique channel instance identifier.
    /// </summary>
    public string ChannelId { get; init; } = "voice-default";

    /// <summary>
    /// Tenant identifier used by the speaker-verification gate. When the
    /// gate is not configured this can be any non-empty string.
    /// </summary>
    public string TenantId { get; init; } = "default";

    /// <summary>
    /// Optional speaker-verification gate. When non-null, the channel runs
    /// verification in parallel with STT at each utterance commit and drops
    /// transcripts whose voiceprint does not match the enrolled owner.
    /// Null means the gate is inactive (current behaviour — all transcripts pass).
    /// </summary>
    public ISpeakerVerifier? SpeakerVerifier { get; init; }

    /// <summary>
    /// Optional sink for verification outcome counters.
    /// </summary>
    public VerificationMetrics? VerificationMetrics { get; init; }

    /// <summary>
    /// Optional runtime recorder. When a session is active for the local host
    /// channel (started via <c>/voice-record start channel:host</c>), the
    /// channel taps committed PCM into the recorder; otherwise a no-op
    /// (single dictionary-lookup miss per committed utterance).
    /// </summary>
    public IRecordingController? Recorder { get; init; }
}
