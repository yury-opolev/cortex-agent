namespace Cortex.Contained.Channels.Discord;

/// <summary>Configuration for the Discord bot channel (global/shared settings).</summary>
public sealed record DiscordChannelOptions
{
    /// <summary>Discord bot token from the Developer Portal.</summary>
    public required string BotToken { get; init; }

    /// <summary>
    /// Whether the bot should transcribe voice message attachments (audio/ogg) in DMs.
    /// When enabled, inbound DM voice messages are transcribed via STT and
    /// replies are sent according to <see cref="DmVoiceReplyMode"/>.
    /// Requires ISpeechToText and ITextToSpeech services. Defaults to false.
    /// </summary>
    public bool DmVoiceTranscription { get; init; }

    /// <summary>
    /// How the bot replies to DM voice messages: "text" (default) sends a normal text reply,
    /// "voice" sends an audio-only reply (OGG/Opus attachment, no text).
    /// Only applies when <see cref="DmVoiceTranscription"/> is true.
    /// </summary>
    public string DmVoiceReplyMode { get; init; } = "text";

    /// <summary>
    /// Soft commit point on silence (in milliseconds) before the bot commits the
    /// accumulated utterance. When the turn detector is enabled, the detector
    /// typically commits much earlier (~400ms minimum silence on a confident
    /// end-of-turn), and can extend this soft point up to MaxSilenceTimeoutMs
    /// when the user isn't done. Defaults to 1500ms.
    /// </summary>
    public int SilenceTimeoutMs { get; init; } = 1500;

    /// <summary>
    /// Whether barge-in detection is enabled. When true, user speech during bot playback
    /// pauses the audio and classifies the interruption (interrupt vs backchannel) via LLM.
    /// </summary>
    public bool EnableBargeIn { get; init; } = true;

    /// <summary>
    /// When true AND an <c>IStreamingSpeechToText</c> service is registered, the
    /// voice handler feeds audio frames incrementally to the streaming recognizer
    /// and asks it for a final transcription on silence (typically faster than a
    /// single batch pass at end-of-utterance). Defaults to true; set to false to
    /// force the legacy batch path.
    /// </summary>
    public bool UseStreamingStt { get; init; } = true;

    /// <summary>
    /// When true AND an <c>ITurnDetector</c> is available (the LiveKit ONNX
    /// model loaded at startup), the voice handler consults the detector during
    /// silence and commits early when P(end-of-turn) exceeds the language
    /// threshold. <see cref="SilenceTimeoutMs"/> remains an unconditional upper
    /// bound. Defaults to true — the detector is well-validated and cuts
    /// voice-to-voice latency by ~1s on average for completed sentences while
    /// the silence timeout still protects against detector mistakes.
    /// </summary>
    public bool UseTurnDetector { get; init; } = true;

    /// <summary>
    /// Linear gain multiplier applied to all outgoing TTS audio on the Discord
    /// voice path, on top of any per-provider gain. Saturating clamp — overshoot
    /// clips to int16 min/max. 1.0 = no change. Use this to tune how loud the
    /// bot sounds in Discord calls without affecting local-voice output.
    /// </summary>
    public float OutputGain { get; init; } = 1.0f;

    /// <summary>
    /// Sustained user-speech duration (ms) required before a barge-in stops the
    /// agent. Filters single-frame coughs/claps. Default 150ms.
    /// </summary>
    public int BargeInOnsetGuardMs { get; init; } = 150;

    /// <summary>How the interrupt classifier resolves ambiguous cases.</summary>
    public BargeInClassifierMode BargeInClassifierMode { get; init; }
        = BargeInClassifierMode.HeuristicPlusLlm;
}
