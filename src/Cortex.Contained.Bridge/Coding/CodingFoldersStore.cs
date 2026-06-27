using Cortex.Contained.Bridge.Storage;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Runtime-mutable store for coding folder configuration. Persists to a JSON file.
/// Reads on demand so changes from the web UI are picked up without a restart.
/// Each entry carries an absolute path and a policy ceiling for that folder.
/// </summary>
public sealed class CodingFoldersStore : JsonFileSettingsStore<CodingFoldersStore.CodingFoldersOptions>
{
    public CodingFoldersStore(string filePath)
        : base(filePath)
    {
    }

    /// <summary>Construct using the default location: <c>%APPDATA%\Cortex\coding-folders.json</c>.</summary>
    public static CodingFoldersStore Default()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = Path.Combine(appData, "Cortex", "coding-folders.json");
        return new CodingFoldersStore(path);
    }

    /// <summary>Path of the JSON file backing this store.</summary>
    public new string FilePath => base.FilePath;

    /// <summary>Read the current list of coding folder entries from disk.</summary>
    public IReadOnlyList<CodingFolderEntry> Get()
    {
        return this.Load().Entries;
    }

    /// <summary>
    /// Add a folder with the given label and policy ceiling.
    /// No-op if the folder is already present (case-insensitive).
    /// Returns true if the entry was added; false if it already existed.
    /// </summary>
    public bool Add(string absoluteFolder, string? label, CodingPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteFolder);
        var normalized = CodingFolderResolver.NormalizePath(absoluteFolder);

        lock (this.SyncLock)
        {
            var options = this.LoadInternal();
            if (options.Entries.Any(e => string.Equals(CodingFolderResolver.NormalizePath(e.Path), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            options.Entries.Add(new CodingFolderEntry
            {
                Path = normalized,
                Label = label,
                DefaultPolicy = policy,
            });
            this.SaveInternal(options);
            return true;
        }
    }

    /// <summary>Remove a folder by path. Returns true if a matching entry was removed.</summary>
    public bool Remove(string absoluteFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteFolder);
        var normalized = CodingFolderResolver.NormalizePath(absoluteFolder);

        lock (this.SyncLock)
        {
            var options = this.LoadInternal();
            var initialCount = options.Entries.Count;
            options.Entries.RemoveAll(e => string.Equals(CodingFolderResolver.NormalizePath(e.Path), normalized, StringComparison.OrdinalIgnoreCase));
            if (options.Entries.Count == initialCount)
            {
                return false;
            }

            this.SaveInternal(options);
            return true;
        }
    }

    /// <summary>Returns true if the candidate path is exactly or inside one of the allowed folders.</summary>
    public bool IsAllowed(string absolutePath)
    {
        var paths = this.Get().Select(e => e.Path).ToList();
        return CodingFolderResolver.IsPathInsideAny(absolutePath, paths);
    }

    /// <summary>
    /// Returns the <see cref="CodingPolicy"/> ceiling of the entry containing
    /// <paramref name="absolutePath"/>.  If no entry covers the path, returns
    /// <see cref="CodingPolicy.Prompt"/> (most restrictive).
    /// </summary>
    public CodingPolicy GetCeiling(string absolutePath)
    {
        var entries = this.Get();
        foreach (var entry in entries)
        {
            if (CodingFolderResolver.IsPathInsideAny(absolutePath, [entry.Path]))
            {
                return entry.DefaultPolicy;
            }
        }

        return CodingPolicy.Prompt;
    }

    /// <summary>On-disk shape of the coding folders document.</summary>
    public sealed class CodingFoldersOptions
    {
        public List<CodingFolderEntry> Entries { get; set; } = [];
    }
}
