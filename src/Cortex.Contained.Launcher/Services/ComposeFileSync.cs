namespace Cortex.Contained.Launcher.Services;

/// <summary>
/// Keeps the stable compose file (<c>%LOCALAPPDATA%\Cortex\docker-compose.yml</c>) equal to the one bundled
/// with the installed package. The Bridge runs its sidecar commands (embeddings, voice, reranker) against
/// the stable copy, so it must be refreshed on every launch — not only when containers are started, which
/// a self-update that already recreated the agent skips.
/// </summary>
internal static class ComposeFileSync
{
    private const string FileName = "docker-compose.yml";

    /// <summary>Copies the bundled compose file over the stable copy; returns its path, or null when nothing is bundled.</summary>
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
        return stablePath;
    }
}
