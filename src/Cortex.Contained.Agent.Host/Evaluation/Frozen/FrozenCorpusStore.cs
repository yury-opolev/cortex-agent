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

    /// <summary>Largest package chunk the Bridge may read at once (well below the hub's message limit).</summary>
    public const int MaxChunkBytes = 4 * 1024 * 1024;

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

    /// <summary>
    /// The directory of the version named by a <c>--corpus frozen:&lt;version&gt;</c> option, looked up in the evaluation
    /// directory that holds <paramref name="workspaceDirectory"/>; throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    public static string ResolveCorpusOption(string corpus, string workspaceDirectory)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        const string Prefix = "frozen:";
        if (!corpus.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("--corpus takes frozen:<version>, e.g. frozen:v1.");
        }

        var store = new FrozenCorpusStore(Path.GetDirectoryName(Path.GetFullPath(workspaceDirectory.TrimEnd('/', '\\')))!);
        var version = corpus[Prefix.Length..];
        return store.Read(version) is not null
            ? store.DirectoryOf(version)
            : throw new InvalidOperationException($"No frozen corpus {version}: list them with 'eval-corpus list'.");
    }

    /// <summary>
    /// Versions a purge of <paramref name="from"/>..<paramref name="to"/> must destroy (D3): every version whose corpus
    /// range overlaps, plus every version directory without a readable manifest (an interrupted freeze or import:
    /// its range is unknown). Channel-scoped purges are treated like all-channel ones (a version spans every channel).
    /// </summary>
    public IReadOnlyList<string> Overlapping(DateTimeOffset? from, DateTimeOffset to)
    {
        if (!Directory.Exists(this.Root))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(this.Root))
        {
            var name = Path.GetFileName(directory);
            var version = name.EndsWith(".partial", StringComparison.Ordinal) ? name[..^".partial".Length] : name;
            if (FrozenCorpusManifest.NumberOf(version) is null)
            {
                continue;
            }

            var manifest = name == version ? FrozenCorpusManifest.Read(Path.Combine(directory, FrozenCorpusManifest.FileName)) : null;
            if (manifest is null || (manifest.CorpusFrom <= to && (from is null || manifest.CorpusTo >= from)))
            {
                result.Add(version);
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Versions, packages and pending off-machine deletions for the Bridge.</summary>
    public FrozenCorpusStatusDto Status() => new()
    {
        Versions = this.List().Select(ToDto).ToList(),
        Packages = this.Packages(),
        PendingOffsiteDeletions = this.PendingDeletions(),
    };

    /// <summary>The Bridge view of a manifest.</summary>
    public static FrozenCorpusVersionDto ToDto(FrozenCorpusManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new FrozenCorpusVersionDto
        {
            Version = manifest.Version,
            CreatedAt = manifest.CreatedAt,
            CorpusFrom = manifest.CorpusFrom,
            CorpusTo = manifest.CorpusTo,
            Messages = manifest.Messages,
            Memories = manifest.Memories,
            HistoryQueries = manifest.HistoryQueries,
            MemoryQueries = manifest.MemoryQueries,
            PackageFileName = manifest.PackageFileName,
            ExportedAt = manifest.ExportedAt,
            Uploaded = manifest.Offsite is not null,
            Imported = manifest.Imported,
        };
    }

    /// <summary>Up to <paramref name="count"/> bytes (at most 4 MiB) of the package <paramref name="name"/> from <paramref name="offset"/>.</summary>
    public EvaluationPackageChunkDto ReadPackage(string name, long offset, int count)
    {
        if (!this.Packages().Contains(name, StringComparer.Ordinal) || offset < 0 || count is <= 0 or > MaxChunkBytes)
        {
            return new EvaluationPackageChunkDto { Error = "No such package, or an invalid range." };
        }

        using var stream = new FileStream(Path.Combine(this.PackagesDirectory, name), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset > stream.Length)
        {
            return new EvaluationPackageChunkDto { Length = stream.Length, Error = "The offset is past the end of the package." };
        }

        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(count, stream.Length - offset)];
        stream.ReadExactly(buffer);
        return new EvaluationPackageChunkDto { Length = stream.Length, Data = buffer };
    }

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
