namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Encryption at rest of the agent's stores (status, pre-flight preview, the one-way switch). Part of the
/// composed <see cref="IAgentHub"/> surface; the Bridge "Storage" page and the key push use it.
/// </summary>
public interface IStorageHub
{
    /// <summary>What is encrypted, what is not, and whether the stores are open.</summary>
    Task<StorageEncryptionStatusDto> GetStorageEncryptionStatus();

    /// <summary>What enabling would encrypt, erase and leave alone, plus the disk-space pre-flight. Changes nothing.</summary>
    Task<StorageEncryptionPreviewDto> PreviewStorageEncryption();

    /// <summary>
    /// Turns encryption at rest on for good: writes the marker and restarts the agent, which migrates the
    /// stores on the next start. Requires the storage key already pushed with the given fingerprint.
    /// </summary>
    Task<StorageEncryptionEnableResultDto> EnableStorageEncryption(StorageEncryptionEnableRequestDto request);
}

/// <summary>String values of <see cref="StorageItemDto.State"/>.</summary>
public static class StorageItemStates
{
    /// <summary>Every file of the item is encrypted with the tenant key.</summary>
    public const string Encrypted = "encrypted";

    /// <summary>The item is stored in plaintext (it will be encrypted when encryption is enabled).</summary>
    public const string Plaintext = "plaintext";

    /// <summary>Some files are encrypted, some not (an interrupted migration; finished on the next open).</summary>
    public const string Mixed = "mixed";

    /// <summary>Nothing stored yet.</summary>
    public const string Absent = "absent";

    /// <summary>Stored in plaintext and outside encryption at rest (see the detail).</summary>
    public const string NotCovered = "notCovered";
}

/// <summary>String values of <see cref="StorageEncryptionStatusDto.KeyState"/>.</summary>
public static class StorageKeyStates
{
    /// <summary>No key pushed yet.</summary>
    public const string Absent = "absent";

    /// <summary>A key was pushed and accepted.</summary>
    public const string Accepted = "accepted";

    /// <summary>The pushed key does not belong to this agent's encrypted data.</summary>
    public const string Rejected = "rejected";
}

/// <summary>One stored item of the agent (a database, a group of files).</summary>
public sealed record StorageItemDto
{
    /// <summary>Stable id (<c>messages</c>, <c>history-index</c>, <c>memory-db</c>, <c>memory-content</c>, …).</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable label.</summary>
    public required string Label { get; init; }

    /// <summary>Path relative to the agent's state or data volume (no host paths).</summary>
    public required string Path { get; init; }

    /// <summary>One of <see cref="StorageItemStates"/>.</summary>
    public required string State { get; init; }

    /// <summary>Total size in bytes (sidecar files included).</summary>
    public long Bytes { get; init; }

    /// <summary>File count for file groups; null for single databases.</summary>
    public int? Files { get; init; }

    /// <summary>Why the item is not covered, or what happens to it.</summary>
    public string? Detail { get; init; }
}

/// <summary>Encryption at rest of one agent.</summary>
public sealed record StorageEncryptionStatusDto
{
    /// <summary>True when the stores run encrypted in this process.</summary>
    public bool EncryptAtRest { get; init; }

    /// <summary>Why: <c>off</c>, <c>config</c> (<c>Storage:EncryptAtRest</c>), <c>marker</c> (enabled from the UI) or <c>detected</c> (encrypted files found).</summary>
    public string Source { get; init; } = "off";

    /// <summary>Encryption was enabled and the agent restarts to apply it.</summary>
    public bool RestartPending { get; init; }

    /// <summary>When encryption was enabled (from the marker), if known.</summary>
    public DateTimeOffset? EnabledAt { get; init; }

    /// <summary>One of <see cref="StorageKeyStates"/>.</summary>
    public string KeyState { get; init; } = StorageKeyStates.Absent;

    /// <summary>Fingerprint of the accepted key (never the key).</summary>
    public string? KeyFingerprint { get; init; }

    /// <summary>Fingerprint of the key the encrypted data belongs to (from the marker).</summary>
    public string? ExpectedKeyFingerprint { get; init; }

    /// <summary>True when any store or file is already encrypted (a key must never be minted anew).</summary>
    public bool HasEncryptedData { get; init; }

    /// <summary><c>messages.db</c>: <c>plaintext</c>, <c>open</c>, <c>awaitingKey</c> or <c>failed</c>.</summary>
    public string MessagesState { get; init; } = "plaintext";

    /// <summary>The memory store: <c>plaintext</c>, <c>open</c>, <c>awaitingKey</c> or <c>failed</c>.</summary>
    public string MemoryState { get; init; } = "plaintext";

    /// <summary>Every known stored item and its state.</summary>
    public IReadOnlyList<StorageItemDto> Items { get; init; } = [];
}

/// <summary>What enabling encryption would do (nothing is changed by the preview).</summary>
public sealed record StorageEncryptionPreviewDto
{
    /// <summary>Encryption is already on (or pending a restart).</summary>
    public bool AlreadyEnabled { get; init; }

    /// <summary>Fingerprint of the key the agent holds, if any.</summary>
    public string? KeyFingerprint { get; init; }

    /// <summary>Items that will be encrypted with the tenant key.</summary>
    public IReadOnlyList<StorageItemDto> WillEncrypt { get; init; } = [];

    /// <summary>Plaintext copies that are erased when <see cref="StorageEncryptionEnableRequestDto.EraseEvaluationArtifacts"/> is set.</summary>
    public IReadOnlyList<StorageItemDto> WillErase { get; init; } = [];

    /// <summary>Items that stay in plaintext, each with the reason.</summary>
    public IReadOnlyList<StorageItemDto> NotCovered { get; init; } = [];

    /// <summary>Free bytes the migration needs on the state volume.</summary>
    public long RequiredFreeBytes { get; init; }

    /// <summary>Free bytes on the state volume.</summary>
    public long AvailableFreeBytes { get; init; }

    /// <summary>Free bytes <c>VACUUM</c> needs in the temporary directory.</summary>
    public long RequiredTempBytes { get; init; }

    /// <summary>Free bytes in the temporary directory.</summary>
    public long AvailableTempBytes { get; init; }

    /// <summary>Problems that make enabling fail now.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>Things the owner must know before enabling.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Body of <see cref="IStorageHub.EnableStorageEncryption"/>.</summary>
public sealed record StorageEncryptionEnableRequestDto
{
    /// <summary>Fingerprint of the key the Bridge pushed (and backed up); the agent must hold exactly this key.</summary>
    public required string KeyFingerprint { get; init; }

    /// <summary>Erase the plaintext evaluation copies (<c>history-eval</c>, <c>history-smoke</c>, <c>memory-eval</c>).</summary>
    public bool EraseEvaluationArtifacts { get; init; } = true;
}

/// <summary>Outcome of <see cref="IStorageHub.EnableStorageEncryption"/>.</summary>
public sealed record StorageEncryptionEnableResultDto
{
    /// <summary>True when encryption is (now) enabled.</summary>
    public bool Accepted { get; init; }

    /// <summary>Encryption was already on; nothing changed.</summary>
    public bool AlreadyEnabled { get; init; }

    /// <summary>The agent stops itself and Docker restarts it; the migration runs on that start.</summary>
    public bool RestartScheduled { get; init; }

    /// <summary>Why the request was refused.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}
