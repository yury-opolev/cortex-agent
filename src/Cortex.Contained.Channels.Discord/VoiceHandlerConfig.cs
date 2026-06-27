namespace Cortex.Contained.Channels.Discord;

using Cortex.Contained.Contracts.Recording;
using Cortex.Contained.Speech.SpeakerId;
using Cortex.Contained.Speech.Tts;

/// <summary>
/// Per-tenant configuration for a <see cref="DiscordVoiceHandler"/>.
/// Combines tenant-specific settings with shared audio tuning from <see cref="DiscordChannelOptions"/>.
/// </summary>
public sealed record VoiceHandlerConfig
{
    /// <summary>Tenant ID this handler belongs to.</summary>
    public required string TenantId { get; init; }

    /// <summary>Discord guild (server) ID where the voice channel lives.</summary>
    public required ulong GuildId { get; init; }

    /// <summary>Discord voice channel ID for real-time STT/TTS.</summary>
    public required ulong VoiceChannelId { get; init; }

    /// <summary>Greeting spoken when the bot joins the voice channel. Null/empty = disabled.</summary>
    public string? VoiceGreeting { get; init; }

    /// <summary>
    /// The agent's configured voice gender. Selects the male or female variant of
    /// the pre-baked last-resort "trouble speaking" notice. Defaults to
    /// <see cref="Cortex.Contained.Speech.VoiceGender.Female"/>.
    /// </summary>
    public Cortex.Contained.Speech.VoiceGender VoiceGender { get; init; }

    /// <summary>Discord user ID linked to this tenant (for voice state matching).</summary>
    public required ulong LinkedUserId { get; init; }

    /// <summary>Soft commit point on silence (ms). Safety ceiling above the turn-detector early-commit path; the detector can extend up to MaxSilenceTimeoutMs when the user isn't done.</summary>
    public int SilenceTimeoutMs { get; init; } = 1500;

    /// <summary>Whether barge-in detection is enabled.</summary>
    public bool EnableBargeIn { get; init; } = true;

    /// <summary>
    /// When true AND a streaming STT engine is registered, the handler feeds
    /// audio frames incrementally to the streaming recognizer and asks for its
    /// final transcription on silence. When false, the handler uses the legacy
    /// batch path: buffer raw 48kHz audio, resample once at end of utterance,
    /// call <see cref="ISpeechToText.TranscribeAsync"/>. Defaults to true.
    /// </summary>
    public bool UseStreamingStt { get; init; } = true;

    /// <summary>
    /// When true AND an <c>ITurnDetector</c> is registered AND streaming STT is
    /// enabled, the handler asks the detector for P(end-of-turn) on the current
    /// partial transcript during silence and commits early when the model is
    /// confident (cuts voice-to-voice latency by ~1 s on average). The fixed
    /// <see cref="SilenceTimeoutMs"/> remains an upper bound. Defaults to true.
    /// </summary>
    public bool UseTurnDetector { get; init; } = true;

    /// <summary>
    /// Linear gain multiplier applied to all outgoing TTS audio on this handler's
    /// Discord voice path, on top of any per-provider gain already applied inside
    /// the TTS engine. Saturating clamp — no wrap clipping. Default 1.0.
    /// </summary>
    public float OutputGain { get; init; } = 1.0f;

    /// <summary>
    /// Optional speaker-verification gate. When non-null, the handler runs
    /// verification in parallel with STT at each utterance commit and drops
    /// transcripts whose voiceprint does not match the enrolled owner. Null
    /// means the gate is inactive (current behaviour — all transcripts pass).
    /// See <c>docs/superpowers/specs/2026-05-11-voice-speaker-identification-design.md</c>.
    /// </summary>
    public ISpeakerVerifier? SpeakerVerifier { get; init; }

    /// <summary>
    /// Optional sink for verification outcome counters. Wired by the Bridge
    /// when verification is enabled.
    /// </summary>
    public VerificationMetrics? VerificationMetrics { get; init; }

    /// <summary>
    /// Sustained user-speech duration (ms) required before a barge-in stops the
    /// agent. Filters single-frame coughs/claps. Default 150ms.
    /// </summary>
    public int BargeInOnsetGuardMs { get; init; } = 150;

    /// <summary>How the interrupt classifier resolves ambiguous cases.</summary>
    public BargeInClassifierMode BargeInClassifierMode { get; init; }
        = BargeInClassifierMode.HeuristicPlusLlm;

    /// <summary>
    /// Optional runtime recorder. When a session is active for this channel
    /// (started via <c>/voice-record start</c>), the handler taps the
    /// post-resample 16 kHz PCM into the recorder; otherwise it's a no-op
    /// (single dictionary-lookup miss per frame). See
    /// <c>docs/superpowers/specs/2026-05-20-voice-recording-slash-commands-design.md</c>.
    /// </summary>
    public IRecordingController? Recorder { get; init; }

    /// <summary>Speaker embedder used to compute the enrollment voiceprint Bridge-side during the wizard. Null disables the wizard.</summary>
    public ISpeakerEmbedder? SpeakerEmbedder { get; init; }

    /// <summary>Pushes the finished enrollment voiceprint to the Agent (tenantId, embedding, modelId). Null disables the wizard.</summary>
    public Func<string, float[], string, Task>? SubmitVoiceprintAsync { get; init; }

    /// <summary>Enrollment samples captured before building the candidate voiceprint.</summary>
    public int EnrollSamplesRequired { get; init; } = 3;

    /// <summary>Confirmation matches required to finish enrollment.</summary>
    public int EnrollMatchesRequired { get; init; } = 2;

    /// <summary>Cosine threshold an utterance must meet during the confirm phase.</summary>
    public float EnrollConfirmThreshold { get; init; } = 0.55f;

    /// <summary>How long the wizard waits for the next repeat before aborting (releasing the lock). Default 90s.</summary>
    public int EnrollTimeoutMs { get; init; } = 90_000;

    /// <summary>
    /// Optional language detector used to update the channel's sticky current
    /// language from STT transcripts and from completed agent messages. When
    /// <see langword="null"/> (default), language routing is disabled and TTS
    /// receives no hint — identical to today's behaviour.
    /// </summary>
    public ILanguageDetector? LanguageDetector { get; init; }

    /// <summary>
    /// Optional per-channel sticky language store. Read on every agent-reply
    /// synthesis to provide a TTS <c>languageHint</c>; updated from STT
    /// transcripts and completed agent messages via the detector. When
    /// <see langword="null"/> (default), language routing is disabled.
    /// </summary>
    public ChannelLanguageStore? LanguageStore { get; init; }

    /// <summary>
    /// Thresholds the <see cref="LanguageStore"/> uses when deciding to flip
    /// the channel's current language. Defaults to
    /// <see cref="LanguageSwitchThresholds.Default"/>.
    /// </summary>
    public LanguageSwitchThresholds LanguageSwitchThresholds { get; init; } = LanguageSwitchThresholds.Default;
}
