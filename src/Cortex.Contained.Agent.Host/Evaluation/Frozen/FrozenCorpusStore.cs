using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>
/// The frozen corpus versions under <c>/app/state/eval/frozen</c> (one directory per version), their export
/// packages under <c>/app/state/eval/packages</c> and the ledger of off-machine copies the owner still has to
/// delete (<c>offsite-deletions.json</c>, D3). Version numbers are never reused.
/// </summary>
public sealed class FrozenCorpusStore
{
    private const string LedgerFileName = "offsite-deletions.json";
    private const string CounterFileName = "last-version";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock sync = new();

    /// <param name="evaluationDirectory">The evaluation root (<c>/app/state/eval</c>).</param>
    public FrozenCorpusStore(string evaluationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationDirectory);
        this.Root = Path.Combine(evaluationDirectory, "frozen");
        this.PackagesDirectory = Path.Combine(evaluationDirectory, "packages");
    }

    /// <summary>Directory of the versions.</summary>
    public string Root { get; }

    /// <summary>Directory of the export packages (passphrase-encrypted).</summary>
    public string PackagesDirectory { get; }

    /// <summary>The directory of <paramref name="version"/> (validated: <c>v</c> and a number).</summary>
    public string DirectoryOf(string version) =>
        FrozenCorpusManifest.NumberOf(version) is not null
            ? Path.Combine(this.Root, version)
            : throw new InvalidOperationException($"'{version}' is not a corpus version (v1, v2, …).");

    /// <summary>The package path of <paramref name="version"/>.</summary>
    public string PackageOf(string version) => Path.Combine(this.PackagesDirectory, FrozenCorpusManifest.PackageFileNameOf(Version(version)));

    /// <summary>Every complete version, oldest first.</summary>
    public IReadOnlyList<FrozenCorpusManifest> List() =>
        Directory.Exists(this.Root)
            ? Directory.EnumerateDirectories(this.Root)
                .Where(d => FrozenCorpusManifest.NumberOf(Path.GetFileName(d)) is not null)
                .Select(d => FrozenCorpusManifest.Read(Path.Combine(d, FrozenCorpusManifest.FileName)))
                .OfType<FrozenCorpusManifest>()
                .OrderBy(m => FrozenCorpusManifest.NumberOf(m.Version))
                .ToList()
            : [];

    /// <summary>The manifest of <paramref name="version"/>, or null.</summary>
    public FrozenCorpusManifest? Read(string version) => FrozenCorpusManifest.Read(Path.Combine(this.DirectoryOf(version), FrozenCorpusManifest.FileName));

    /// <summary>Export packages present, by file name.</summary>
    public IReadOnlyList<string> Packages() =>
        Directory.Exists(this.PackagesDirectory)
            ? Directory.EnumerateFiles(this.PackagesDirectory, "*.cxpkg").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>Reserves the next version name (never reused, even after a version was destroyed).</summary>
    public string ReserveNextVersion()
    {
        lock (this.sync)
        {
            Directory.CreateDirectory(this.Root);
            var counter = Path.Combine(this.Root, CounterFileName);
            var last = File.Exists(counter) && int.TryParse(File.ReadAllText(counter).Trim(), CultureInfo.InvariantCulture, out var n) ? n : 0;
            var existing = Directory.EnumerateDirectories(this.Root).Select(d => FrozenCorpusManifest.NumberOf(Path.GetFileName(d).Split('.')[0]) ?? 0).DefaultIfEmpty(0).Max();
            var next = Math.Max(last, existing) + 1;
            File.WriteAllText(counter, next.ToString(CultureInfo.InvariantCulture));
            return "v" + next.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Destroys the local copy of <paramref name="version"/> (directory and package) and, when it was ever
    /// exported, records the off-machine copy for deletion. Returns false when something could not be deleted.
    /// </summary>
    public bool Destroy(string version, DateTimeOffset at)
    {
        var manifest = this.Read(version);
        if (manifest is not null && (manifest.ExportedAt is not null || manifest.Offsite is not null))
        {
            this.AddPendingDeletion(new OffsiteDeletionDto
            {
                Version = version,
                Secret = OffsiteCopy.SecretName,
                PackageFileName = manifest.PackageFileName,
                ContentId = manifest.Offsite?.ContentId,
                DestroyedAt = at,
            });
        }

        var gone = true;
        foreach (var path in new[] { this.DirectoryOf(version), this.DirectoryOf(version) + ".partial" })
        {
            gone &= TryDeleteDirectory(path);
        }

        var package = this.PackageOf(version);
        try
        {
            File.Delete(package);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            gone = false;
        }

        return gone;
    }

    /// <summary>Off-machine copies the owner still has to delete.</summary>
    public IReadOnlyList<OffsiteDeletionDto> PendingDeletions()
    {
        lock (this.sync)
        {
            var path = Path.Combine(this.Root, LedgerFileName);
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<List<OffsiteDeletionDto>>(File.ReadAllText(path), Json) ?? [] : [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>Records that the owner deleted the off-machine copy of <paramref name="version"/>; false when none was pending.</summary>
    public bool AcknowledgeDeletion(string version)
    {
        lock (this.sync)
        {
            var pending = this.PendingDeletions().ToList();
            var removed = pending.RemoveAll(p => string.Equals(p.Version, version, StringComparison.Ordinal));
            this.WriteLedger(pending);
            return removed > 0;
        }
    }

    private void AddPendingDeletion(OffsiteDeletionDto deletion)
    {
        lock (this.sync)
        {
            var pending = this.PendingDeletions().Where(p => !string.Equals(p.Version, deletion.Version, StringComparison.Ordinal)).Append(deletion).ToList();
            this.WriteLedger(pending);
        }
    }

    private void WriteLedger(IReadOnlyList<OffsiteDeletionDto> pending)
    {
        Directory.CreateDirectory(this.Root);
        var path = Path.Combine(this.Root, LedgerFileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(pending, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static string Version(string version) => FrozenCorpusManifest.NumberOf(version) is not null ? version : throw new InvalidOperationException($"'{version}' is not a corpus version (v1, v2, …).");

    private static bool TryDeleteDirectory(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
