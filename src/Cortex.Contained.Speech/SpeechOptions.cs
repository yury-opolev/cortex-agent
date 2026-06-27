namespace Cortex.Contained.Speech;

/// <summary>
/// Configuration options for the speech subsystem.
/// </summary>
public sealed class SpeechOptions
{
    /// <summary>Section name in configuration files.</summary>
    public const string SectionName = "speech";

    /// <summary>STT engine settings.</summary>
    public SttOptions Stt { get; set; } = new();

    /// <summary>TTS engine settings.</summary>
    public TtsOptions Tts { get; set; } = new();
}

/// <summary>
/// Speech-to-text engine configuration.
/// </summary>
public sealed class SttOptions
{
    /// <summary>STT engine name: "whisper" (default).</summary>
    public string Engine { get; set; } = "whisper";

    /// <summary>Path to the Whisper model file (e.g. ggml-base.bin).</summary>
    public string? WhisperModelPath { get; set; }

    /// <summary>Language code for Whisper (e.g. "en").</summary>
    public string Language { get; set; } = "en";

    /// <summary>Optional initial prompt to bias domain vocabulary / proper
    /// nouns (e.g. "Cortex"). Empty = none. Applied on the final pass.</summary>
    public string? InitialPrompt { get; set; }
}

/// <summary>
/// Text-to-speech engine configuration.
/// </summary>
public sealed class TtsOptions
{
    /// <summary>TTS engine name: "kokoro" (default, cross-platform) or "windows-sapi".</summary>
    public string Engine { get; set; } = "kokoro";

    /// <summary>Kokoro voice name (e.g. "af_heart").</summary>
    public string KokoroVoice { get; set; } = "af_heart";

    /// <summary>Path to a custom Kokoro model file. Null = auto-download default model.</summary>
    public string? KokoroModelPath { get; set; }

    /// <summary>Windows SAPI voice name (used when Engine = "windows-sapi").</summary>
    public string? WindowsVoiceName { get; set; }

    /// <summary>Windows SAPI speech rate (-10 to 10, used when Engine = "windows-sapi").</summary>
    public int WindowsSpeechRate { get; set; }

    /// <summary>Silero voice name (e.g. "xenia"). Used when Engine = "silero".</summary>
    public string SileroVoice { get; set; } = "xenia";

    /// <summary>Path to base directory containing Silero model subdirectories. Null = default location.</summary>
    public string? SileroModelPath { get; set; }
}
