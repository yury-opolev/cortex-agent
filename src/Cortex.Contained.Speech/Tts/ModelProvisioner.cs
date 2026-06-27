using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Copies TTS model files from known locations (git submodules) to the models directory
/// on first run. Idempotent — skips if files are already present.
/// </summary>
public static partial class ModelProvisioner
{
    /// <summary>
    /// Ensures Silero model files are in the target directory.
    /// Copies from the git submodule (<c>lib/silero-sharp/</c>) if present and target is missing.
    /// </summary>
    /// <param name="targetDir">Target directory (e.g. <c>%LOCALAPPDATA%/Cortex/models/silero/</c>).</param>
    /// <param name="submoduleToolsDir">Path to <c>lib/silero-sharp/tools/</c> (contains model file).</param>
    /// <param name="submoduleAccentorDir">Path to <c>lib/silero-sharp/accentor/</c> (contains stress model).</param>
    /// <param name="logger">Logger for provisioning status.</param>
    public static void EnsureSileroModel(
        string targetDir,
        string submoduleToolsDir,
        string submoduleAccentorDir,
        ILogger logger)
    {
        var targetModelFile = Path.Combine(targetDir, SileroTextToSpeech.DefaultModelFileName);

        // Already provisioned — nothing to do
        if (File.Exists(targetModelFile))
        {
            return;
        }

        // Source model file from the git submodule
        var sourceModelFile = Path.Combine(submoduleToolsDir, SileroTextToSpeech.DefaultModelFileName);
        if (!File.Exists(sourceModelFile))
        {
            LogSubmoduleNotFound(logger, submoduleToolsDir);
            return;
        }

        Directory.CreateDirectory(targetDir);

        // Copy the TorchScript model
        File.Copy(sourceModelFile, targetModelFile, overwrite: false);
        LogFileCopied(logger, SileroTextToSpeech.DefaultModelFileName, targetDir);

        // Copy symbols.json if present
        var sourceSymbols = Path.Combine(submoduleToolsDir, "symbols.json");
        if (File.Exists(sourceSymbols))
        {
            File.Copy(sourceSymbols, Path.Combine(targetDir, "symbols.json"), overwrite: false);
            LogFileCopied(logger, "symbols.json", targetDir);
        }

        // Copy accentor directory (stress/homograph models)
        if (Directory.Exists(submoduleAccentorDir))
        {
            var targetAccentorDir = Path.Combine(targetDir, "accentor");
            CopyDirectory(submoduleAccentorDir, targetAccentorDir);
            LogDirectoryCopied(logger, "accentor", targetDir);
        }

        LogProvisioningComplete(logger, targetDir);
    }

    /// <summary>
    /// Recursively copies a directory and all its contents.
    /// </summary>
    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, targetFile, overwrite: false);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var targetSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, targetSubDir);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Copied {FileName} to {TargetDir}")]
    private static partial void LogFileCopied(ILogger logger, string fileName, string targetDir);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copied {DirName}/ to {TargetDir}")]
    private static partial void LogDirectoryCopied(ILogger logger, string dirName, string targetDir);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Silero submodule not found at {Path} — skipping model provisioning")]
    private static partial void LogSubmoduleNotFound(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Silero model provisioned to {TargetDir}")]
    private static partial void LogProvisioningComplete(ILogger logger, string targetDir);
}
