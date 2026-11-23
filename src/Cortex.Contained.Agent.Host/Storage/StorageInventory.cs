using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Free bytes on the volume that holds a path.</summary>
public interface IDiskSpace
{
    /// <summary>Free bytes available to this process on the volume of <paramref name="path"/>, or -1 when unknown.</summary>
    long AvailableBytes(string path);
}

/// <summary><see cref="IDiskSpace"/> from the mounted drives (the longest mount point that contains the path).</summary>
public sealed class DriveDiskSpace : IDiskSpace
{
    /// <inheritdoc />
    public long AvailableBytes(string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var drive = DriveInfo.GetDrives()
            .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, comparison))
            .MaxBy(d => d.RootDirectory.FullName.Length);
        return drive?.AvailableFreeSpace ?? -1;
    }
}

/// <summary>
/// Lists what the agent stores and in which state (encrypted, plaintext, not covered), from file headers
/// only: no key is needed and nothing is opened, created or changed. Paths in the result are relative to
/// the state or data volume, never host paths.
/// </summary>
public sealed class StorageInventory
{
    /// <summary>Item ids of the evaluation copies (erased on request when enabling).</summary>
    public const string EvaluationId = "evaluation";

    private static readonly string[] SqliteSidecars = ["", "-wal", "-shm", "-journal"];

    private static readonly (string Name, string Id, string Label, string Detail)[] NotCoveredItems =
    [
        ("subagents", "subagents", "Subagent transcripts (subagents.db)", "Kept 24 hours (records 7 days) and erased by history deletion; not encrypted."),
        ("todos", "todos", "Agent to-do lists (todos.db)", "Short lists the agent writes for itself; not encrypted."),
        ("scheduler", "scheduler", "Scheduled tasks (tasks.db)", "Task prompts of reminders and recurring jobs; not encrypted."),
        ("external-agent", "external-agent", "Coding sessions (external-agent/sessions.db)", "Session bookkeeping of the coding relay; not encrypted."),
        ("voiceprints", "voiceprints", "Voiceprints (voiceprints.db)", "Speaker embeddings, no message text; not encrypted."),
        ("maintenance", "maintenance", "Maintenance counters (maintenance.db)", "No message text."),
        ("self-notes.md", "self-notes", "Agent self-notes (self-notes.md)", "Operational notes the agent writes and reads every turn; not encrypted."),
        ("context-bootstrap.md", "context-bootstrap", "Context bootstrap (context-bootstrap.md)", "Legacy notes file; not encrypted."),
    ];

    private static readonly HashSet<string> KnownStateEntries = new(StringComparer.Ordinal)
    {
        "messages.db", "messages.db-wal", "messages.db-shm", "messages.db-journal", "history", "memory", "eval", "eval-ledger",
        "recall-settings.json", "recall-settings.json.tmp", StorageEncryptionMarker.FileName, StorageEncryptionMarker.FileName + ".tmp",
        Agent.SessionSnapshotSerializer.SnapshotFileName, Agent.SessionSnapshotSerializer.SnapshotFileName + ".tmp",
        "subagents", "todos", "scheduler", "external-agent", "voiceprints", "maintenance", "self-notes.md", "context-bootstrap.md",
    };

    private readonly StorageLayout layout;

    public StorageInventory(StorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        this.layout = layout;
    }

    /// <summary>Test seam: called with each path before it is read.</summary>
    internal Action<string>? BeforeRead { get; set; }

    /// <summary>Items that encryption at rest covers.</summary>
    public IReadOnlyList<StorageItemDto> Covered() =>
    [
        this.Database("messages", "Message history and event log (messages.db)", this.layout.MessagesDatabase),
        this.HistoryIndex(),
        this.Database("memory-db", "Memory index (memory.db: metadata, vectors, keyword index)", this.layout.MemoryDatabasePath),
        this.MemoryContent(),
        this.Snapshot(),
    ];

    /// <summary>
    /// The evaluation directory (null when it is empty): plaintext copies of history and memory made before encryption was
    /// enabled, or — with <paramref name="encryptedCopies"/> (an encrypted install) — evaluation copies encrypted with the
    /// evaluation key plus plaintext reports (ids and numbers only). On an encrypted install the state follows the files:
    /// every database, sealed text file and frozen manifest is checked, so plaintext left over from before encryption (or
    /// written by an older version) shows as plaintext or mixed, never as encrypted.
    /// </summary>
    public StorageItemDto? Evaluation(bool encryptedCopies = false)
    {
        var (bytes, files) = this.Measure(this.layout.EvaluationDirectory);
        if (files == 0)
        {
            return null;
        }

        var state = encryptedCopies ? this.EvaluationCopiesState() : StorageItemStates.Plaintext;
        return new StorageItemDto
        {
            Id = EvaluationId,
            Label = "Evaluation runs (history-eval, history-smoke, memory-eval)",
            Path = "eval/",
            State = state,
            Bytes = bytes,
            Files = files,
            Detail = !encryptedCopies
                ? "Raw copies of messages.db and the memory store with their datasets. Erase them when enabling; afterwards the evaluations run encrypted inside the agent."
                : state is StorageItemStates.Plaintext or StorageItemStates.Mixed
                    ? "Plaintext copies of history, memory or datasets remain in eval/ (made before encryption was enabled). Encryption does not cover them: erase them (delete the files or the eval/ directory on the state volume); the evaluation commands recreate encrypted ones."
                    : "Corpus copies and frozen corpora are encrypted with the evaluation key and datasets are sealed; reports, verdicts and manifests hold ids and numbers only.",
        };
    }
    /// <summary>Items that stay in plaintext, with the reason (including unrecognized entries of the state volume).</summary>
    public IReadOnlyList<StorageItemDto> NotCovered()
    {
        var items = new List<StorageItemDto>();
        foreach (var (name, id, label, detail) in NotCoveredItems)
        {
            var path = Path.Combine(this.layout.StateRoot, name);
            var (bytes, files) = this.Measure(path);
            if (files > 0)
            {
                items.Add(new StorageItemDto { Id = id, Label = label, Path = name, State = StorageItemStates.NotCovered, Bytes = bytes, Files = files, Detail = detail });
            }
        }

        var toolOutput = this.Measure(Path.Combine(this.layout.DataRoot, "tool-output"));
        if (toolOutput.Files > 0)
        {
            items.Add(new StorageItemDto
            {
                Id = "tool-output",
                Label = "Large tool results (tool-output/ in the sandbox)",
                Path = "data:tool-output/",
                State = StorageItemStates.NotCovered,
                Bytes = toolOutput.Bytes,
                Files = toolOutput.Files,
                Detail = "The agent reads these with its file tools, so they stay readable; deleted after 7 days and by history deletion.",
            });
        }

        if (this.TryRead(this.layout.StateRoot, () => Directory.Exists(this.layout.StateRoot) ? Directory.EnumerateFileSystemEntries(this.layout.StateRoot).Order(StringComparer.Ordinal).ToList() : [], out var entries))
        {
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (KnownStateEntries.Contains(name) || name.StartsWith(Agent.SessionSnapshotSerializer.PendingPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var (bytes, files) = this.Measure(entry);
                items.Add(new StorageItemDto
                {
                    Id = "other:" + name,
                    Label = name,
                    Path = name,
                    State = StorageItemStates.NotCovered,
                    Bytes = bytes,
                    Files = files,
                    Detail = "Not recognized by this version. Review it and delete it if it holds personal data.",
                });
            }
        }

        return items;
    }

    /// <summary>True when any covered item holds encrypted bytes.</summary>
    public bool HasEncryptedData() => this.Covered().Any(i => i.State is StorageItemStates.Encrypted or StorageItemStates.Mixed);

    // A file that vanishes or cannot be read mid-scan is skipped here; the covered items turn Unknown instead.
    private (long Bytes, int Files) Measure(string path)
    {
        if (!this.TryRead(path, () => (Bytes: File.Exists(path) ? new FileInfo(path).Length : -1L, IsDirectory: Directory.Exists(path)), out var shape))
        {
            return (0, 0);
        }

        if (shape.Bytes >= 0)
        {
            return (shape.Bytes, 1);
        }

        if (!shape.IsDirectory || !this.TryRead(path, () => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList(), out var files))
        {
            return (0, 0);
        }

        long bytes = 0;
        var count = 0;
        foreach (var file in files)
        {
            if (this.TryRead(file, () => new FileInfo(file).Length, out var length))
            {
                bytes += length;
                count++;
            }
        }

        return (bytes, count);
    }

    private bool TryRead<T>(string path, Func<T> read, out T value)
    {
        try
        {
            this.BeforeRead?.Invoke(path);
            value = read();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            value = default!;
            return false;
        }
    }

    // Files of the evaluation directory that carry personal text or data (reports, verdicts and the *.example.jsonl samples do not).
    private static bool CarriesPersonalData(string name) =>
        name.EndsWith(".db", StringComparison.Ordinal)
        || name.EndsWith(".memory.data", StringComparison.Ordinal)
        || (name.EndsWith(".jsonl", StringComparison.Ordinal) && !name.EndsWith(".example.jsonl", StringComparison.Ordinal))
        || string.Equals(name, FrozenCorpusBuilder.SecretFileName, StringComparison.Ordinal);

    private string EvaluationCopiesState()
    {
        var states = new List<StoredFileState?>();
        var evaluation = this.layout.EvaluationDirectory;
        if (!this.TryRead(evaluation, () => Directory.EnumerateFiles(evaluation, "*", SearchOption.AllDirectories).ToList(), out var files))
        {
            return StorageItemStates.Unknown;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!CarriesPersonalData(name))
            {
                continue;
            }

            states.Add(this.TryRead(file, () => new FileInfo(file).Length == 0
                ? StoredFileState.Empty
                : name.EndsWith(".db", StringComparison.Ordinal) ? SqliteFileHeader.Classify(file)
                : StorageFileCipher.IsEncryptedFile(file) ? StoredFileState.Encrypted : StoredFileState.Plaintext, out var state) ? state : null);
        }

        foreach (var manifest in files.Where(f => Path.GetFileName(f) == FrozenCorpusManifest.FileName))
        {
            if (this.TryRead(manifest, () => FrozenCorpusManifest.Read(manifest), out var read) && read is { Encrypted: false })
            {
                states.Add(StoredFileState.Plaintext);
            }
        }

        var combined = Combine(states);
        return combined == StorageItemStates.Absent ? StorageItemStates.Encrypted : combined;
    }
    private StoredFileState? Classify(string path) =>
        this.TryRead(path, () => SqliteFileHeader.Classify(path), out var state) ? state : null;

    private StoredFileState? ClassifyContent(string path) =>
        this.TryRead(path, () => StorageFileCipher.IsEncryptedFile(path) ? StoredFileState.Encrypted : StoredFileState.Plaintext, out var state) ? state : null;

    private static string Combine(IEnumerable<StoredFileState?> states)
    {
        var all = states.ToList();
        if (all.Any(s => s is null))
        {
            return StorageItemStates.Unknown;
        }

        var present = all.Where(s => s is StoredFileState.Plaintext or StoredFileState.Encrypted).Distinct().ToList();
        return present.Count switch
        {
            0 => StorageItemStates.Absent,
            1 => present[0] == StoredFileState.Encrypted ? StorageItemStates.Encrypted : StorageItemStates.Plaintext,
            _ => StorageItemStates.Mixed,
        };
    }

    private string Relative(string path) => Path.GetRelativePath(this.layout.StateRoot, path).Replace('\\', '/');

    private StorageItemDto Database(string id, string label, string path) => new()
    {
        Id = id,
        Label = label,
        Path = this.Relative(path),
        State = Combine([this.Classify(path)]),
        Bytes = SqliteSidecars.Select(s => path + s).Sum(f => this.Measure(f).Bytes),
    };

    private StorageItemDto HistoryIndex()
    {
        var generationsKnown = this.TryRead(this.layout.HistoryDirectory, () => Directory.Exists(this.layout.HistoryDirectory)
            ? Directory.EnumerateFiles(this.layout.HistoryDirectory, "history-index.*.db").ToList()
            : [], out var generations);
        return new StorageItemDto
        {
            Id = "history-index",
            Label = "History search index (keyword postings, vectors)",
            Path = "history/",
            State = generationsKnown ? Combine(generations.Select(this.Classify)) : StorageItemStates.Unknown,
            Bytes = this.Measure(this.layout.HistoryDirectory).Bytes,
            Files = generations.Count,
        };
    }

    private StorageItemDto MemoryContent()
    {
        var filesKnown = this.TryRead(this.layout.MemoriesDirectory, () => Directory.Exists(this.layout.MemoriesDirectory)
            ? Directory.EnumerateFiles(this.layout.MemoriesDirectory, "*.memory.data").Where(f => f.EndsWith(".memory.data", StringComparison.Ordinal)).ToList()
            : [], out var files);
        return new StorageItemDto
        {
            Id = "memory-content",
            Label = "Memory content files (*.memory.data)",
            Path = this.Relative(this.layout.MemoriesDirectory) + "/",
            State = filesKnown ? Combine(files.Select(this.ClassifyContent)) : StorageItemStates.Unknown,
            Bytes = files.Sum(f => this.Measure(f).Bytes),
            Files = files.Count,
        };
    }

    private StorageItemDto Snapshot()
    {
        var path = this.layout.SessionsSnapshot;
        var pending = this.TryRead(this.layout.StateRoot, () => Directory.Exists(this.layout.StateRoot)
            ? Agent.SessionSnapshotSerializer.PendingSnapshots(this.layout.StateRoot)
            : [], out var found) ? found : [];
        var states = new List<StoredFileState?>();
        foreach (var file in new[] { path }.Concat(pending))
        {
            states.Add(this.TryRead(file, () => !File.Exists(file)
                ? StoredFileState.Absent
                : StorageFileCipher.IsEncryptedFile(file) ? StoredFileState.Encrypted : StoredFileState.Plaintext, out var read) ? read : null);
        }

        return new StorageItemDto
        {
            Id = "sessions-snapshot",
            Label = "Live sessions saved at shutdown (sessions-snapshot.json)",
            Path = Path.GetFileName(path),
            State = Combine(states),
            Bytes = new[] { path }.Concat(pending).Sum(f => this.Measure(f).Bytes),
        };
    }
}
