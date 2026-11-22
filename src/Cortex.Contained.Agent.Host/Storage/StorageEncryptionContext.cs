using Cortex.Contained.Contracts.Security;
using MemoryMcp.Core.Configuration;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Where the agent keeps the stores that encryption at rest covers or reports on.</summary>
/// <param name="StateRoot">The state volume (<c>/app/state</c>).</param>
/// <param name="DataRoot">The tool sandbox volume (<c>/app/data</c>).</param>
/// <param name="MemoryDatabasePath">The curated memory database (<c>memory.db</c>).</param>
/// <param name="MemoriesDirectory">The curated memory content files (<c>*.memory.data</c>).</param>
public sealed record StorageLayout(string StateRoot, string DataRoot, string MemoryDatabasePath, string MemoriesDirectory)
{
    /// <summary>The message history.</summary>
    public string MessagesDatabase => Path.Combine(this.StateRoot, "messages.db");

    /// <summary>The history index generations.</summary>
    public string HistoryDirectory => Path.Combine(this.StateRoot, "history");

    /// <summary>The shutdown snapshot of live sessions.</summary>
    public string SessionsSnapshot => Path.Combine(this.StateRoot, Agent.SessionSnapshotSerializer.SnapshotFileName);

    /// <summary>The encryption marker.</summary>
    public string Marker => Path.Combine(this.StateRoot, StorageEncryptionMarker.FileName);

    /// <summary>Run directories of <c>history-eval</c>, <c>history-smoke</c> and <c>memory-eval</c>.</summary>
    public string EvaluationDirectory => Path.Combine(this.StateRoot, "eval");

    /// <summary>The layout for the configured roots and memory options.</summary>
    public static StorageLayout From(string stateRoot, string dataRoot, MemoryMcpOptions memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return new StorageLayout(stateRoot, dataRoot, memory.DatabasePath, memory.MemoriesDirectory);
    }
}

/// <summary>
/// Whether this process runs the stores encrypted, and why. Resolved once at startup: configuration
/// (<c>Storage:EncryptAtRest</c>), the marker written by the owner's switch, or encrypted files found on
/// disk. Any of the three turns encryption on, so a lost marker or a reset configuration can never make
/// the agent create plaintext stores next to encrypted ones.
/// </summary>
public sealed class StorageEncryptionContext
{
    private readonly Lock sync = new();
    private StorageEncryptionMarker? marker;
    private bool restartPending;

    /// <summary>Creates the context.</summary>
    public StorageEncryptionContext(StorageLayout layout, bool enabled, string source, StorageEncryptionMarker? marker)
    {
        this.Layout = layout;
        this.Enabled = enabled;
        this.Source = source;
        this.marker = marker;
    }

    /// <summary>The store locations.</summary>
    public StorageLayout Layout { get; }

    /// <summary>True when the stores run encrypted in this process.</summary>
    public bool Enabled { get; }

    /// <summary><c>off</c>, <c>config</c>, <c>marker</c> or <c>detected</c>.</summary>
    public string Source { get; }

    /// <summary>The marker, if one exists.</summary>
    public StorageEncryptionMarker? Marker
    {
        get
        {
            lock (this.sync)
            {
                return this.marker;
            }
        }
    }

    /// <summary>Encryption was enabled in this process; it applies after the restart.</summary>
    public bool RestartPending
    {
        get
        {
            lock (this.sync)
            {
                return this.restartPending;
            }
        }
    }

    /// <summary>True when anything written from now on must be encrypted (the shutdown snapshot of an enabling process too).</summary>
    public bool ProtectsNewWrites => this.Enabled || this.RestartPending;

    /// <summary>Resolves the mode for this process.</summary>
    public static StorageEncryptionContext Resolve(StorageOptions options, StorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(layout);
        var marker = StorageEncryptionMarker.Read(layout.Marker);
        if (marker is not null)
        {
            return new StorageEncryptionContext(layout, true, "marker", marker);
        }

        if (options.EncryptAtRest)
        {
            return new StorageEncryptionContext(layout, true, "config", null);
        }

        return HasEncryptedData(layout)
            ? new StorageEncryptionContext(layout, true, "detected", null)
            : new StorageEncryptionContext(layout, false, "off", null);
    }

    /// <summary>True when any covered store already holds encrypted bytes: an encrypted database header or session snapshot. Memory content files alone never switch the mode (a note can start with the cipher's magic).</summary>
    public static bool HasEncryptedData(StorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (SqliteFileHeader.Classify(layout.MessagesDatabase) == StoredFileState.Encrypted
            || SqliteFileHeader.Classify(layout.MemoryDatabasePath) == StoredFileState.Encrypted)
        {
            return true;
        }

        return (File.Exists(layout.SessionsSnapshot) && StorageFileCipher.IsEncryptedFile(layout.SessionsSnapshot))
            || Agent.SessionSnapshotSerializer.PendingSnapshots(layout.StateRoot).Count > 0;
    }

    /// <summary>Persists <paramref name="written"/> and flags the restart that applies it.</summary>
    public void MarkRestartPending(StorageEncryptionMarker written)
    {
        ArgumentNullException.ThrowIfNull(written);
        written.Write(this.Layout.Marker);
        lock (this.sync)
        {
            this.marker = written;
            this.restartPending = true;
        }
    }

    /// <summary>Writes a marker for an encrypted process that has none (or one without fingerprint) once the key opened the data.</summary>
    public bool EnsureMarker(string fingerprint, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        lock (this.sync)
        {
            if (!this.Enabled || this.marker is { KeyFingerprint.Length: > 0 })
            {
                return false;
            }

            var written = new StorageEncryptionMarker { KeyFingerprint = fingerprint, EnabledAt = this.marker?.EnabledAt ?? now };
            written.Write(this.Layout.Marker);
            this.marker = written;
            return true;
        }
    }
}
