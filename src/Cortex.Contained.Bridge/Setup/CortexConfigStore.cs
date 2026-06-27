using System.Globalization;

namespace Cortex.Contained.Bridge.Setup;

/// <summary>
/// Single point of entry for all writes to <c>cortex.yml</c>. Every call
/// stamps a <c>.bak-yyyyMMddHHmmss</c> snapshot of the previous file before
/// overwriting, so an accidental nuke (e.g. the 2026-05-20 incident where the
/// LLM-provider save endpoint regenerated the whole YAML from scratch and lost
/// every channel + tenant) is always recoverable from a sibling backup.
/// Keeps the most recent <see cref="MaxBackups"/> backups; older ones are
/// pruned to bound disk usage.
/// </summary>
public static class CortexConfigStore
{
    public const int MaxBackups = 20;

    private const string BackupPrefix = ".bak-";

    /// <summary>
    /// Write <paramref name="newContent"/> to <paramref name="path"/> after
    /// taking a timestamped backup of the previous file (if any). Backups
    /// follow the pattern <c>cortex.yml.bak-yyyyMMddHHmmss</c>. Returns the
    /// full path of the backup that was created, or null if there was no
    /// previous file to back up.
    /// </summary>
    public static string? WriteWithBackup(string path, string newContent)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(newContent);

        string? backupPath = null;
        if (File.Exists(path))
        {
            backupPath = ResolveUniqueBackupPath(path, DateTimeOffset.UtcNow);
            File.Copy(path, backupPath, overwrite: false);
            PruneOldBackups(path);
        }

        File.WriteAllText(path, newContent);
        return backupPath;
    }

    /// <summary>
    /// Resolve a backup path that doesn't already exist on disk. If two saves
    /// land in the same UTC second, the base path collides; append a
    /// <c>-1</c>, <c>-2</c>, ... counter until we find a free slot so neither
    /// backup is lost. The counter is small (single-digit usually) so the
    /// lexical ordering used by <see cref="PruneOldBackups"/> still treats
    /// later-stamp/higher-counter files as newer.
    /// </summary>
    private static string ResolveUniqueBackupPath(string path, DateTimeOffset at)
    {
        var basePath = BackupPathFor(path, at);
        if (!File.Exists(basePath))
        {
            return basePath;
        }

        for (var i = 1; i < 1000; i++)
        {
            var candidate = basePath + "-" + i.ToString(CultureInfo.InvariantCulture);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Pathological: 1000+ backups in the same UTC second. Fall back to a
        // GUID suffix rather than throwing — losing a backup is worse than an
        // ugly filename.
        return basePath + "-" + Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Compute the backup path for a config file at a given moment. Public so
    /// callers/tests can predict (or verify) the file name.
    /// </summary>
    public static string BackupPathFor(string path, DateTimeOffset at)
    {
        var stamp = at.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return path + BackupPrefix + stamp;
    }

    /// <summary>
    /// Keep only the most recent <see cref="MaxBackups"/> backups of the
    /// given config file. Older ones are deleted.
    /// </summary>
    public static int PruneOldBackups(string configPath)
    {
        var dir = Path.GetDirectoryName(configPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return 0;
        }

        var fileName = Path.GetFileName(configPath);
        var prefix = fileName + BackupPrefix;
        var backups = Directory.EnumerateFiles(dir, fileName + BackupPrefix + "*")
            .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(f => f, StringComparer.Ordinal) // timestamp suffix → lexical order = chronological
            .ToList();

        if (backups.Count <= MaxBackups)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var stale in backups.Skip(MaxBackups))
        {
            try
            {
                File.Delete(stale);
                deleted++;
            }
            catch
            {
                // Best-effort: a locked or vanished backup shouldn't block writes.
            }
        }

        return deleted;
    }
}
