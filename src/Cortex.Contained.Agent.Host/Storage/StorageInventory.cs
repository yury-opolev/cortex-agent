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
        "messages.db", "messages.db-wal", "messages.db-shm", "messages.db-journal", "history", "memory", "eval",
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

    /// <summary>Items that encryption at rest covers.</summary>
    public IReadOnlyList<StorageItemDto> Covered() =>
    [
        this.Database("messages", "Message history and event log (messages.db)", this.layout.MessagesDatabase),
        this.HistoryIndex(),
        this.Database("memory-db", "Memory index (memory.db: metadata, vectors, keyword index)", this.layout.MemoryDatabasePath),
        this.MemoryContent(),
        this.Snapshot(),
    ];

    /// <summary>Plaintext copies of history and memory made by the evaluation commands (null when there are none).</summary>
    public StorageItemDto? Evaluation()
    {
        var (bytes, files) = Measure(this.layout.EvaluationDirectory);
        return files == 0
            ? null
            : new StorageItemDto
            {
                Id = EvaluationId,
                Label = "Evaluation runs (history-eval, history-smoke, memory-eval)",
                Path = "eval/",
                State = StorageItemStates.Plaintext,
                Bytes = bytes,
                Files = files,
                Detail = "Raw copies of messages.db and the memory store with their datasets. Erase them when enabling; the commands refuse to run while encryption is on.",
            };
    }

    /// <summary>Items that stay in plaintext, with the reason (including unrecognized entries of the state volume).</summary>
    public IReadOnlyList<StorageItemDto> NotCovered()
    {
        var items = new List<StorageItemDto>();
        foreach (var (name, id, label, detail) in NotCoveredItems)
        {
            var path = Path.Combine(this.layout.StateRoot, name);
            var (bytes, files) = Measure(path);
            if (files > 0)
            {
                items.Add(new StorageItemDto { Id = id, Label = label, Path = name, State = StorageItemStates.NotCovered, Bytes = bytes, Files = files, Detail = detail });
            }
        }

        var toolOutput = Measure(Path.Combine(this.layout.DataRoot, "tool-output"));
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

        if (Directory.Exists(this.layout.StateRoot))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(this.layout.StateRoot).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(entry);
                if (KnownStateEntries.Contains(name))
                {
                    continue;
                }

                var (bytes, files) = Measure(entry);
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

    private static (long Bytes, int Files) Measure(string path)
    {
        if (File.Exists(path))
        {
            return (new FileInfo(path).Length, 1);
        }

        if (!Directory.Exists(path))
        {
            return (0, 0);
        }

        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        return (files.Sum(f => f.Length), files.Count);
    }

    private static string Combine(IEnumerable<StoredFileState> states)
    {
        var present = states.Where(s => s is StoredFileState.Plaintext or StoredFileState.Encrypted).Distinct().ToList();
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
        State = Combine([SqliteFileHeader.Classify(path)]),
        Bytes = SqliteSidecars.Select(s => path + s).Where(File.Exists).Sum(f => new FileInfo(f).Length),
    };

    private StorageItemDto HistoryIndex()
    {
        var generations = Directory.Exists(this.layout.HistoryDirectory)
            ? Directory.EnumerateFiles(this.layout.HistoryDirectory, "history-index.*.db").ToList()
            : [];
        return new StorageItemDto
        {
            Id = "history-index",
            Label = "History search index (keyword postings, vectors)",
            Path = "history/",
            State = Combine(generations.Select(SqliteFileHeader.Classify)),
            Bytes = Measure(this.layout.HistoryDirectory).Bytes,
            Files = generations.Count,
        };
    }

    private StorageItemDto MemoryContent()
    {
        var files = Directory.Exists(this.layout.MemoriesDirectory)
            ? Directory.EnumerateFiles(this.layout.MemoriesDirectory, "*.memory.data").Where(f => f.EndsWith(".memory.data", StringComparison.Ordinal)).ToList()
            : [];
        return new StorageItemDto
        {
            Id = "memory-content",
            Label = "Memory content files (*.memory.data)",
            Path = this.Relative(this.layout.MemoriesDirectory) + "/",
            State = Combine(files.Select(f => StorageFileCipher.IsEncryptedFile(f) ? StoredFileState.Encrypted : StoredFileState.Plaintext)),
            Bytes = files.Sum(f => new FileInfo(f).Length),
            Files = files.Count,
        };
    }

    private StorageItemDto Snapshot()
    {
        var path = this.layout.SessionsSnapshot;
        var state = !File.Exists(path)
            ? StoredFileState.Absent
            : StorageFileCipher.IsEncryptedFile(path) ? StoredFileState.Encrypted : StoredFileState.Plaintext;
        return new StorageItemDto
        {
            Id = "sessions-snapshot",
            Label = "Live sessions saved at shutdown (sessions-snapshot.json)",
            Path = Path.GetFileName(path),
            State = Combine([state]),
            Bytes = File.Exists(path) ? new FileInfo(path).Length : 0,
        };
    }
}
