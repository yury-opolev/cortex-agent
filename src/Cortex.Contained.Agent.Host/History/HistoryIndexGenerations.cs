using System.Globalization;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// The generation files of the history index in one directory: <c>history-index.&lt;gen&gt;.db</c>
/// plus the pointer file <c>history-index.current</c>, rewritten atomically (temp + rename).
/// </summary>
public sealed class HistoryIndexGenerations
{
    private const string Prefix = "history-index.";
    private const string PointerFile = "history-index.current";

    /// <summary>Purpose label the index passphrase is derived for.</summary>
    public const string PasswordPurpose = "history-index";

    private readonly Storage.StorageKeyRing? keys;

    /// <summary>Creates the manager; the directory is created if missing. With <paramref name="keys"/> every generation is encrypted (D4).</summary>
    public HistoryIndexGenerations(string directory, Storage.StorageKeyRing? keys = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.keys = keys;
        this.Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    /// <summary>The directory holding the generation files.</summary>
    public string Directory { get; }

    /// <summary>Creates generation <paramref name="generation"/> (encrypted when keys are configured).</summary>
    public HistoryIndexDatabase Create(long generation, ProjectorState state) =>
        HistoryIndexDatabase.Create(this.PathFor(generation), state, this.keys?.PasswordFor(PasswordPurpose));

    /// <summary>Opens generation <paramref name="generation"/> (encrypted when keys are configured).</summary>
    public HistoryIndexDatabase Open(long generation) =>
        HistoryIndexDatabase.Open(this.PathFor(generation), this.keys?.PasswordFor(PasswordPurpose));

    /// <summary>Path of generation <paramref name="generation"/>.</summary>
    public string PathFor(long generation) =>
        Path.Combine(this.Directory, string.Create(CultureInfo.InvariantCulture, $"{Prefix}{generation}.db"));

    /// <summary>The generation the pointer file names, or null.</summary>
    public long? ReadCurrent()
    {
        var pointer = Path.Combine(this.Directory, PointerFile);
        return File.Exists(pointer) && long.TryParse(File.ReadAllText(pointer).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var gen)
            ? gen
            : null;
    }

    /// <summary>Atomically points <c>history-index.current</c> at <paramref name="generation"/>.</summary>
    public void WriteCurrent(long generation)
    {
        var pointer = Path.Combine(this.Directory, PointerFile);
        var temp = pointer + ".tmp";
        File.WriteAllText(temp, generation.ToString(CultureInfo.InvariantCulture));
        File.Move(temp, pointer, overwrite: true);
    }

    /// <summary>Generations that have a database file, ascending.</summary>
    public IReadOnlyList<long> List() =>
        System.IO.Directory.EnumerateFiles(this.Directory, Prefix + "*.db")
            .Select(f => Path.GetFileName(f)[Prefix.Length..^3])
            .Select(n => long.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var g) ? g : -1)
            .Where(g => g > 0)
            .Order()
            .ToList();

    /// <summary>The next unused generation number.</summary>
    public long Next() => Math.Max(this.List().DefaultIfEmpty(0).Max(), this.ReadCurrent() ?? 0) + 1;

    /// <summary>Unlinks a generation's database, <c>-wal</c> and <c>-shm</c>. Returns true when all are gone.</summary>
    public bool Delete(long generation)
    {
        var path = this.PathFor(generation);
        var gone = true;
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                gone = false;
            }
            catch (UnauthorizedAccessException)
            {
                gone = false;
            }

            gone &= !File.Exists(file);
        }

        return gone;
    }
}
