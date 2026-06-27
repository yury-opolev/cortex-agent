namespace Cortex.Contained.Speech.Tests;

/// <summary>
/// Resolves the location of the downloaded LiveKit turn-detector model files
/// for test fixtures. Prefers the production location populated by
/// <c>SpeechModels.targets</c>; falls back to the spike's cache so dev machines
/// that have run the spike don't need a full Bridge build just to run tests.
/// Throws a friendly <see cref="FileNotFoundException"/> when neither exists.
/// </summary>
internal static class TurnDetectorModelLocator
{
    private static readonly string[] CandidateDirectories = BuildCandidates();

    /// <summary>
    /// Returns the first candidate directory that contains all required files
    /// (<c>model_q8.onnx</c>, <c>tokenizer.json</c>, <c>languages.json</c>).
    /// </summary>
    public static string ResolveRequiredDirectory()
    {
        foreach (var dir in CandidateDirectories)
        {
            if (File.Exists(Path.Combine(dir, "model_q8.onnx"))
                && File.Exists(Path.Combine(dir, "tokenizer.json"))
                && File.Exists(Path.Combine(dir, "languages.json")))
            {
                return dir;
            }
        }

        throw new FileNotFoundException(
            "Turn-detector model files not found in any of the expected locations:\n  "
            + string.Join("\n  ", CandidateDirectories)
            + "\nBuild the Bridge project (SpeechModels.targets downloads the model) "
            + "or run the spike at SPIKES/livekit-turn-detector-spike/TurnDetectorSpike once.");
    }

    /// <summary>
    /// Returns the path to a specific file inside the first directory where
    /// all required files exist.
    /// </summary>
    public static string ResolveRequiredFile(string filename) =>
        Path.Combine(ResolveRequiredDirectory(), filename);

    private static string[] BuildCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            // Production — populated at build-time by src/Cortex.Contained.Bridge/SpeechModels.targets
            Path.Combine(localAppData, "Cortex", "models", "turn-detector"),

            // Developer fallback — the spike's cache at
            //   SPIKES/livekit-turn-detector-spike/TurnDetectorSpike
            Path.Combine(localAppData, "TurnDetectorSpike", "v0.4.1-intl"),
        ];
    }
}
