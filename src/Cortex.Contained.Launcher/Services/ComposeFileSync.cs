namespace Cortex.Contained.Launcher.Services;

/// <summary>
/// Keeps the stable compose files (<c>%LOCALAPPDATA%\Cortex\docker-compose.yml</c> and its optional
/// overrides) equal to the ones bundled with the installed package. The Bridge runs its sidecar commands
/// (embeddings, voice, reranker) against the stable copy, so it must be refreshed on every launch — not only
/// when containers are started, which a self-update that already recreated the agent skips.
/// </summary>
internal static class ComposeFileSync
{
    private const string FileName = "docker-compose.yml";

    /// <summary>Compose overrides the Bridge layers on top of the base file (opt-in embeddings GPU, opt-in embeddings host port).</summary>
    private static readonly string[] OverrideFileNames = ["docker-compose.embeddings-gpu.yml", "docker-compose.embeddings-host.yml"];

    /// <summary>
    /// Copies the bundled compose file and the bundled overrides over the stable copies; returns the path of
    /// the compose file, or null when nothing is bundled.
    /// </summary>
    public static string? Refresh(string bundleDirectory, string dataDirectory)
    {
        var bundledPath = Path.Combine(bundleDirectory, FileName);
        if (!File.Exists(bundledPath))
        {
            return null;
        }

        Directory.CreateDirectory(dataDirectory);
        var stablePath = Path.Combine(dataDirectory, FileName);
        File.Copy(bundledPath, stablePath, overwrite: true);

        foreach (var overrideName in OverrideFileNames)
        {
            var bundledOverride = Path.Combine(bundleDirectory, overrideName);
            if (File.Exists(bundledOverride))
            {
                File.Copy(bundledOverride, Path.Combine(dataDirectory, overrideName), overwrite: true);
            }
        }

        return stablePath;
    }
}
