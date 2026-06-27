namespace Cortex.Contained.Speech;

/// <summary>
/// Resolves the Whisper model path: explicit config wins, then the voice
/// channel setting, then the legacy config key, else the bundled default
/// (large-v3-turbo-q8_0). Pure — extracted from the Bridge composition root so
/// the fallback order is unit-tested. Blank/whitespace is treated as unset
/// (YAML "Key: " binds as "" not null).
/// </summary>
public static class WhisperModelPathResolver
{
    public const string DefaultModelFileName = "ggml-large-v3-turbo-q8_0.bin";

    public static string Resolve(
        string? configured, string? voiceSetting, string? legacyConfig, string defaultModelsDir)
    {
        return NullIfBlank(configured)
            ?? NullIfBlank(voiceSetting)
            ?? NullIfBlank(legacyConfig)
            ?? Path.Combine(defaultModelsDir, DefaultModelFileName);

        static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
