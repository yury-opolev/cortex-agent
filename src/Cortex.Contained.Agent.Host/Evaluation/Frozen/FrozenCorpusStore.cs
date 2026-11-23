using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>A history deletion that completed: messages from <see cref="From"/> (open when null) to <see cref="To"/> are gone.</summary>
public sealed record PurgedRange(DateTimeOffset? From, DateTimeOffset To, DateTimeOffset At);

/// <summary>
/// The frozen corpus versions under <c>/app/state/eval/frozen</c> (one directory per version), their export
/// packages under <c>/app/state/eval/packages</c> and the ledger of off-machine copies the owner still has to
/// delete (<c>offsite-deletions.json</c>, D3). Version numbers are never reused. The ledger, the version counter
/// (<c>last-version</c>) and the log of completed purge ranges live in <c>/app/state/eval-ledger</c>, beside the evaluation
/// directory and not inside it: erasing the evaluation artifacts (when enabling encryption) must not lose what the owner
/// still has to delete off the machine, nor let a version number or a purged corpus come back.
/// </summary>
public sealed class FrozenCorpusStore
{
    private const string LedgerFileName = "offsite-deletions.json";
    private const string CounterFileName = "last-version";
    private const string PurgeLogFileName = "purges.json";

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
        this.LedgerDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(evaluationDirectory.TrimEnd('/', '\\')))!, "eval-ledger");
        this.MigrateLegacyLedger();
    }

    /// <summary>Directory of the ledger, the version counter and the purge log (outside the evaluation directory, never erased with it).</summary>
    public string LedgerDirectory { get; }

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
            Directory.CreateDirectory(this.LedgerDirectory);
            var counter = Path.Combine(this.LedgerDirectory, CounterFileName);
            var last = ReadCounter(counter);
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
        if (manifest is not null && (manifest.ExportedAt is not null || manifest.Offsite is not null || manifest.Imported))
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
        catch (DirectoryNotFoundException)
        {
            // No packages directory: nothing was ever exported.
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
            try
            {
                return ReadLedger(Path.Combine(this.LedgerDirectory, LedgerFileName));
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>True when the off-machine copy of <paramref name="version"/> is still waiting to be deleted.</summary>
    public bool IsOffsiteDeletionPending(string version) =>
        this.PendingDeletions().Any(p => string.Equals(p.Version, version, StringComparison.Ordinal));

    /// <summary>
    /// Records a completed history deletion. The log is permanent (it outlives the versions it destroyed and the
    /// evaluation directory), so a package whose corpus overlaps it can be refused on import.
    /// </summary>
    public void RecordPurge(DateTimeOffset? from, DateTimeOffset to, DateTimeOffset at)
    {
        lock (this.sync)
        {
            var purges = this.CompletedPurges().ToList();
            if (purges.Any(p => p.From == from && p.To == to))
            {
                return;
            }

            purges.Add(new PurgedRange(from, to, at));
            Directory.CreateDirectory(this.LedgerDirectory);
            var path = Path.Combine(this.LedgerDirectory, PurgeLogFileName);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(purges, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }

    /// <summary>Every completed history deletion recorded so far.</summary>
    public IReadOnlyList<PurgedRange> CompletedPurges()
    {
        lock (this.sync)
        {
            var path = Path.Combine(this.LedgerDirectory, PurgeLogFileName);
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<List<PurgedRange>>(File.ReadAllText(path), Json) ?? [] : [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>The first completed purge whose range overlaps <paramref name="corpusFrom"/>..<paramref name="corpusTo"/>, or null.</summary>
    public PurgedRange? PurgedRangeOverlapping(DateTimeOffset corpusFrom, DateTimeOffset corpusTo) =>
        this.CompletedPurges().FirstOrDefault(p => corpusFrom <= p.To && (p.From is null || corpusTo >= p.From));

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
        Directory.CreateDirectory(this.LedgerDirectory);
        var path = Path.Combine(this.LedgerDirectory, LedgerFileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(pending, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static List<OffsiteDeletionDto> ReadLedger(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<List<OffsiteDeletionDto>>(File.ReadAllText(path), Json) ?? [] : [];

    private static int ReadCounter(string path) =>
        File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture, out var n) ? n : 0;

    // Older installs kept both files under eval/frozen, where the evaluation erase on enabling encryption deleted them.
    private void MigrateLegacyLedger()
    {
        var legacyLedger = Path.Combine(this.Root, LedgerFileName);
        var legacyCounter = Path.Combine(this.Root, CounterFileName);
        if (!File.Exists(legacyLedger) && !File.Exists(legacyCounter))
        {
            return;
        }

        lock (this.sync)
        {
            try
            {
                if (File.Exists(legacyLedger))
                {
                    var current = this.PendingDeletions();
                    var merged = current.Concat(ReadLedger(legacyLedger).Where(l => !current.Any(c => string.Equals(c.Version, l.Version, StringComparison.Ordinal)))).ToList();
                    this.WriteLedger(merged);
                    File.Delete(legacyLedger);
                }

                if (File.Exists(legacyCounter))
                {
                    Directory.CreateDirectory(this.LedgerDirectory);
                    var counter = Path.Combine(this.LedgerDirectory, CounterFileName);
                    File.WriteAllText(counter, Math.Max(ReadCounter(counter), ReadCounter(legacyCounter)).ToString(CultureInfo.InvariantCulture));
                    File.Delete(legacyCounter);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // The legacy files stay where they are and are migrated on the next start.
            }
        }
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
