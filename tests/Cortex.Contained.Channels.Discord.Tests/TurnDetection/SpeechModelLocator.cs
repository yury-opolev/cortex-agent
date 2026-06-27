namespace Cortex.Contained.Channels.Discord.Tests.TurnDetection;

/// <summary>
/// Locates the speech-model artifacts produced by
/// <c>src/Cortex.Contained.Bridge/SpeechModels.targets</c>. When the models
/// aren't on disk the WAV-fixture tests are skipped — that build target only
/// runs from the Bridge project, so a fresh clone of just the tests project
/// will not have them.
/// </summary>
internal static class SpeechModelLocator
{
    public static bool TryLocate(out string whisperPath, out string turnDetectorDir)
    {
        whisperPath = string.Empty;
        turnDetectorDir = string.Empty;

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var solutionMarker = Path.Combine(dir, "cortex-contained.sln");
            if (File.Exists(solutionMarker))
            {
                var modelsDir = Path.Combine(dir, "models");
                var whisper = Path.Combine(modelsDir, "ggml-base.bin");
                var detectorDir = Path.Combine(modelsDir, "turn-detector");
                if (File.Exists(whisper)
                    && File.Exists(Path.Combine(detectorDir, "model_q8.onnx"))
                    && File.Exists(Path.Combine(detectorDir, "tokenizer.json"))
                    && File.Exists(Path.Combine(detectorDir, "languages.json")))
                {
                    whisperPath = whisper;
                    turnDetectorDir = detectorDir;
                    return true;
                }
                return false;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }
}
