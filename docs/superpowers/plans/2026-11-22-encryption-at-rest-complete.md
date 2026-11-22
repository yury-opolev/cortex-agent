# Encryption at Rest, Complete — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make encryption at rest something the owner can turn on — a one-way switch on a new Storage page — and make it cover the whole agent. Today it covers `messages.db` and the history index. This adds the curated memory store (`memory.db` and the content files) and the session snapshot, scrubs message and memory text from the logs, erases or refuses plaintext evaluation copies, reports what is encrypted, adds a password-wrapped recovery kit for the tenant key, and guards against rollbacks.

**Architecture:**
- **The switch.** The owner's switch writes a one-way marker (`/app/state/storage-encryption.json`) in the agent's state volume and restarts the agent. At startup the effective mode is configuration ∨ marker ∨ encrypted files detected.
- **The memory store.** `EncryptedMemoryStore` decorates memory-mcp's `SqliteVecMemoryStore`. It uses a SQLite3 Multiple Ciphers passphrase for `memory.db` and an AES-256-GCM `StorageFileCipher` for the content files, both derived from the tenant key with HKDF and distinct labels. It opens lazily and fails closed, migrating crash-safely.
- **The Bridge.** It gains `/api/tenants/{t}/storage/*`: status, preview, enable, recovery-kit export and restore. All sensitive calls require the Bridge password again. A key push policy never mints a key over encrypted data.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, SignalR, Microsoft.Data.Sqlite + SQLite3MC.PCLRaw.bundle 2.2.7 (SQLite 3.51.2), sqlite-vec 0.1.9 (vendored in memory-mcp), System.Security.Cryptography (HKDF, AesGcm, PBKDF2), xUnit + NSubstitute + FakeTimeProvider, Alpine.js + Bootstrap (Bridge UI).

**Design:** [docs/superpowers/specs/2026-11-22-encryption-at-rest-complete-design.md](../specs/2026-11-22-encryption-at-rest-complete-design.md). Decisions are referred to as D1–D10 and findings as F1–F10.

---

## Ground rules

- Branch `feat/encryption-at-rest` is checked out in the parent repo.
- For the submodule (Task 5), create the branch `feat/encryption-at-rest` in `lib/memory-mcp` from the **pinned** commit `4150f7b`. memory-mcp `main` is `24555a7`, a merge commit that contains `4150f7b`. The parent bumps the pointer in the same task.
- Commit after every task with the trailer `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`. **Never stage `version.json`**: always `git add` explicit paths.
- Do not push.
- Public repository: no real names, places or host paths in code, tests or docs.

## Gotchas (read before Task 1)

1. **Warnings are errors** (`TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`). Analyzers that bit while writing this plan:
   - **CA1873**: do not pass a method call result straight into a `[LoggerMessage]` call; assign it to a local first.
   - **CA1822**: test helpers that touch no instance state must be `static`.
   - **CS8604**: `request.Content?.Length` on a non-nullable `string` makes the flow analysis treat `request.Content` as maybe-null afterwards; use `request.Content.Length`.
2. **memory-mcp tests are not in `cortex-contained.sln`.** Run them with `dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests`. That project has about 200 pre-existing `xUnit1051` warnings (not errors). New tests must pass `TestContext.Current.CancellationToken` so they add none.
3. **The SQLite3MC build compiles `SQLITE_SECURE_DELETE`** (`PRAGMA secure_delete` is `1` on a fresh connection). Do not "add" `secure_delete` to `SqliteStoreBase`. Task 12 adds a guard test instead.
4. **sqlite-vec under SQLite3MC.** `EncryptedSqlite.Prepare` migrates a `vec0` database without loading the extension. VACUUM copies virtual-table schema rows and shadow tables as plain tables. This is verified by Task 7's tests. Do not load vec0 in `Prepare`.
5. **Pools and Windows file locks.**
   - Before scanning or deleting database files in tests, call `SqliteConnection.ClearAllPools()`.
   - Byte scans must open files with `FileShare.ReadWrite | FileShare.Delete`: some stores, such as `MaintenanceStore`, are registered as instances and stay open until the process ends.
6. **`FakeTimeProvider`.** `Task.Delay(delay, timeProvider)` must be *created* before `Advance`. That is why `StorageEncryptionService` starts the delay synchronously (`_ = this.StopAfterDelayAsync()`), not inside `Task.Run`.
7. **`AgentHub` already uses the namespace prefix `Storage.`** for types. A property named `Storage` would shadow it, so the property is called `StorageEncryption`.
8. **DI injects optional constructor parameters when the service is registered.** `MemoryManagementService(…, IMemoryAtRest? atRest = null)` and `AgentHub(…, StorageEncryptionService? storageEncryption = null)` rely on that.
9. **`IAgentHub` composition (Task 11)** must land in the same commit as `AgentHub.Storage.cs`, or the solution does not compile. Task 1 only *adds* `IStorageHub`.
10. **Program top-level pattern.** `args[0] is "a" or "b" or "c" && …` binds as `(args[0] is ("a" or "b" or "c")) && …`.
11. **Files are CRLF.** Use the editor's replace tools, not PowerShell `-replace` with here-strings: LF here-strings do not match.
12. **`dotnet run` of the Agent Host** listens on the `launchSettings.json` port (5208), not `ASPNETCORE_URLS`.
13. **`SubAgentSetGoalToolTests` can fail under parallel load.** It is pre-existing and unrelated; rerun the project before investigating.

## File structure

| Path | New/Mod | Responsibility |
|---|---|---|
| `src/Cortex.Contained.Contracts/Security/StorageKeyFingerprint.cs` | new | non-secret key check value (HKDF, 8 bytes hex) |
| `src/Cortex.Contained.Contracts/Security/SqliteFileHeader.cs` | new | plaintext/encrypted classification by header |
| `src/Cortex.Contained.Contracts/Hub/IStorageHub.cs` | new | hub surface + DTOs |
| `src/Cortex.Contained.Contracts/Hub/IAgentHub.cs` | mod (T11) | compose `IStorageHub` |
| `src/Cortex.Contained.Agent.Host/Storage/StorageKeyRing.cs` | mod | raw purpose keys, fingerprint, expected-fingerprint guard |
| `src/Cortex.Contained.Agent.Host/Storage/StorageFileCipher.cs` | new | AES-GCM file format (`IContentEncryptor`) |
| `src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionMarker.cs` | new | the one-way marker |
| `src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionContext.cs` | new | `StorageLayout` + mode resolution |
| `src/Cortex.Contained.Agent.Host/Storage/MemoryContentMigrator.cs` | new | crash-safe content migration |
| `src/Cortex.Contained.Agent.Host/Storage/EncryptedMemoryStore.cs` | new | encrypted `IMemoryStore` + `IMemoryAtRest` |
| `src/Cortex.Contained.Agent.Host/Storage/EncryptedStorageOpener.cs` | new | open/migrate when the key arrives |
| `src/Cortex.Contained.Agent.Host/Storage/StorageInventory.cs` | new | header-only inventory, `IDiskSpace` |
| `src/Cortex.Contained.Agent.Host/Storage/SecureErase.cs` | new | overwrite-then-delete |
| `src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionService.cs` | new | status/preview/enable/restart, `StorageHealth` |
| `src/Cortex.Contained.Agent.Host/Storage/EvaluationGuard.cs` | new | eval CLIs refuse while encrypted |
| `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Storage.cs` | new | `IStorageHub` adapter |
| `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs` | mod | ctor param, log scrub |
| `src/Cortex.Contained.Agent.Host/Hubs/AgentProcessingService.cs` | mod | encrypted snapshot, deferred restore |
| `src/Cortex.Contained.Agent.Host/Agent/SessionSnapshot.cs` | mod | cipher-aware save/restore, overwrite plaintext |
| `src/Cortex.Contained.Agent.Host/Memory/MemoryManagementService.cs` | mod | reads through `IMemoryAtRest` |
| `src/Cortex.Contained.Agent.Host/Memory/MemoryConsolidationService.cs`, `MemoryCompactionService.cs`, `MemoryExtractionService.cs` | mod | log scrub |
| `src/Cortex.Contained.Agent.Host/Program.cs` | mod | wiring, CLI guard, `/health` |
| `src/Cortex.Contained.Channels.Voice/VoiceChannel.cs` | mod | log scrub |
| `lib/memory-mcp/src/MemoryMcp.Core/Storage/SqliteVecMemoryStore.cs` | mod (submodule) | atomic content writes |
| `src/Cortex.Contained.Bridge/Security/SecretManager.cs` | mod | `TryGet`/`Replace` key, export timestamps |
| `src/Cortex.Contained.Bridge/Security/StorageRecoveryKit.cs` | new | kit create/open |
| `src/Cortex.Contained.Bridge/Hosting/StorageKeyPushPolicy.cs` | new | never mint over encrypted data |
| `src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs` | mod | uses the policy |
| `src/Cortex.Contained.Bridge/Hub/HubClient.Storage.cs` | new | hub calls |
| `src/Cortex.Contained.Bridge/Storage/IStorageAgentClient.cs` | new | test seam |
| `src/Cortex.Contained.Bridge/Storage/BridgeStorageReport.cs` | new | Bridge store states |
| `src/Cortex.Contained.Bridge/Storage/StorageEndpoints.cs` | new | the five endpoints |
| `src/Cortex.Contained.Bridge/Auth/SessionManager.cs` | mod | `VerifyPassword` |
| `src/Cortex.Contained.Bridge/Program.cs` | mod | map endpoints |
| `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-storage.js` | new | Storage page |
| `src/Cortex.Contained.Bridge/wwwroot/app.html`, `js/components/layout.js` | mod | page, tab, sidebar, route |
| `docs/encryption-at-rest.md` | new | owner guide |
| `docs/security.md`, `docs/event-log.md`, `docs/architecture.md`, `docs/api-reference.md` | mod | docs |
| tests (listed per task) | new/mod | — |

## Task overview

| # | Task | Project(s) |
|---|---|---|
| 1 | Contracts: key fingerprint, SQLite header classification, storage hub DTOs | Contracts |
| 2 | Key ring: purpose keys, fingerprint, expected-fingerprint guard | Agent |
| 3 | `StorageFileCipher` (AES-256-GCM file format) | Agent |
| 4 | Marker, layout, effective mode resolution + wiring | Agent |
| 5 | memory-mcp: atomic content writes (submodule) | memory-mcp |
| 6 | `MemoryContentMigrator` (crash-safe, overwrite plaintext) | Agent |
| 7 | `EncryptedMemoryStore` (lazy, fail closed, migrate) | Agent |
| 8 | Direct memory readers + `EncryptedStorageOpener` + wiring | Agent |
| 9 | Encrypted session snapshot, deferred restore | Agent |
| 10 | `StorageInventory` + disk space | Agent |
| 11 | `StorageEncryptionService`, `IStorageHub`, `/health` | Agent, Contracts |
| 12 | Evaluation CLI guard + engine secure-delete guard | Agent |
| 13 | Log scrub + log-template hygiene tests | Agent, Voice, Bridge tests |
| 14 | Bridge key custody (`TryGet`, `Replace`, export timestamps) | Bridge |
| 15 | `StorageRecoveryKit` | Bridge |
| 16 | `HubClient.Storage` + key push policy | Bridge |
| 17 | `IStorageAgentClient`, `BridgeStorageReport`, `VerifyPassword` | Bridge |
| 18 | `StorageEndpoints` | Bridge |
| 19 | Storage page (UI) | Bridge UI |
| 20 | End-to-end integration test | Integration tests |
| 21 | Documentation | docs |
| 22 | Final verification | all |

---

### Task 1: Contracts — key fingerprint, SQLite header classification, storage hub DTOs

**Files:**
- Create: `src/Cortex.Contained.Contracts/Security/StorageKeyFingerprint.cs`
- Create: `src/Cortex.Contained.Contracts/Security/SqliteFileHeader.cs`
- Create: `src/Cortex.Contained.Contracts/Hub/IStorageHub.cs`
- Test: `tests/Cortex.Contained.Contracts.Tests/Security/StorageEncryptionContractsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Contracts.Tests.Security;

public sealed class StorageEncryptionContractsTests : IDisposable
{
    private static readonly byte[] KeyA = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(2, 32).Select(i => (byte)i).ToArray();

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"sqlite-header-{Guid.NewGuid():N}");

    public StorageEncryptionContractsTests()
    {
        Directory.CreateDirectory(this.dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Fingerprint_IsStableHexAndKeySpecific()
    {
        var a = StorageKeyFingerprint.Compute(KeyA);

        Assert.Equal(a, StorageKeyFingerprint.Compute(Convert.ToBase64String(KeyA)));
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.NotEqual(a, StorageKeyFingerprint.Compute(KeyB));
        Assert.DoesNotContain(Convert.ToHexStringLower(KeyA)[..16], a, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_DiffersFromEveryStoragePurposeDerivation()
    {
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, KeyA, 32, info: Encoding.UTF8.GetBytes("cortex-storage/messages.db"));

        Assert.False(Convert.ToHexStringLower(derived).StartsWith(StorageKeyFingerprint.Compute(KeyA), StringComparison.Ordinal));
    }

    [Fact]
    public void Fingerprint_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => StorageKeyFingerprint.Compute(new byte[16]));
    }

    [Fact]
    public void Display_GroupsOfFour()
    {
        Assert.Equal("0123-4567-89ab-cdef", StorageKeyFingerprint.Display("0123456789abcdef"));
    }

    [Fact]
    public void Classify_ByHeader()
    {
        var plain = Path.Combine(this.dir, "plain.db");
        var encrypted = Path.Combine(this.dir, "enc.db");
        var empty = Path.Combine(this.dir, "empty.db");
        File.WriteAllBytes(plain, [.. "SQLite format 3\0"u8.ToArray(), .. new byte[100]]);
        File.WriteAllBytes(encrypted, RandomNumberGenerator.GetBytes(4096));
        File.WriteAllBytes(empty, []);

        Assert.Equal(StoredFileState.Plaintext, SqliteFileHeader.Classify(plain));
        Assert.Equal(StoredFileState.Encrypted, SqliteFileHeader.Classify(encrypted));
        Assert.Equal(StoredFileState.Empty, SqliteFileHeader.Classify(empty));
        Assert.Equal(StoredFileState.Absent, SqliteFileHeader.Classify(Path.Combine(this.dir, "none.db")));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Contracts.Tests --filter "FullyQualifiedName~StorageEncryptionContractsTests" -v q`
Expected: build FAILS — `The type or namespace name 'StorageKeyFingerprint' could not be found`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Contracts/Security/StorageKeyFingerprint.cs`:

```csharp
using System.Security.Cryptography;

namespace Cortex.Contained.Contracts.Security;

/// <summary>
/// The non-secret identifier of a tenant storage master key: the first 8 bytes of
/// HKDF-SHA256(master, info <c>cortex-storage/key-check</c>) as 16 lowercase hex characters. The Bridge,
/// the agent and the recovery kit compare it to tell "the same key" from "another key" without revealing
/// the key itself.
/// </summary>
public static class StorageKeyFingerprint
{
    /// <summary>Required master key length in bytes.</summary>
    public const int KeyBytes = 32;

    /// <summary>The fingerprint of <paramref name="masterKey"/>.</summary>
    public static string Compute(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != KeyBytes)
        {
            throw new ArgumentException($"A storage key has exactly {KeyBytes} bytes.", nameof(masterKey));
        }

        Span<byte> check = stackalloc byte[8];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, check, ReadOnlySpan<byte>.Empty, "cortex-storage/key-check"u8);
        return Convert.ToHexStringLower(check);
    }

    /// <summary>The fingerprint of a Base64 master key.</summary>
    public static string Compute(string base64Key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        var key = Convert.FromBase64String(base64Key);
        try
        {
            return Compute(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Groups of four for display (<c>0123-4567-89ab-cdef</c>).</summary>
    public static string Display(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return string.Join('-', Enumerable.Range(0, (fingerprint.Length + 3) / 4)
            .Select(i => fingerprint.Substring(i * 4, Math.Min(4, fingerprint.Length - (i * 4)))));
    }
}
```

`src/Cortex.Contained.Contracts/Security/SqliteFileHeader.cs`:

```csharp
namespace Cortex.Contained.Contracts.Security;

/// <summary>What the first bytes of a stored file say about it.</summary>
public enum StoredFileState
{
    /// <summary>No such file.</summary>
    Absent,

    /// <summary>The file exists but is empty (no content either way).</summary>
    Empty,

    /// <summary>A readable plaintext file (a SQLite file starting with <c>SQLite format 3\0</c>).</summary>
    Plaintext,

    /// <summary>Anything else: an encrypted SQLite file has a random-looking first page.</summary>
    Encrypted,
}

/// <summary>
/// Classifies SQLite database files by their header without opening them (no key needed, nothing is
/// created). SQLite3 Multiple Ciphers encrypts page 1 including the header, so a plaintext database is
/// exactly one that starts with the SQLite magic string.
/// </summary>
public static class SqliteFileHeader
{
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();

    /// <summary>The state of the database file at <paramref name="path"/>.</summary>
    public static StoredFileState Classify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return StoredFileState.Absent;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[16];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read == 0)
        {
            return StoredFileState.Empty;
        }

        return read == header.Length && header.SequenceEqual(Magic) ? StoredFileState.Plaintext : StoredFileState.Encrypted;
    }
}
```

`src/Cortex.Contained.Contracts/Hub/IStorageHub.cs` (the interface is *not* composed into `IAgentHub` yet — Task 11):

```csharp
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
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Contracts.Tests --filter "FullyQualifiedName~StorageEncryptionContractsTests" -v q`
Expected: `Passed!  - Failed: 0, Passed: 5`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Contracts/Security/StorageKeyFingerprint.cs src/Cortex.Contained.Contracts/Security/SqliteFileHeader.cs src/Cortex.Contained.Contracts/Hub/IStorageHub.cs tests/Cortex.Contained.Contracts.Tests/Security/StorageEncryptionContractsTests.cs
git commit -m "feat(contracts): storage key fingerprint, SQLite header check and the storage hub DTOs" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: Key ring — purpose keys, fingerprint, expected-fingerprint guard

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Storage/StorageKeyRing.cs` (whole file below)
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Events/StorageKeyRingTests.cs`

- [ ] **Step 1: Write the failing tests** — add `using Cortex.Contained.Contracts.Security;` at the top of `StorageKeyRingTests.cs` and append inside the class:

```csharp
    [Fact]
    public void Provide_KeyOfAnotherFingerprintThanTheData_RejectedBeforeAcceptance()
    {
        var ring = new StorageKeyRing { ExpectedFingerprint = StorageKeyFingerprint.Compute(KeyA) };

        Assert.Equal(StorageKeyAcceptance.Rejected, ring.Provide(KeyB));
        Assert.True(ring.RejectedForData);
        Assert.False(ring.IsAvailable);
        Assert.Equal(StorageKeyAcceptance.Accepted, ring.Provide(KeyA));
        Assert.Equal(StorageKeyFingerprint.Compute(KeyA), ring.Fingerprint);
    }

    [Fact]
    public void KeyFor_IsPurposeBoundAndMatchesThePassword()
    {
        var ring = new StorageKeyRing();
        Assert.Throws<StorageLockedException>(() => ring.KeyFor("memory-content"));
        ring.Provide(KeyA);

        var content = ring.KeyFor("memory-content");

        Assert.Equal(32, content.Length);
        Assert.NotEqual(content, ring.KeyFor("sessions-snapshot"));
        Assert.Equal(ring.PasswordFor("memory-content"), Convert.ToHexStringLower(content));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~StorageKeyRingTests" -v q`
Expected: build FAILS — `'StorageKeyRing' does not contain a definition for 'ExpectedFingerprint'`.

- [ ] **Step 3: Implement** — replace `src/Cortex.Contained.Agent.Host/Storage/StorageKeyRing.cs` with:

```csharp
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Outcome of <see cref="StorageKeyRing.Provide"/>.</summary>
public enum StorageKeyAcceptance
{
    /// <summary>First key of this process; storage can open.</summary>
    Accepted,

    /// <summary>The same key again (reconnect, credential refresh).</summary>
    Unchanged,

    /// <summary>Malformed, or different from the key already in use (never switched at runtime).</summary>
    Rejected,
}

/// <summary>
/// Holds the storage master key pushed by the Bridge (D4) in memory and derives one SQLite3
/// Multiple Ciphers passphrase per database (HKDF-SHA256, purpose-bound). Fail-closed: until the
/// key arrives nothing can open an encrypted database, and a different key is never accepted.
/// </summary>
public sealed class StorageKeyRing
{
    /// <summary>Required master key length in bytes.</summary>
    public const int KeyBytes = 32;

    private readonly TaskCompletionSource<byte[]> master = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock sync = new();
    private string? expectedFingerprint;

    /// <summary>True once a key has been accepted.</summary>
    public bool IsAvailable => this.master.Task.IsCompletedSuccessfully;

    /// <summary>True after a key was refused because its fingerprint is not the one the encrypted data belongs to.</summary>
    public bool RejectedForData { get; private set; }

    /// <summary>Fingerprint of the accepted key (never the key itself), or null before one arrived.</summary>
    public string? Fingerprint => this.IsAvailable ? StorageKeyFingerprint.Compute(this.master.Task.Result) : null;

    /// <summary>
    /// The fingerprint recorded when encryption was enabled (the marker). A key with another fingerprint
    /// is rejected before anything tries to open the data with it.
    /// </summary>
    public string? ExpectedFingerprint
    {
        get => this.expectedFingerprint;
        set => this.expectedFingerprint = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Offers the Base64 master key; the first valid key wins for the process lifetime.</summary>
    public StorageKeyAcceptance Provide(string base64Key)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException)
        {
            return StorageKeyAcceptance.Rejected;
        }

        if (key.Length != KeyBytes)
        {
            return StorageKeyAcceptance.Rejected;
        }

        if (this.expectedFingerprint is { } expected
            && !string.Equals(StorageKeyFingerprint.Compute(key), expected, StringComparison.Ordinal))
        {
            this.RejectedForData = true;
            return StorageKeyAcceptance.Rejected;
        }

        lock (this.sync)
        {
            if (this.master.TrySetResult(key))
            {
                return StorageKeyAcceptance.Accepted;
            }

            return CryptographicOperations.FixedTimeEquals(this.master.Task.Result, key)
                ? StorageKeyAcceptance.Unchanged
                : StorageKeyAcceptance.Rejected;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the key; throws <see cref="TimeoutException"/> otherwise.</summary>
    public async Task<string> WaitForPasswordAsync(string purpose, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var key = await this.master.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        return DerivePassword(key, purpose);
    }

    /// <summary>Waits for the key without a time limit (background services).</summary>
    public Task WhenAvailableAsync(CancellationToken cancellationToken) => this.master.Task.WaitAsync(cancellationToken);

    /// <summary>The passphrase for <paramref name="purpose"/>; throws <see cref="StorageLockedException"/> before the key arrived.</summary>
    public string PasswordFor(string purpose) =>
        this.IsAvailable
            ? DerivePassword(this.master.Task.Result, purpose)
            : throw new StorageLockedException($"The storage key for {purpose} has not been provided by the Bridge yet.");

    /// <summary>The raw 256-bit key for <paramref name="purpose"/>; throws <see cref="StorageLockedException"/> before the key arrived.</summary>
    public byte[] KeyFor(string purpose) =>
        this.IsAvailable
            ? DeriveKey(this.master.Task.Result, purpose)
            : throw new StorageLockedException($"The storage key for {purpose} has not been provided by the Bridge yet.");

    /// <summary>Waits up to <paramref name="timeout"/> for the key and returns the raw key for <paramref name="purpose"/>.</summary>
    public async Task<byte[]> WaitForKeyAsync(string purpose, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var key = await this.master.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        return DeriveKey(key, purpose);
    }

    /// <summary>Deterministic, purpose-bound passphrase (64 hex characters).</summary>
    public static string DerivePassword(byte[] masterKey, string purpose)
    {
        var derived = DeriveKey(masterKey, purpose);
        try
        {
            return Convert.ToHexStringLower(derived);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    /// <summary>Deterministic, purpose-bound 256-bit key: HKDF-SHA256(master, info <c>cortex-storage/{purpose}</c>).</summary>
    public static byte[] DeriveKey(byte[] masterKey, string purpose)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, KeyBytes, info: Encoding.UTF8.GetBytes("cortex-storage/" + purpose));
    }
}

/// <summary>Encrypted storage cannot open: the Bridge has not provided the storage key.</summary>
public sealed class StorageLockedException : Exception
{
    public StorageLockedException()
    {
    }

    public StorageLockedException(string message)
        : base(message)
    {
    }

    public StorageLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~StorageKeyRingTests|FullyQualifiedName~MessageStoreEncryptionTests|FullyQualifiedName~HistoryIndexEncryptionTests" -v q`
Expected: all pass (`StorageKeyRingTests` 7). The passphrases are unchanged, so existing encrypted stores still open.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Storage/StorageKeyRing.cs tests/Cortex.Contained.Agent.Host.Tests/Events/StorageKeyRingTests.cs
git commit -m "feat(storage): purpose keys, key fingerprint and the expected-fingerprint guard in the key ring" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: `StorageFileCipher` (AES-256-GCM file format)

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Storage/StorageFileCipher.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Storage/StorageFileCipherTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

public sealed class StorageFileCipherTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherKey = Enumerable.Range(2, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Encrypt_RoundTripsWithAFreshNonceAndNoPlaintext()
    {
        var cipher = new StorageFileCipher(Key, "memory-content");
        var plaintext = Encoding.UTF8.GetBytes("zqfilemarker secret");

        var a = cipher.Encrypt(plaintext);
        var b = cipher.Encrypt(plaintext);

        Assert.True(StorageFileCipher.IsEncrypted(a));
        Assert.NotEqual(a, b);
        Assert.Equal(-1, a.AsSpan().IndexOf("zqfilemarker"u8));
        Assert.Equal(plaintext, cipher.Decrypt(a));
    }

    [Fact]
    public void Decrypt_WrongKeyOrLabelOrTamperedByte_FailsClosed()
    {
        var blob = new StorageFileCipher(Key, "memory-content").Encrypt("x"u8.ToArray());
        var tampered = (byte[])blob.Clone();
        tampered[^1] ^= 1;

        Assert.ThrowsAny<CryptographicException>(() => new StorageFileCipher(OtherKey, "memory-content").Decrypt(blob));
        Assert.ThrowsAny<CryptographicException>(() => new StorageFileCipher(Key, "sessions-snapshot").Decrypt(blob));
        Assert.ThrowsAny<CryptographicException>(() => new StorageFileCipher(Key, "memory-content").Decrypt(tampered));
    }

    [Fact]
    public void Decrypt_Plaintext_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => new StorageFileCipher(Key, "memory-content").Decrypt("plain text"u8.ToArray()));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~StorageFileCipherTests" -v q`
Expected: build FAILS — `The type or namespace name 'StorageFileCipher' could not be found`.

- [ ] **Step 3: Implement** `src/Cortex.Contained.Agent.Host/Storage/StorageFileCipher.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using MemoryMcp.Core.Security;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Authenticated file encryption with a key derived from the tenant storage key (D4): AES-256-GCM with a
/// fresh random 96-bit nonce per write. Layout: <c>"CXE1"</c> magic (4 bytes), nonce (12), tag (16),
/// ciphertext. The associated data binds every blob to its purpose label, so a memory content file and a
/// session snapshot cannot be swapped for each other. Thread-safe.
/// </summary>
public sealed class StorageFileCipher : IContentEncryptor
{
    /// <summary>Bytes before the ciphertext.</summary>
    public const int HeaderSize = 4 + NonceSize + TagSize;

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] key;
    private readonly byte[] associatedData;

    /// <param name="key">The 256-bit key for <paramref name="label"/> (<see cref="StorageKeyRing.KeyFor"/>).</param>
    /// <param name="label">The purpose the files belong to, e.g. <c>memory-content</c>.</param>
    public StorageFileCipher(byte[] key, string label)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (key.Length != StorageKeyRing.KeyBytes)
        {
            throw new ArgumentException($"The key must have {StorageKeyRing.KeyBytes} bytes.", nameof(key));
        }

        this.key = (byte[])key.Clone();
        this.associatedData = Encoding.UTF8.GetBytes("cortex-storage-file/1\n" + label);
    }

    /// <summary>The magic prefix of an encrypted file.</summary>
    public static ReadOnlySpan<byte> Magic => "CXE1"u8;

    /// <summary>True when <paramref name="content"/> starts with the encrypted-file magic.</summary>
    public static bool IsEncrypted(ReadOnlySpan<byte> content) => content.Length >= HeaderSize && content.StartsWith(Magic);

    /// <summary>True when the file at <paramref name="path"/> starts with the encrypted-file magic.</summary>
    public static bool IsEncryptedFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> prefix = stackalloc byte[HeaderSize];
        var read = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        return IsEncrypted(prefix[..read]);
    }

    /// <inheritdoc />
    public byte[] Encrypt(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var blob = new byte[HeaderSize + plaintext.Length];
        Magic.CopyTo(blob);
        var nonce = blob.AsSpan(4, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(this.key, TagSize);
        aes.Encrypt(nonce, plaintext, blob.AsSpan(HeaderSize), blob.AsSpan(4 + NonceSize, TagSize), this.associatedData);
        return blob;
    }

    /// <summary>
    /// Decrypts a blob written by <see cref="Encrypt"/>. A blob without the magic throws
    /// <see cref="InvalidDataException"/> (plaintext is never served as if it were authenticated); a wrong key
    /// or any modified byte throws <see cref="AuthenticationTagMismatchException"/>.
    /// </summary>
    public byte[] Decrypt(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!IsEncrypted(ciphertext))
        {
            throw new InvalidDataException("The file is not encrypted with the storage key.");
        }

        var plaintext = new byte[ciphertext.Length - HeaderSize];
        using var aes = new AesGcm(this.key, TagSize);
        aes.Decrypt(
            ciphertext.AsSpan(4, NonceSize),
            ciphertext.AsSpan(HeaderSize),
            ciphertext.AsSpan(4 + NonceSize, TagSize),
            plaintext,
            this.associatedData);
        return plaintext;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~StorageFileCipherTests" -v q`
Expected: `Passed: 3`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Storage/StorageFileCipher.cs tests/Cortex.Contained.Agent.Host.Tests/Storage/StorageFileCipherTests.cs
git commit -m "feat(storage): AES-256-GCM file cipher bound to a purpose label" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: Marker, layout, effective mode resolution + wiring

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionMarker.cs`
- Create: `src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionContext.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs` (message store, key ring and index registrations)
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Storage/StorageEncryptionContextTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Security.Cryptography;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

public sealed class StorageEncryptionContextTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"enc-context-{Guid.NewGuid():N}");
    private readonly StorageLayout layout;

    public StorageEncryptionContextTests()
    {
        Directory.CreateDirectory(this.root);
        this.layout = new StorageLayout(
            Path.Combine(this.root, "state"),
            Path.Combine(this.root, "data"),
            Path.Combine(this.root, "state", "memory", "memory.db"),
            Path.Combine(this.root, "state", "memory", "memories"));
        Directory.CreateDirectory(this.layout.MemoriesDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Resolve_NothingConfiguredNothingEncrypted_Off()
    {
        var context = StorageEncryptionContext.Resolve(new StorageOptions(), this.layout);

        Assert.False(context.Enabled);
        Assert.Equal("off", context.Source);
        Assert.False(context.ProtectsNewWrites);
    }

    [Fact]
    public void Resolve_Config_On()
    {
        var context = StorageEncryptionContext.Resolve(new StorageOptions { EncryptAtRest = true }, this.layout);

        Assert.True(context.Enabled);
        Assert.Equal("config", context.Source);
    }

    [Fact]
    public void Resolve_Marker_WinsOverConfigOff()
    {
        new StorageEncryptionMarker { KeyFingerprint = "0123456789abcdef" }.Write(this.layout.Marker);

        var context = StorageEncryptionContext.Resolve(new StorageOptions { EncryptAtRest = false }, this.layout);

        Assert.True(context.Enabled);
        Assert.Equal("marker", context.Source);
        Assert.Equal("0123456789abcdef", context.Marker!.KeyFingerprint);
    }

    [Fact]
    public void Resolve_UnreadableMarker_StaysEncrypted()
    {
        File.WriteAllText(this.layout.Marker, "{ not json");

        var context = StorageEncryptionContext.Resolve(new StorageOptions(), this.layout);

        Assert.True(context.Enabled);
        Assert.Equal(string.Empty, context.Marker!.KeyFingerprint);
    }

    [Fact]
    public void Resolve_EncryptedMessagesDbWithoutMarker_Detected()
    {
        File.WriteAllBytes(this.layout.MessagesDatabase, RandomNumberGenerator.GetBytes(4096));

        var context = StorageEncryptionContext.Resolve(new StorageOptions(), this.layout);

        Assert.True(context.Enabled);
        Assert.Equal("detected", context.Source);
    }

    [Fact]
    public void Resolve_EncryptedMemoryFileWithoutMarker_Detected()
    {
        var cipher = new StorageFileCipher(RandomNumberGenerator.GetBytes(32), "memory-content");
        File.WriteAllBytes(Path.Combine(this.layout.MemoriesDirectory, "a.memory.data"), cipher.Encrypt("x"u8.ToArray()));

        Assert.Equal("detected", StorageEncryptionContext.Resolve(new StorageOptions(), this.layout).Source);
    }

    [Fact]
    public void MarkRestartPending_PersistsMarkerAndProtectsNewWrites()
    {
        var context = StorageEncryptionContext.Resolve(new StorageOptions(), this.layout);

        context.MarkRestartPending(new StorageEncryptionMarker { KeyFingerprint = "fp", EnabledAt = DateTimeOffset.UnixEpoch });

        Assert.False(context.Enabled);
        Assert.True(context.RestartPending);
        Assert.True(context.ProtectsNewWrites);
        Assert.Equal("fp", StorageEncryptionMarker.Read(this.layout.Marker)!.KeyFingerprint);
        Assert.False(File.Exists(this.layout.Marker + ".tmp"));
    }

    [Fact]
    public void EnsureMarker_DetectedWithoutMarker_WritesTheFingerprintOnce()
    {
        File.WriteAllBytes(this.layout.MessagesDatabase, RandomNumberGenerator.GetBytes(4096));
        var context = StorageEncryptionContext.Resolve(new StorageOptions(), this.layout);

        Assert.True(context.EnsureMarker("fp1", DateTimeOffset.UnixEpoch));
        Assert.False(context.EnsureMarker("fp2", DateTimeOffset.UnixEpoch));
        Assert.Equal("fp1", StorageEncryptionMarker.Read(this.layout.Marker)!.KeyFingerprint);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~StorageEncryptionContextTests" -v q`
Expected: build FAILS — `The type or namespace name 'StorageLayout' could not be found`.

- [ ] **Step 3: Implement** `src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionMarker.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// The persisted decision to encrypt the agent's stores (<c>&lt;state&gt;/storage-encryption.json</c>), written
/// when the owner enables encryption from the Bridge. It lives next to the data, so the Launcher's compose
/// sync, image upgrades and Self-Update never lose it, and it is one-way: nothing in the product removes it.
/// </summary>
public sealed record StorageEncryptionMarker
{
    /// <summary>File name inside the state root.</summary>
    public const string FileName = "storage-encryption.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Format version.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Always true in a written marker.</summary>
    public bool EncryptAtRest { get; init; } = true;

    /// <summary>Fingerprint of the key the data is encrypted with (empty when unknown).</summary>
    public string KeyFingerprint { get; init; } = string.Empty;

    /// <summary>When encryption was enabled.</summary>
    public DateTimeOffset? EnabledAt { get; init; }

    /// <summary>
    /// Reads the marker. Missing file: null. A file that exists but cannot be read means encryption is on
    /// with an unknown fingerprint (fail closed: never fall back to plaintext).
    /// </summary>
    public static StorageEncryptionMarker? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<StorageEncryptionMarker>(File.ReadAllText(path), Json);
            return marker is { EncryptAtRest: true } ? marker : new StorageEncryptionMarker();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new StorageEncryptionMarker();
        }
    }

    /// <summary>Writes the marker atomically and durably (temp file, flush to disk, rename).</summary>
    public void Write(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, this, Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
```

`src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionContext.cs`:

```csharp
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

    /// <summary>True when any covered store already holds encrypted bytes.</summary>
    public static bool HasEncryptedData(StorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (SqliteFileHeader.Classify(layout.MessagesDatabase) == StoredFileState.Encrypted
            || SqliteFileHeader.Classify(layout.MemoryDatabasePath) == StoredFileState.Encrypted)
        {
            return true;
        }

        if (File.Exists(layout.SessionsSnapshot) && StorageFileCipher.IsEncryptedFile(layout.SessionsSnapshot))
        {
            return true;
        }

        return Directory.Exists(layout.MemoriesDirectory)
            && Directory.EnumerateFiles(layout.MemoriesDirectory, "*.memory.data").Any(StorageFileCipher.IsEncryptedFile);
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
```

Wire it in `src/Cortex.Contained.Agent.Host/Program.cs`. Replace:

```csharp
// --- Message Store (per-tenant SQLite persistence; encrypted at rest when Storage:EncryptAtRest, D4) ---
var storageOptions = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
builder.Services.AddSingleton(sp =>
    new Cortex.Contained.Agent.Host.Storage.MessageStore(
        Path.Combine(stateRoot, "messages.db"),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Cortex.Contained.Agent.Host.Storage.MessageStore>(),
        storageOptions.EncryptAtRest
            ? new MessageStoreEncryption(sp.GetRequiredService<StorageKeyRing>(), TimeSpan.FromSeconds(storageOptions.KeyWaitSeconds))
            : null));
```

with:

```csharp
// --- Message Store (per-tenant SQLite persistence; encrypted at rest when Storage:EncryptAtRest, D4) ---
var storageOptions = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
// Encryption at rest is on when configured, when the owner enabled it (marker in the state volume) or when
// encrypted files already exist: a reset configuration can never make the agent write plaintext again.
var storageEncryption = StorageEncryptionContext.Resolve(
    storageOptions,
    StorageLayout.From(stateRoot, sandboxRoot, builder.Configuration.GetSection(MemoryMcpOptions.SectionName).Get<MemoryMcpOptions>() ?? new MemoryMcpOptions()));
builder.Services.AddSingleton(storageEncryption);
builder.Services.AddSingleton(sp => new Cortex.Contained.Agent.Host.Storage.StorageKeyRing
{
    ExpectedFingerprint = storageEncryption.Marker?.KeyFingerprint,
});
builder.Services.AddSingleton(sp =>
    new Cortex.Contained.Agent.Host.Storage.MessageStore(
        Path.Combine(stateRoot, "messages.db"),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Cortex.Contained.Agent.Host.Storage.MessageStore>(),
        storageEncryption.Enabled
            ? new MessageStoreEncryption(sp.GetRequiredService<StorageKeyRing>(), TimeSpan.FromSeconds(storageOptions.KeyWaitSeconds))
            : null));
```

In the `HistoryIndexGenerations` registration replace `storageOptions.EncryptAtRest ? sp.GetRequiredService<StorageKeyRing>() : null,` with `storageEncryption.Enabled ? sp.GetRequiredService<StorageKeyRing>() : null,`. Then **delete** the now-duplicate line under "Event log and provenance":

```csharp
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Storage.StorageKeyRing>();
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~StorageEncryptionContextTests|FullyQualifiedName~MessageStoreEncryptionTests|FullyQualifiedName~StorageReadinessTests" -v q`
Expected: all pass (`StorageEncryptionContextTests` 8). The build has 0 warnings.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionMarker.cs src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionContext.cs src/Cortex.Contained.Agent.Host/Program.cs tests/Cortex.Contained.Agent.Host.Tests/Storage/StorageEncryptionContextTests.cs
git commit -m "feat(storage): one-way encryption marker; encryption is on by config, marker or detected ciphertext" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: memory-mcp — atomic content writes (submodule)

**Files (in `lib/memory-mcp`):**
- Modify: `src/MemoryMcp.Core/Storage/SqliteVecMemoryStore.cs` (`InitializeAsync`, `WriteContentFileAsync`)
- Test: `tests/MemoryMcp.Core.Tests/Storage/SqliteVecMemoryStoreTests.cs`
- Parent: `lib/memory-mcp` (pointer bump)

- [ ] **Step 1: Branch the submodule from the pinned commit**

```powershell
cd lib\memory-mcp
git status --short          # must be clean
git checkout -b feat/encryption-at-rest 4150f7b
```

- [ ] **Step 2: Write the failing tests** — append inside `SqliteVecMemoryStoreTests`:

```csharp
    [Fact]
    public async Task StoreMemoryAsync_WritesThroughATempFileThatIsGoneAfterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        await this.store.InitializeAsync(ct);
        var memoryId = Guid.NewGuid().ToString();
        var (chunks, vectors) = CreateTestChunks(memoryId);

        await this.store.StoreMemoryAsync(memoryId, "atomic content", chunks, vectors, ct);

        var memories = Path.Combine(this.tempDir, "memories");
        Assert.Equal("atomic content", await File.ReadAllTextAsync(Path.Combine(memories, $"{memoryId}.memory.data"), ct));
        Assert.Empty(Directory.EnumerateFiles(memories, "*.tmp"));
    }

    [Fact]
    public async Task InitializeAsync_TornTempFileFromACrash_IsRemovedAndTheContentFileKept()
    {
        var ct = TestContext.Current.CancellationToken;
        await this.store.InitializeAsync(ct);
        var memoryId = Guid.NewGuid().ToString();
        var (chunks, vectors) = CreateTestChunks(memoryId);
        await this.store.StoreMemoryAsync(memoryId, "kept content", chunks, vectors, ct);
        var contentPath = Path.Combine(this.tempDir, "memories", $"{memoryId}.memory.data");
        await File.WriteAllTextAsync(contentPath + ".tmp", "torn", ct);

        await this.store.InitializeAsync(ct);

        Assert.False(File.Exists(contentPath + ".tmp"));
        Assert.Equal("kept content", (await this.store.GetMemoryAsync(memoryId, ct))!.Content);
    }
```

- [ ] **Step 3: Run to verify**

Run: `dotnet test tests\MemoryMcp.Core.Tests --filter "FullyQualifiedName~SqliteVecMemoryStoreTests" -v q`
Expected: `InitializeAsync_TornTempFileFromACrash_IsRemovedAndTheContentFileKept` FAILS (`Assert.False() Failure` — the `.tmp` file is still there). The first test passes already and stays as a guard.

- [ ] **Step 4: Implement** — in `SqliteVecMemoryStore.cs`, add the clean-up call to `InitializeAsync`:

```csharp
        // Ensure directories exist
        Directory.CreateDirectory(this.options.DataDirectory);
        Directory.CreateDirectory(this.options.MemoriesDirectory);
        this.DeleteLeftoverTempFiles();
```

and replace `WriteContentFileAsync`:

```csharp
    private async Task WriteContentFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        var plainBytes = Encoding.UTF8.GetBytes(content);
        var outputBytes = this.contentEncryptor.Encrypt(plainBytes);
        await File.WriteAllBytesAsync(path, outputBytes, cancellationToken);
    }
```

with:

```csharp
    /// <summary>Suffix of the temporary file a content write goes through before it replaces the content file.</summary>
    internal const string TempSuffix = ".tmp";

    /// <summary>
    /// Writes the content file atomically: the bytes go to a temporary file that is flushed to disk and then
    /// renamed over the content file, so a crash leaves either the old or the new content, never a torn file.
    /// </summary>
    private async Task WriteContentFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        var plainBytes = Encoding.UTF8.GetBytes(content);
        var outputBytes = this.contentEncryptor.Encrypt(plainBytes);
        var temp = path + TempSuffix;
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(outputBytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>Removes temporary content files left behind by a crash mid-write (the old content file is intact).</summary>
    private void DeleteLeftoverTempFiles()
    {
        foreach (var temp in Directory.EnumerateFiles(this.options.MemoriesDirectory, "*.memory.data" + TempSuffix))
        {
            File.Delete(temp);
        }
    }
```

- [ ] **Step 5: Run the submodule suite**

Run: `dotnet test tests\MemoryMcp.Core.Tests -v q`
Expected: `Passed!  - Failed: 0, Passed: 142`. The pre-existing `xUnit1051` warnings remain, and none come from the new tests.

- [ ] **Step 6: Commit in the submodule, then bump the pointer in the parent**

```powershell
git add src/MemoryMcp.Core/Storage/SqliteVecMemoryStore.cs tests/MemoryMcp.Core.Tests/Storage/SqliteVecMemoryStoreTests.cs
git commit -m "fix(storage): write memory content atomically and drop torn temp files on start" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
cd ..\..
dotnet build src\Cortex.Contained.Agent.Host -v q   # 0 warnings
git add lib/memory-mcp
git commit -m "chore(deps): memory-mcp with atomic content writes" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: `MemoryContentMigrator` (crash-safe, overwrite plaintext)

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Storage/MemoryContentMigrator.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Storage/MemoryContentMigratorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

/// <summary>Encrypting memory content files in place: no plaintext left, crash-safe, fail closed.</summary>
[Trait("Category", "Integrity")]
public sealed class MemoryContentMigratorTests : IDisposable
{
    private static readonly byte[] KeyA = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(2, 32).Select(i => (byte)i).ToArray();

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"memcontent-{Guid.NewGuid():N}");

    public MemoryContentMigratorTests()
    {
        Directory.CreateDirectory(this.dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static StorageFileCipher Cipher(byte[] key) => new(key, "memory-content");

    private string Write(string id, string text)
    {
        var path = Path.Combine(this.dir, id + ".memory.data");
        File.WriteAllText(path, text);
        return path;
    }

    private string Read(string id, byte[] key) =>
        Encoding.UTF8.GetString(Cipher(key).Decrypt(File.ReadAllBytes(Path.Combine(this.dir, id + ".memory.data"))));

    private bool AnyFileContains(string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        return Directory.EnumerateFiles(this.dir).Any(f => File.ReadAllBytes(f).AsSpan().IndexOf(needle) >= 0);
    }

    private string HashDirectory() => string.Join(
        ",",
        Directory.EnumerateFiles(this.dir).Order(StringComparer.Ordinal).Select(f => Path.GetFileName(f) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    [Fact]
    public void Migrate_PlaintextFiles_EncryptedAndNoPlaintextLeft()
    {
        this.Write("a", "zqmemmarker one");
        this.Write("b", "zqmemmarker two");

        var result = MemoryContentMigrator.Migrate(this.dir, Cipher(KeyA));

        Assert.Equal(new MemoryContentMigrationResult(2, 0, 0), result);
        Assert.Equal("zqmemmarker one", this.Read("a", KeyA));
        Assert.False(this.AnyFileContains("zqmemmarker"));
        Assert.Empty(Directory.EnumerateFiles(this.dir, "*" + MemoryContentMigrator.MigratingSuffix));
        Assert.Equal(new MemoryContentMigrationResult(0, 2, 0), MemoryContentMigrator.Migrate(this.dir, Cipher(KeyA)));
    }

    [Theory]
    [InlineData("after-encrypted-copy")]
    [InlineData("after-plaintext-overwrite")]
    public void Migrate_CrashMidFile_CompletedOnTheNextRun(string stage)
    {
        this.Write("a", "zqcrashmarker");

        Assert.Throws<IOException>(() => MemoryContentMigrator.Migrate(
            this.dir, Cipher(KeyA), s => { if (s == stage) { throw new IOException("power loss"); } }));

        MemoryContentMigrator.Migrate(this.dir, Cipher(KeyA));

        Assert.Equal("zqcrashmarker", this.Read("a", KeyA));
        Assert.False(this.AnyFileContains("zqcrashmarker"));
        Assert.Empty(Directory.EnumerateFiles(this.dir, "*" + MemoryContentMigrator.MigratingSuffix));
    }

    [Fact]
    public void Migrate_TornEncryptedCopy_DroppedAndThePlaintextMigrated()
    {
        var path = this.Write("a", "zqtornmarker");
        File.WriteAllBytes(path + MemoryContentMigrator.MigratingSuffix, [.. StorageFileCipher.Magic, .. new byte[40]]);

        MemoryContentMigrator.Migrate(this.dir, Cipher(KeyA));

        Assert.Equal("zqtornmarker", this.Read("a", KeyA));
        Assert.False(this.AnyFileContains("zqtornmarker"));
    }

    [Fact]
    public void Migrate_WrongKey_ThrowsAndChangesNothing()
    {
        this.Write("a", "x");
        MemoryContentMigrator.Migrate(this.dir, Cipher(KeyA));
        this.Write("b", "zqlatermarker");
        var before = this.HashDirectory();

        Assert.Throws<StorageKeyMismatchException>(() => MemoryContentMigrator.Migrate(this.dir, Cipher(KeyB)));

        Assert.Equal(before, this.HashDirectory());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~MemoryContentMigratorTests" -v q`
Expected: build FAILS — `The name 'MemoryContentMigrator' does not exist`.

- [ ] **Step 3: Implement** `src/Cortex.Contained.Agent.Host/Storage/MemoryContentMigrator.cs`:

```csharp
using System.Security.Cryptography;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Counts of one <see cref="MemoryContentMigrator.Migrate"/> run.</summary>
/// <param name="Migrated">Plaintext files encrypted in this run.</param>
/// <param name="AlreadyEncrypted">Files that were encrypted already.</param>
/// <param name="Recovered">Conversions interrupted by a crash and completed in this run.</param>
public sealed record MemoryContentMigrationResult(int Migrated, int AlreadyEncrypted, int Recovered);

/// <summary>
/// Encrypts the curated memory content files (<c>*.memory.data</c>) in place, crash-safe and without
/// leaving the plaintext behind (D4). Per file: the ciphertext is written to <c>&lt;file&gt;.migrating</c> and
/// flushed, the plaintext file is overwritten with zeros and flushed, then the ciphertext is renamed over it.
/// A crash at any point is completed on the next run: a <c>.migrating</c> file that authenticates replaces
/// the original; one that does not (torn write) is dropped while the original plaintext is still intact.
/// </summary>
public static class MemoryContentMigrator
{
    /// <summary>Suffix of an encrypted copy that has not replaced its plaintext file yet.</summary>
    public const string MigratingSuffix = ".migrating";

    private const string ContentPattern = "*.memory.data";

    /// <summary>
    /// Migrates <paramref name="memoriesDirectory"/>. Throws <see cref="StorageKeyMismatchException"/> before
    /// touching anything when an encrypted file does not authenticate with <paramref name="cipher"/>.
    /// </summary>
    /// <param name="memoriesDirectory">The content directory.</param>
    /// <param name="cipher">The memory-content cipher.</param>
    /// <param name="faultInjector">Test seam: called with <c>after-encrypted-copy</c> and <c>after-plaintext-overwrite</c>.</param>
    public static MemoryContentMigrationResult Migrate(string memoriesDirectory, StorageFileCipher cipher, Action<string>? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoriesDirectory);
        ArgumentNullException.ThrowIfNull(cipher);
        if (!Directory.Exists(memoriesDirectory))
        {
            return new MemoryContentMigrationResult(0, 0, 0);
        }

        var files = Directory.EnumerateFiles(memoriesDirectory, ContentPattern)
            .Where(f => f.EndsWith(".memory.data", StringComparison.Ordinal))
            .ToList();
        var encrypted = files.Where(StorageFileCipher.IsEncryptedFile).ToList();
        if (encrypted.Count > 0 && !Authenticates(cipher, File.ReadAllBytes(encrypted[0])))
        {
            throw new StorageKeyMismatchException("The memory content files cannot be decrypted with the provided storage key.");
        }

        var recovered = 0;
        foreach (var temp in Directory.EnumerateFiles(memoriesDirectory, ContentPattern + MigratingSuffix).ToList())
        {
            var target = temp[..^MigratingSuffix.Length];
            if (Authenticates(cipher, File.ReadAllBytes(temp)))
            {
                File.Move(temp, target, overwrite: true);
                recovered++;
                continue;
            }

            if (!File.Exists(target) || IsAllZero(File.ReadAllBytes(target)))
            {
                throw new InvalidDataException($"{Path.GetFileName(target)}: the interrupted encryption left no readable copy; it is kept for manual recovery.");
            }

            File.Delete(temp);
        }

        var migrated = 0;
        var already = 0;
        foreach (var file in Directory.EnumerateFiles(memoriesDirectory, ContentPattern)
                     .Where(f => f.EndsWith(".memory.data", StringComparison.Ordinal))
                     .ToList())
        {
            var bytes = File.ReadAllBytes(file);
            try
            {
                if (StorageFileCipher.IsEncrypted(bytes))
                {
                    already++;
                    continue;
                }

                var temp = file + MigratingSuffix;
                WriteDurably(temp, cipher.Encrypt(bytes));
                faultInjector?.Invoke("after-encrypted-copy");
                WriteDurably(file, new byte[bytes.Length]);
                faultInjector?.Invoke("after-plaintext-overwrite");
                File.Move(temp, file, overwrite: true);
                migrated++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        return new MemoryContentMigrationResult(migrated, already - recovered, recovered);
    }

    private static bool Authenticates(StorageFileCipher cipher, byte[] blob)
    {
        try
        {
            CryptographicOperations.ZeroMemory(cipher.Decrypt(blob));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsAllZero(byte[] bytes) => !bytes.AsSpan().ContainsAnyExcept((byte)0);

    private static void WriteDurably(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.SetLength(bytes.Length);
        stream.Flush(flushToDisk: true);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~MemoryContentMigratorTests" -v q`
Expected: `Passed: 5`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Storage/MemoryContentMigrator.cs tests/Cortex.Contained.Agent.Host.Tests/Storage/MemoryContentMigratorTests.cs
git commit -m "feat(storage): crash-safe in-place encryption of memory content files" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 7: `EncryptedMemoryStore` (lazy, fail closed, migrate)

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Storage/EncryptedMemoryStore.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Storage/EncryptedMemoryStoreTests.cs`

- [ ] **Step 1: Write the failing integrity test**

```csharp
using System.Security.Cryptography;
using System.Text;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Security;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Retrieval;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

/// <summary>
/// The curated memory store under the tenant key: memory.db (chunks, sqlite-vec vectors, FTS5 keyword
/// index) through SQLite3 Multiple Ciphers and the content files through AES-GCM.
/// </summary>
[Trait("Category", "Integrity")]
public sealed class EncryptedMemoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"memstore-enc-{Guid.NewGuid():N}");
    private readonly MemoryMcpOptions settings;

    public EncryptedMemoryStoreTests()
    {
        this.settings = new MemoryMcpOptions
        {
            DataDirectory = this.dir,
            DatabaseFileName = "memory.db",
            Ollama = new OllamaOptions { Dimensions = 4 },
        };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static StorageKeyRing Ring(byte seed = 1)
    {
        var ring = new StorageKeyRing();
        ring.Provide(Convert.ToBase64String(Enumerable.Range(seed, 32).Select(i => (byte)i).ToArray()));
        return ring;
    }

    private static List<ChunkRecord> Chunk(string memoryId, string? title) =>
    [
        new ChunkRecord
        {
            MemoryId = memoryId,
            ChunkIndex = 0,
            StartOffset = 0,
            Length = 10,
            Title = title,
            Tags = ["zqtagmarker"],
            CreatedAt = T0,
            UpdatedAt = T0,
        },
    ];

    private static List<float[]> Vector(float x) => [[x, 1f, 0f, 0f]];

    private EncryptedMemoryStore Encrypted(StorageKeyRing ring, TimeSpan? wait = null) =>
        new(Options.Create(this.settings), ring, wait ?? TimeSpan.FromSeconds(5), NullLoggerFactory.Instance);

    private async Task SeedPlaintextAsync()
    {
        using (var plain = new SqliteVecMemoryStore(Options.Create(this.settings), new NullContentEncryptor(), NullLogger<SqliteVecMemoryStore>.Instance))
        {
            await plain.InitializeAsync();
            await plain.StoreMemoryAsync("m1", "zqkeptmarker alpha", Chunk("m1", "zqtitlemarker"), Vector(1));
            await plain.StoreMemoryAsync("m2", "zqdeletedmarker beta", Chunk("m2", null), Vector(2));
            await plain.DeleteMemoryAsync("m2");
        }

        SqliteConnection.ClearAllPools();
    }

    private bool AnyFileContains(string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        foreach (var file in Directory.EnumerateFiles(this.dir, "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (copy.ToArray().AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private string HashAllFiles() => string.Join(
        ",",
        Directory.EnumerateFiles(this.dir, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(f => Path.GetFileName(f) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    [Fact]
    public async Task PlaintextStore_MigratedOnOpen_ServesSearchAndLeavesNoPlaintextAnywhere()
    {
        await this.SeedPlaintextAsync();
        Assert.True(this.AnyFileContains("zqkeptmarker"));

        using (var store = this.Encrypted(Ring()))
        {
            Assert.Equal("zqkeptmarker alpha", (await store.GetMemoryAsync("m1"))!.Content);
            Assert.Equal("m1", Assert.Single(await store.SearchDenseAsync([1f, 1f, 0f, 0f], 5, null, MemorySearchFilter.None)).MemoryId);
            Assert.Equal("m1", Assert.Single(await store.KeywordSearchAsync(KeywordQuery.Build("zqtitlemarker", stemmed: false)!, 5, MemorySearchFilter.None)).MemoryId);
            Assert.Equal(StorageState.Open, store.State);
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(StoredFileState.Encrypted, SqliteFileHeader.Classify(this.settings.DatabasePath));
        Assert.False(this.AnyFileContains("zqkeptmarker"));
        Assert.False(this.AnyFileContains("zqtitlemarker"));
        Assert.False(this.AnyFileContains("zqtagmarker"));
        Assert.False(this.AnyFileContains("zqdeletedmarker"));
    }

    [Fact]
    public async Task NewStore_WritesOnlyCiphertext()
    {
        using (var store = this.Encrypted(Ring()))
        {
            await store.StoreMemoryAsync("m1", "zqnewmarker", Chunk("m1", "zqnewtitle"), Vector(1));
            Assert.Equal("zqnewmarker", await store.ReadContentAsync(Path.Combine(this.settings.MemoriesDirectory, "m1.memory.data"), CancellationToken.None));
        }

        SqliteConnection.ClearAllPools();
        Assert.False(this.AnyFileContains("zqnewmarker"));
        Assert.False(this.AnyFileContains("zqnewtitle"));
        using var plain = new SqliteConnection($"Data Source={this.settings.DatabasePath};Pooling=False");
        plain.Open();
        using var cmd = plain.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks;";
        Assert.Equal(26, Assert.Throws<SqliteException>(() => cmd.ExecuteScalar()).SqliteErrorCode);
    }

    [Fact]
    public async Task WrongKey_FailsClosedAndNeverTouchesAFile()
    {
        using (var store = this.Encrypted(Ring(seed: 1)))
        {
            await store.StoreMemoryAsync("m1", "x", Chunk("m1", null), Vector(1));
        }

        SqliteConnection.ClearAllPools();
        var before = this.HashAllFiles();

        using var wrong = this.Encrypted(Ring(seed: 2));
        await Assert.ThrowsAsync<StorageKeyMismatchException>(() => wrong.GetMemoryAsync("m1"));
        await Assert.ThrowsAsync<StorageLockedException>(() => wrong.GetMemoryAsync("m1"));

        Assert.Equal(StorageState.Failed, wrong.State);
        Assert.Equal(before, this.HashAllFiles());
    }

    [Fact]
    public async Task NoKey_LockedAndNothingCreated()
    {
        using var store = this.Encrypted(new StorageKeyRing(), TimeSpan.FromMilliseconds(50));

        await store.InitializeAsync();
        await Assert.ThrowsAsync<StorageLockedException>(() => store.GetMemoryAsync("m1"));

        Assert.Equal(StorageState.AwaitingKey, store.State);
        Assert.False(File.Exists(this.settings.DatabasePath));
    }

    [Theory]
    [InlineData("before-rekey")]
    [InlineData("after-encrypted-copy")]
    [InlineData("after-plaintext-overwrite")]
    public async Task CrashMidMigration_CompletedOnTheNextOpen(string stage)
    {
        await this.SeedPlaintextAsync();

        using (var crashing = this.Encrypted(Ring()))
        {
            crashing.FaultInjector = s => { if (s == stage) { throw new IOException("power loss"); } };
            await Assert.ThrowsAsync<IOException>(() => crashing.GetMemoryAsync("m1"));
        }

        SqliteConnection.ClearAllPools();
        using (var store = this.Encrypted(Ring()))
        {
            Assert.Equal("zqkeptmarker alpha", (await store.GetMemoryAsync("m1"))!.Content);
        }

        SqliteConnection.ClearAllPools();
        Assert.False(this.AnyFileContains("zqkeptmarker"));
        Assert.False(this.AnyFileContains("zqtitlemarker"));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~EncryptedMemoryStoreTests" -v q`
Expected: build FAILS — `The type or namespace name 'EncryptedMemoryStore' could not be found`.

- [ ] **Step 3: Implement** `src/Cortex.Contained.Agent.Host/Storage/EncryptedMemoryStore.cs`:

```csharp
using System.Text;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>Read access to the curated memory files for code that bypasses <see cref="IMemoryStore"/> (memory list, quarantine times).</summary>
public interface IMemoryAtRest
{
    /// <summary>The SQLite3 Multiple Ciphers passphrase of <c>memory.db</c>, or null when the store is plaintext. Waits for the open.</summary>
    Task<string?> GetDatabasePasswordAsync(CancellationToken cancellationToken);

    /// <summary>The text of a <c>*.memory.data</c> file (decrypted when encrypted).</summary>
    Task<string> ReadContentAsync(string path, CancellationToken cancellationToken);
}

/// <summary><see cref="IMemoryAtRest"/> of an unencrypted memory store.</summary>
public sealed class PlaintextMemoryAtRest : IMemoryAtRest
{
    /// <inheritdoc />
    public Task<string?> GetDatabasePasswordAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<string> ReadContentAsync(string path, CancellationToken cancellationToken) => File.ReadAllTextAsync(path, cancellationToken);
}

/// <summary>
/// The curated memory store encrypted with the tenant key (D4): <c>memory.db</c> (chunks, sqlite-vec vectors,
/// FTS5 keyword index) through SQLite3 Multiple Ciphers and every <c>*.memory.data</c> file through
/// AES-256-GCM. Opens lazily once the Bridge has pushed the key, migrating a plaintext store in place first
/// (database, then content files). Fail closed: nothing is served or created before the key arrives, and a
/// key that does not open the data locks the store for the process lifetime without touching a byte.
/// </summary>
public sealed partial class EncryptedMemoryStore : IMemoryStore, IMemoryAtRest, IDisposable
{
    /// <summary>Purpose of the <c>memory.db</c> passphrase.</summary>
    public const string DatabasePurpose = "memory.db";

    /// <summary>Purpose (and associated-data label) of the content file key.</summary>
    public const string ContentPurpose = "memory-content";

    private readonly IOptions<MemoryMcpOptions> options;
    private readonly StorageKeyRing keys;
    private readonly TimeSpan keyWait;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<EncryptedMemoryStore> logger;
    private readonly SemaphoreSlim openGate = new(1, 1);
    private volatile SqliteVecMemoryStore? inner;
    private volatile StorageState state = StorageState.AwaitingKey;
    private string? password;
    private StorageFileCipher? cipher;

    public EncryptedMemoryStore(IOptions<MemoryMcpOptions> options, StorageKeyRing keys, TimeSpan keyWait, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.options = options;
        this.keys = keys;
        this.keyWait = keyWait;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory.CreateLogger<EncryptedMemoryStore>();
    }

    /// <summary>Whether the store is serving.</summary>
    public StorageState State => this.state;

    /// <summary>Test seam for the content migration (<see cref="MemoryContentMigrator.Migrate"/>) and <c>before-rekey</c> of the database.</summary>
    internal Action<string>? FaultInjector { get; set; }

    /// <inheritdoc />
    public string? KeywordIndexUnavailableReason =>
        this.inner?.KeywordIndexUnavailableReason
        ?? (this.state == StorageState.Failed ? "The memory store is locked: the storage key does not open it." : null);

    /// <summary>Nothing happens at startup: the store opens when the key arrives (see <see cref="WhenOpenAsync"/>).</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Waits for the key without a time limit, then opens (and migrates) the store.</summary>
    public async Task WhenOpenAsync(CancellationToken cancellationToken)
    {
        await this.keys.WhenAvailableAsync(cancellationToken).ConfigureAwait(false);
        await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StoreMemoryAsync(string memoryId, string content, List<ChunkRecord> chunks, List<float[]> vectors, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).StoreMemoryAsync(memoryId, content, chunks, vectors, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<MemoryResult?> GetMemoryAsync(string memoryId, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).GetMemoryAsync(memoryId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task UpdateMetadataAsync(string memoryId, string? title, List<string>? tags, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).UpdateMetadataAsync(memoryId, title, tags, updatedAt, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> DeleteMemoryAsync(string memoryId, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).DeleteMemoryAsync(memoryId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> DeleteMemoriesAsync(IReadOnlyCollection<string> memoryIds, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).DeleteMemoriesAsync(memoryIds, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<List<SearchResult>> SearchAsync(float[] queryVector, int limit, float? minScore = null, List<string>? tags = null, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).SearchAsync(queryVector, limit, minScore, tags, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<List<SearchResult>> SearchDenseAsync(float[] queryVector, int limit, float? minScore, MemorySearchFilter filter, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).SearchDenseAsync(queryVector, limit, minScore, filter, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<List<KeywordHit>> KeywordSearchAsync(string matchExpression, int limit, MemorySearchFilter filter, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).KeywordSearchAsync(matchExpression, limit, filter, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> RebuildKeywordIndexAsync(CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).RebuildKeywordIndexAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string memoryId, CancellationToken cancellationToken = default) =>
        await (await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false)).ExistsAsync(memoryId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<string?> GetDatabasePasswordAsync(CancellationToken cancellationToken)
    {
        await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        return this.password;
    }

    /// <inheritdoc />
    public async Task<string> ReadContentAsync(string path, CancellationToken cancellationToken)
    {
        await this.EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        var blob = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(this.cipher!.Decrypt(blob));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.inner?.Dispose();
        this.openGate.Dispose();
    }

    private async ValueTask<SqliteVecMemoryStore> EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (this.inner is { } open)
        {
            return open;
        }

        await this.openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.inner is { } raced)
            {
                return raced;
            }

            if (this.state == StorageState.Failed)
            {
                throw new StorageLockedException("memory.db does not open with the provided storage key; it stays locked.");
            }

            byte[] contentKey;
            try
            {
                this.password = await this.keys.WaitForPasswordAsync(DatabasePurpose, this.keyWait, cancellationToken).ConfigureAwait(false);
                contentKey = await this.keys.WaitForKeyAsync(ContentPurpose, this.keyWait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new StorageLockedException("The memory store is locked: the Bridge has not provided the storage key.", ex);
            }

            this.cipher = new StorageFileCipher(contentKey, ContentPurpose);
            var settings = this.options.Value;
            try
            {
                Directory.CreateDirectory(settings.DataDirectory);
                var outcome = EncryptedSqlite.Prepare(settings.DatabasePath, this.password, this.FaultInjector);
                var content = MemoryContentMigrator.Migrate(settings.MemoriesDirectory, this.cipher, this.FaultInjector);
                var store = new SqliteVecMemoryStore(this.options, this.cipher, this.loggerFactory.CreateLogger<SqliteVecMemoryStore>(), this.password);
                await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
                this.inner = store;
                this.state = StorageState.Open;
                this.LogOpened(outcome, content.Migrated, content.Recovered);
                return store;
            }
            catch (StorageKeyMismatchException)
            {
                this.state = StorageState.Failed;
                throw;
            }
        }
        finally
        {
            this.openGate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory store opened encrypted (database {Outcome}; {Migrated} content files encrypted, {Recovered} interrupted conversions completed)")]
    private partial void LogOpened(EncryptedOpenOutcome outcome, int migrated, int recovered);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~EncryptedMemoryStoreTests" -v q`
Expected: `Passed: 7`. This also proves F2: sqlite-vec plus the FTS5 index migrate and search under SQLite3MC.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Storage/EncryptedMemoryStore.cs tests/Cortex.Contained.Agent.Host.Tests/Storage/EncryptedMemoryStoreTests.cs
git commit -m "feat(storage): encrypted memory store (memory.db via SQLite3MC, content via AES-GCM), lazy and fail closed" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 8: Direct memory readers + `EncryptedStorageOpener` + wiring

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Memory/MemoryManagementService.cs`
- Create: `src/Cortex.Contained.Agent.Host/Storage/EncryptedStorageOpener.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs` (after `AddMemoryMcpCore`; the startup guard message)
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Storage/EncryptedMemoryAccessTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Security;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

/// <summary>Readers that bypass IMemoryStore (memory list, quarantine) and the opener that migrates after the key push.</summary>
[Trait("Category", "Integrity")]
public sealed class EncryptedMemoryAccessTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly string root = Path.Combine(Path.GetTempPath(), $"memaccess-{Guid.NewGuid():N}");
    private readonly MemoryMcpOptions settings;

    public EncryptedMemoryAccessTests()
    {
        this.settings = new MemoryMcpOptions
        {
            DataDirectory = Path.Combine(this.root, "state", "memory"),
            Ollama = new OllamaOptions { Dimensions = 4 },
        };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static List<ChunkRecord> Chunk(string memoryId) =>
    [
        new ChunkRecord { MemoryId = memoryId, ChunkIndex = 0, StartOffset = 0, Length = 5, Title = "t", Tags = [], CreatedAt = T0, UpdatedAt = T0 },
    ];

    [Fact]
    public async Task MemoryManagementService_ListsAndTimesThroughTheEncryptedStore()
    {
        var ring = new StorageKeyRing();
        ring.Provide(Key);
        using var store = new EncryptedMemoryStore(Options.Create(this.settings), ring, TimeSpan.FromSeconds(5), NullLoggerFactory.Instance);
        await store.StoreMemoryAsync("m1", "zqlistmarker", Chunk("m1"), [[1f, 0f, 0f, 0f]]);
        using var management = new MemoryManagementService(
            Substitute.For<IMemoryService>(), Options.Create(this.settings), NullLogger<MemoryManagementService>.Instance, store);

        var list = await management.ListAsync();
        var times = await management.ListCreationTimesAsync(CancellationToken.None);

        Assert.Equal("zqlistmarker", Assert.Single(list.Items).Content);
        Assert.Equal(T0, Assert.Single(times).CreatedAt);
    }

    [Fact]
    public async Task Opener_KeyArrives_OpensBothStoresAndRecordsTheFingerprint()
    {
        var layout = StorageLayout.From(Path.Combine(this.root, "state"), Path.Combine(this.root, "data"), this.settings);
        Directory.CreateDirectory(layout.StateRoot);
        var context = StorageEncryptionContext.Resolve(new StorageOptions { EncryptAtRest = true }, layout);
        var ring = new StorageKeyRing();
        await using var messages = new MessageStore(layout.MessagesDatabase, NullLogger<MessageStore>.Instance, new MessageStoreEncryption(ring, TimeSpan.FromSeconds(5)));
        using var memory = new EncryptedMemoryStore(Options.Create(this.settings), ring, TimeSpan.FromSeconds(5), NullLoggerFactory.Instance);
        using var opener = new EncryptedStorageOpener(context, ring, messages, memory, TimeProvider.System, NullLogger<EncryptedStorageOpener>.Instance);

        await opener.StartAsync(CancellationToken.None);
        ring.Provide(Key);
        await opener.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StorageState.Open, messages.State);
        Assert.Equal(StorageState.Open, memory.State);
        Assert.Equal(StorageKeyFingerprint.Compute(Key), StorageEncryptionMarker.Read(layout.Marker)!.KeyFingerprint);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~EncryptedMemoryAccessTests" -v q`
Expected: build FAILS — `'MemoryManagementService' does not contain a constructor that takes 4 arguments` and `EncryptedStorageOpener` not found.

- [ ] **Step 3: Implement**

In `MemoryManagementService.cs`, add the field and constructor parameter:

```csharp
    private readonly ILogger<MemoryManagementService> logger;
    private readonly Storage.IMemoryAtRest atRest;
    private SqliteConnection? readConnection;
    private bool disposed;

    public MemoryManagementService(
        IMemoryService memoryService,
        IOptions<MemoryMcpOptions> options,
        ILogger<MemoryManagementService> logger,
        Storage.IMemoryAtRest? atRest = null)
    {
        this.memoryService = memoryService;
        this.options = options.Value;
        this.logger = logger;
        this.atRest = atRest ?? new Storage.PlaintextMemoryAtRest();
    }
```

In `ListAsync` replace the content read:

```csharp
            // Read content from file (decrypted when the store is encrypted)
            var contentPath = Path.Combine(this.options.MemoriesDirectory, $"{memoryId}.memory.data");
            var content = File.Exists(contentPath)
                ? await this.atRest.ReadContentAsync(contentPath, cancellationToken).ConfigureAwait(false)
                : "(content file missing)";
```

In both `ListCreationTimesAsync` and `EnsureConnectionAsync`, add a `Password` member to the `SqliteConnectionStringBuilder` initializer, after `Mode = SqliteOpenMode.ReadOnly,`:

```csharp
            Password = await this.atRest.GetDatabasePasswordAsync(cancellationToken).ConfigureAwait(false),
```

Create `src/Cortex.Contained.Agent.Host/Storage/EncryptedStorageOpener.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Opens the encrypted stores as soon as the Bridge pushes the storage key, so the one-time migration of a
/// plaintext store runs right after the restart that enabled encryption instead of on the first request.
/// Records the key fingerprint in the marker when an encrypted agent has none yet (configured or detected).
/// </summary>
internal sealed partial class EncryptedStorageOpener : BackgroundService
{
    private readonly StorageEncryptionContext context;
    private readonly StorageKeyRing keys;
    private readonly IStorageState messages;
    private readonly EncryptedMemoryStore memory;
    private readonly TimeProvider time;
    private readonly ILogger<EncryptedStorageOpener> logger;

    public EncryptedStorageOpener(
        StorageEncryptionContext context,
        StorageKeyRing keys,
        IStorageState messages,
        EncryptedMemoryStore memory,
        TimeProvider time,
        ILogger<EncryptedStorageOpener> logger)
    {
        this.context = context;
        this.keys = keys;
        this.messages = messages;
        this.memory = memory;
        this.time = time;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await this.keys.WhenAvailableAsync(stoppingToken).ConfigureAwait(false);
            await this.messages.WhenOpenAsync(stoppingToken).ConfigureAwait(false);
            await this.memory.WhenOpenAsync(stoppingToken).ConfigureAwait(false);
            if (this.context.EnsureMarker(this.keys.Fingerprint!, this.time.GetUtcNow()))
            {
                this.LogMarkerWritten();
            }

            this.LogStoresOpen();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is StorageKeyMismatchException or StorageLockedException or InvalidDataException or IOException or SqliteException)
        {
            this.LogOpenFailed(ex.GetType().Name, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Encrypted stores are open (messages, history, memory)")]
    private partial void LogStoresOpen();

    [LoggerMessage(Level = LogLevel.Information, Message = "Encryption marker written with the fingerprint of the key that opened the stores")]
    private partial void LogMarkerWritten();

    [LoggerMessage(Level = LogLevel.Error, Message = "Encrypted stores did not open ({ErrorType}): {ErrorMessage}")]
    private partial void LogOpenFailed(string errorType, string errorMessage);
}
```

In `Program.cs`, directly after `builder.Services.AddMemoryMcpCore(builder.Configuration);` add:

```csharp
// --- Encryption at rest of the memory store (memory.db + *.memory.data) under the tenant key (D4) ---
if (storageEncryption.Enabled)
{
    builder.Services.AddSingleton(sp => new EncryptedMemoryStore(
        sp.GetRequiredService<IOptions<MemoryMcpOptions>>(),
        sp.GetRequiredService<StorageKeyRing>(),
        TimeSpan.FromSeconds(storageOptions.KeyWaitSeconds),
        sp.GetRequiredService<ILoggerFactory>()));
    builder.Services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<EncryptedMemoryStore>());
    builder.Services.AddSingleton<IMemoryAtRest>(sp => sp.GetRequiredService<EncryptedMemoryStore>());
    builder.Services.AddHostedService<EncryptedStorageOpener>();
}
else
{
    builder.Services.AddSingleton<IMemoryAtRest, PlaintextMemoryAtRest>();
}
```

Update the startup guard message (the `MemoryMcp:Encryption:Enabled` throw) to:

```csharp
        "MemoryMcp:Encryption:Enabled is not supported by the Agent Host: its SQLite engine (SQLite3 Multiple Ciphers) cannot open SQLCipher databases. Storage:EncryptAtRest (or the Bridge Storage page) encrypts memory.db and the memory files with the tenant key.");
```

The existing startup `await memoryStore.InitializeAsync()` stays: `EncryptedMemoryStore.InitializeAsync` returns at once, and nothing is created before the key.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~EncryptedMemoryAccessTests|FullyQualifiedName~Memory" -v q`
Expected: all pass (`EncryptedMemoryAccessTests` 2).

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Memory/MemoryManagementService.cs src/Cortex.Contained.Agent.Host/Storage/EncryptedStorageOpener.cs src/Cortex.Contained.Agent.Host/Program.cs tests/Cortex.Contained.Agent.Host.Tests/Storage/EncryptedMemoryAccessTests.cs
git commit -m "feat(storage): wire the encrypted memory store; open and migrate when the key arrives" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 9: Encrypted session snapshot, deferred restore

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Agent/SessionSnapshot.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentProcessingService.cs` (whole file below)
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs` (`AgentProcessingService` registration)
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Storage/EncryptedSessionSnapshotTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

/// <summary>The shutdown snapshot of live sessions under encryption at rest.</summary>
[Trait("Category", "Integrity")]
public sealed class EncryptedSessionSnapshotTests : IDisposable
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"snapshot-enc-{Guid.NewGuid():N}");
    private readonly StorageLayout layout;

    public EncryptedSessionSnapshotTests()
    {
        Directory.CreateDirectory(this.dir);
        this.layout = new StorageLayout(this.dir, this.dir, Path.Combine(this.dir, "memory", "memory.db"), Path.Combine(this.dir, "memory", "memories"));
    }

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string SnapshotPath => Path.Combine(this.dir, SessionSnapshotSerializer.SnapshotFileName);

    private static AgentSessionStore Sessions(string? text = null)
    {
        var store = new AgentSessionStore(new SessionConfig(), new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        if (text is not null)
        {
            store.GetOrCreate("ch").AddMessage(new LlmMessage { Role = "user", Content = text });
        }

        return store;
    }

    private static StorageKeyRing Ring(bool provided)
    {
        var ring = new StorageKeyRing();
        if (provided)
        {
            ring.Provide(Key);
        }

        return ring;
    }

    private static StorageFileCipher Cipher(StorageKeyRing ring) =>
        new(ring.KeyFor(SessionSnapshotSerializer.Purpose), SessionSnapshotSerializer.Purpose);

    private bool SnapshotContains(string marker) =>
        File.ReadAllBytes(this.SnapshotPath).AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0;

    [Fact]
    public async Task SaveWithCipher_NoPlaintextOnDisk_RestoredOnlyWithTheKey()
    {
        var ring = Ring(provided: true);
        await SessionSnapshotSerializer.SaveAsync(SessionSnapshotSerializer.CaptureSnapshot(Sessions("zqsnapmarker")), this.dir, NullLogger.Instance, Cipher(ring));

        Assert.False(this.SnapshotContains("zqsnapmarker"));
        Assert.Equal(0, SessionSnapshotSerializer.TryRestore(Sessions(), this.dir, NullLogger.Instance));
        Assert.True(File.Exists(this.SnapshotPath));

        var restoredInto = Sessions();
        Assert.Equal(1, SessionSnapshotSerializer.TryRestore(restoredInto, this.dir, NullLogger.Instance, Cipher(ring)));
        Assert.True(restoredInto.TryGet("ch", out _));
        Assert.False(File.Exists(this.SnapshotPath));
    }

    [Fact]
    public async Task Stop_WhileEnablingIsPending_WritesTheSnapshotEncrypted()
    {
        var context = StorageEncryptionContext.Resolve(new StorageOptions(), this.layout);
        context.MarkRestartPending(new StorageEncryptionMarker { KeyFingerprint = "fp" });
        using var service = new AgentProcessingService(
            Substitute.For<IAgentRuntime>(), Sessions("zqpendingmarker"), this.dir, NullLogger<AgentProcessingService>.Instance, context, Ring(provided: true));

        await service.StopAsync(CancellationToken.None);

        Assert.True(SessionSnapshotSerializer.IsEncrypted(this.dir));
        Assert.False(this.SnapshotContains("zqpendingmarker"));
    }

    [Fact]
    public async Task Stop_EncryptedWithoutKey_WritesNoSnapshotAtAll()
    {
        var context = StorageEncryptionContext.Resolve(new StorageOptions { EncryptAtRest = true }, this.layout);
        using var service = new AgentProcessingService(
            Substitute.For<IAgentRuntime>(), Sessions("zqnokeymarker"), this.dir, NullLogger<AgentProcessingService>.Instance, context, Ring(provided: false));

        await service.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(this.SnapshotPath));
    }

    [Fact]
    public async Task Start_EncryptedSnapshot_RestoredWhenTheKeyArrivesBeforeProcessingStarts()
    {
        var writer = Ring(provided: true);
        await SessionSnapshotSerializer.SaveAsync(SessionSnapshotSerializer.CaptureSnapshot(Sessions("zqrestoremarker")), this.dir, NullLogger.Instance, Cipher(writer));
        var context = StorageEncryptionContext.Resolve(new StorageOptions { EncryptAtRest = true }, this.layout);
        var runtime = Substitute.For<IAgentRuntime>();
        var sessions = Sessions();
        var ring = Ring(provided: false);
        using var service = new AgentProcessingService(runtime, sessions, this.dir, NullLogger<AgentProcessingService>.Instance, context, ring);

        await service.StartAsync(CancellationToken.None);
        await runtime.DidNotReceive().StartProcessingAsync(Arg.Any<CancellationToken>());
        ring.Provide(Key);
        await service.DeferredStart.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(sessions.TryGet("ch", out _));
        await runtime.Received(1).StartProcessingAsync(Arg.Any<CancellationToken>());
        Assert.False(File.Exists(this.SnapshotPath));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~EncryptedSessionSnapshotTests" -v q`
Expected: build FAILS — `'SessionSnapshotSerializer' does not contain a definition for 'Purpose'`.

- [ ] **Step 3: Implement**

In `SessionSnapshot.cs`, add the purpose constant under `SnapshotFileName`:

```csharp
    /// <summary>Key purpose (and associated-data label) of an encrypted snapshot.</summary>
    public const string Purpose = "sessions-snapshot";
```

Replace the `SaveAsync` signature and body head (through `LogSnapshotSaved`):

```csharp
    /// <summary>
    /// Saves the snapshot to disk using atomic write (temp + flush + rename). With <paramref name="cipher"/> the
    /// file is AES-GCM encrypted under the tenant storage key (it holds every live conversation).
    /// </summary>
    public static async Task SaveAsync(SessionsSnapshot snapshot, string stateDir, ILogger logger, Storage.StorageFileCipher? cipher = null)
    {
        var filePath = Path.Combine(stateDir, SnapshotFileName);
        var tempPath = filePath + ".tmp";

        try
        {
            Directory.CreateDirectory(stateDir);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, jsonOptions);
            if (cipher is not null)
            {
                var plaintext = bytes;
                bytes = cipher.Encrypt(plaintext);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }

            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, filePath, overwrite: true);

            LogSnapshotSaved(logger, snapshot.Sessions.Count, filePath);
        }
```

Replace the `TryRestore` signature and opening (up to and including the `Deserialize` line) with:

```csharp
    /// <summary>True when the snapshot file exists and is encrypted (restoring it needs the storage key).</summary>
    public static bool IsEncrypted(string stateDir)
    {
        var filePath = Path.Combine(stateDir, SnapshotFileName);
        return File.Exists(filePath) && Storage.StorageFileCipher.IsEncryptedFile(filePath);
    }

    /// <summary>
    /// Loads and restores sessions from the snapshot file.
    /// Returns the number of sessions restored, or 0 if no snapshot or on failure.
    /// The snapshot file is deleted after successful restore (one-shot); a plaintext one is overwritten first.
    /// An encrypted snapshot without <paramref name="cipher"/>, or one that does not authenticate, is kept.
    /// </summary>
    public static int TryRestore(AgentSessionStore sessionStore, string stateDir, ILogger logger, Storage.StorageFileCipher? cipher = null)
    {
        var filePath = Path.Combine(stateDir, SnapshotFileName);
        if (!File.Exists(filePath))
        {
            return 0;
        }

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (Storage.StorageFileCipher.IsEncrypted(bytes))
            {
                if (cipher is null)
                {
                    LogEncryptedSnapshotKept(logger, filePath);
                    return 0;
                }

                try
                {
                    bytes = cipher.Decrypt(bytes);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    LogEncryptedSnapshotKept(logger, filePath);
                    return 0;
                }
            }

            var snapshot = JsonSerializer.Deserialize<SessionsSnapshot>(bytes, jsonOptions);
```

Replace `DeleteSnapshotFile`:

```csharp
    private static void DeleteSnapshotFile(string filePath)
    {
        try
        {
            // A plaintext snapshot holds whole conversations: overwrite it before unlinking.
            if (File.Exists(filePath) && !Storage.StorageFileCipher.IsEncryptedFile(filePath))
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.Write(new byte[stream.Length]);
                stream.Flush(flushToDisk: true);
            }

            File.Delete(filePath);
        }
        catch
        {
            // Best effort — stale file will be overwritten next shutdown
        }
    }
```

Add the log method after `LogSnapshotRestoreFailed`:

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "Encrypted session snapshot {FilePath} kept: it cannot be decrypted now (no storage key, or another key)")]
    private static partial void LogEncryptedSnapshotKept(ILogger logger, string filePath);
```

Replace `src/Cortex.Contained.Agent.Host/Hubs/AgentProcessingService.cs` with:

```csharp
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary>
/// Hosted service that starts and stops the <see cref="AgentRuntime"/> consumer loop.
/// On startup, restores session state from a snapshot file (if present).
/// On shutdown, saves all active sessions to the snapshot file for resilient restart.
/// With encryption at rest the snapshot is written encrypted (never in plaintext: without the key it is
/// skipped), and an encrypted snapshot is restored once the Bridge has pushed the key — the consumer loop
/// starts after that restore so restored history always precedes new messages.
/// </summary>
internal sealed partial class AgentProcessingService : IHostedService, IDisposable
{
    private readonly IAgentRuntime runtime;
    private readonly AgentSessionStore sessionStore;
    private readonly string stateDir;
    private readonly ILogger<AgentProcessingService> logger;
    private readonly StorageEncryptionContext? encryption;
    private readonly StorageKeyRing? keys;
    private readonly TimeSpan keyWait;
    private readonly CancellationTokenSource stopping = new();
    private Task deferredStart = Task.CompletedTask;

    public AgentProcessingService(
        IAgentRuntime runtime,
        AgentSessionStore sessionStore,
        string stateDir,
        ILogger<AgentProcessingService> logger,
        StorageEncryptionContext? encryption = null,
        StorageKeyRing? keys = null,
        TimeSpan? keyWait = null)
    {
        this.runtime = runtime;
        this.sessionStore = sessionStore;
        this.stateDir = stateDir;
        this.logger = logger;
        this.encryption = encryption;
        this.keys = keys;
        this.keyWait = keyWait ?? TimeSpan.FromSeconds(120);
    }

    /// <summary>The deferred restore-then-start of an encrypted snapshot (completed when there is none).</summary>
    internal Task DeferredStart => this.deferredStart;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (this.keys is not null && SessionSnapshotSerializer.IsEncrypted(this.stateDir))
        {
            this.deferredStart = Task.Run(() => this.RestoreEncryptedThenStartAsync(this.stopping.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        // Restore sessions from snapshot before starting the consumer loop.
        // If restore fails or no snapshot exists, sessions start empty and
        // the Bridge will re-seed them from MessageStore.
        var restored = SessionSnapshotSerializer.TryRestore(this.sessionStore, this.stateDir, this.logger);
        if (restored > 0)
        {
            this.LogSessionsRestored(restored);
        }

        return this.runtime.StartProcessingAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await this.stopping.CancelAsync().ConfigureAwait(false);
        await this.deferredStart.ConfigureAwait(false);

        // Stop processing first — drains in-flight work so sessions are in a clean state.
        await this.runtime.StopProcessingAsync(cancellationToken).ConfigureAwait(false);

        // Save all active sessions to disk for restoration on next startup.
        var snapshot = SessionSnapshotSerializer.CaptureSnapshot(this.sessionStore);
        if (snapshot.Sessions.Count == 0)
        {
            return;
        }

        StorageFileCipher? cipher = null;
        if (this.encryption?.ProtectsNewWrites == true)
        {
            if (this.keys is not { IsAvailable: true })
            {
                this.LogSnapshotSkipped(snapshot.Sessions.Count);
                return;
            }

            cipher = new StorageFileCipher(this.keys.KeyFor(SessionSnapshotSerializer.Purpose), SessionSnapshotSerializer.Purpose);
        }

        await SessionSnapshotSerializer.SaveAsync(snapshot, this.stateDir, this.logger, cipher).ConfigureAwait(false);
    }

    public void Dispose() => this.stopping.Dispose();

    private async Task RestoreEncryptedThenStartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var key = await this.keys!.WaitForKeyAsync(SessionSnapshotSerializer.Purpose, this.keyWait, cancellationToken).ConfigureAwait(false);
            var restored = SessionSnapshotSerializer.TryRestore(
                this.sessionStore, this.stateDir, this.logger, new StorageFileCipher(key, SessionSnapshotSerializer.Purpose));
            if (restored > 0)
            {
                this.LogSessionsRestored(restored);
            }
        }
        catch (TimeoutException)
        {
            this.LogSnapshotRestoreDeferred();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await this.runtime.StartProcessingAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restored {Count} sessions from snapshot")]
    private partial void LogSessionsRestored(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session snapshot of {Count} sessions not saved: encryption at rest is on and the storage key is not available")]
    private partial void LogSnapshotSkipped(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Encrypted session snapshot not restored: the storage key did not arrive in time; processing starts without it and the snapshot is kept")]
    private partial void LogSnapshotRestoreDeferred();
}
```

In `Program.cs`, pass the new arguments to the registration:

```csharp
builder.Services.AddSingleton<IHostedService>(sp =>
    new AgentProcessingService(
        sp.GetRequiredService<IAgentRuntime>(),
        sp.GetRequiredService<AgentSessionStore>(),
        stateRoot,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentProcessingService>(),
        storageEncryption,
        sp.GetRequiredService<StorageKeyRing>(),
        TimeSpan.FromSeconds(storageOptions.KeyWaitSeconds)));
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~EncryptedSessionSnapshotTests|FullyQualifiedName~SessionSnapshotTests" -v q`
Expected: all pass (`EncryptedSessionSnapshotTests` 4, existing `SessionSnapshotTests` unchanged).

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Agent/SessionSnapshot.cs src/Cortex.Contained.Agent.Host/Hubs/AgentProcessingService.cs src/Cortex.Contained.Agent.Host/Program.cs tests/Cortex.Contained.Agent.Host.Tests/Storage/EncryptedSessionSnapshotTests.cs
git commit -m "feat(storage): encrypt the shutdown session snapshot; restore it after the key push" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 10: `StorageInventory` + disk space

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Storage/StorageInventory.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Storage/StorageInventoryTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Security.Cryptography;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

public sealed class StorageInventoryTests : IDisposable
{
    private static readonly byte[] SqliteHeader = [.. "SQLite format 3\0"u8.ToArray(), .. new byte[4080]];

    private readonly string root = Path.Combine(Path.GetTempPath(), $"inventory-{Guid.NewGuid():N}");
    private readonly StorageLayout layout;

    public StorageInventoryTests()
    {
        this.layout = new StorageLayout(
            Path.Combine(this.root, "state"),
            Path.Combine(this.root, "data"),
            Path.Combine(this.root, "state", "memory", "memory.db"),
            Path.Combine(this.root, "state", "memory", "memories"));
        Directory.CreateDirectory(this.layout.MemoriesDirectory);
        Directory.CreateDirectory(this.layout.HistoryDirectory);
        Directory.CreateDirectory(Path.Combine(this.layout.DataRoot, "tool-output"));
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void Covered_ClassifiesEachStoreByItsBytes()
    {
        var cipher = new StorageFileCipher(RandomNumberGenerator.GetBytes(32), "memory-content");
        Write(this.layout.MessagesDatabase, SqliteHeader);
        Write(this.layout.MessagesDatabase + "-wal", new byte[100]);
        Write(this.layout.MemoryDatabasePath, RandomNumberGenerator.GetBytes(4096));
        Write(Path.Combine(this.layout.HistoryDirectory, "history-index.1.db"), SqliteHeader);
        Write(Path.Combine(this.layout.HistoryDirectory, "history-index.2.db"), RandomNumberGenerator.GetBytes(4096));
        Write(Path.Combine(this.layout.MemoriesDirectory, "a.memory.data"), "plain"u8.ToArray());
        Write(Path.Combine(this.layout.MemoriesDirectory, "b.memory.data"), cipher.Encrypt("x"u8.ToArray()));

        var items = new StorageInventory(this.layout).Covered().ToDictionary(i => i.Id);

        Assert.Equal(StorageItemStates.Plaintext, items["messages"].State);
        Assert.Equal(SqliteHeader.Length + 100, items["messages"].Bytes);
        Assert.Equal(StorageItemStates.Encrypted, items["memory-db"].State);
        Assert.Equal("memory/memory.db", items["memory-db"].Path);
        Assert.Equal(StorageItemStates.Mixed, items["history-index"].State);
        Assert.Equal(StorageItemStates.Mixed, items["memory-content"].State);
        Assert.Equal(2, items["memory-content"].Files);
        Assert.Equal(StorageItemStates.Absent, items["sessions-snapshot"].State);
        Assert.True(new StorageInventory(this.layout).HasEncryptedData());
    }

    [Fact]
    public void NotCoveredAndEvaluation_ListKnownPlaintextStoresAndUnrecognizedEntries()
    {
        Write(Path.Combine(this.layout.StateRoot, "subagents", "subagents.db"), SqliteHeader);
        Write(Path.Combine(this.layout.StateRoot, "backups-old", "copy.memory.data"), "x"u8.ToArray());
        Write(Path.Combine(this.layout.StateRoot, "recall-settings.json"), "{}"u8.ToArray());
        Write(Path.Combine(this.layout.EvaluationDirectory, "hybrid", "runs", "r1", "messages.db"), SqliteHeader);
        Write(Path.Combine(this.layout.DataRoot, "tool-output", "big.txt"), "x"u8.ToArray());
        var inventory = new StorageInventory(this.layout);

        var notCovered = inventory.NotCovered().ToDictionary(i => i.Id);
        var evaluation = inventory.Evaluation();

        Assert.Equal(StorageItemStates.NotCovered, notCovered["subagents"].State);
        Assert.Contains("other:backups-old", notCovered.Keys);
        Assert.Contains("tool-output", notCovered.Keys);
        Assert.DoesNotContain("other:recall-settings.json", notCovered.Keys);
        Assert.DoesNotContain("other:memory", notCovered.Keys);
        Assert.Equal(1, evaluation!.Files);
        Assert.Equal(StorageItemStates.Plaintext, evaluation.State);
    }

    [Fact]
    public void EmptyState_NothingEncryptedNoEvaluation()
    {
        var inventory = new StorageInventory(this.layout);

        Assert.False(inventory.HasEncryptedData());
        Assert.Null(inventory.Evaluation());
        Assert.All(inventory.Covered(), i => Assert.Equal(StorageItemStates.Absent, i.State));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~StorageInventoryTests" -v q`
Expected: build FAILS — `The type or namespace name 'StorageInventory' could not be found`.

- [ ] **Step 3: Implement** `src/Cortex.Contained.Agent.Host/Storage/StorageInventory.cs`:

```csharp
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
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~StorageInventoryTests" -v q`
Expected: `Passed: 3`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Storage/StorageInventory.cs tests/Cortex.Contained.Agent.Host.Tests/Storage/StorageInventoryTests.cs
git commit -m "feat(storage): header-only inventory of encrypted, plaintext and not-covered stores" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 11: `StorageEncryptionService`, `IStorageHub`, `/health`

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Storage/SecureErase.cs`
- Create: `src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionService.cs`
- Create: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Storage.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs` (field + optional ctor param)
- Modify: `src/Cortex.Contained.Contracts/Hub/IAgentHub.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs` (registrations + `/health`)
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Storage/StorageEncryptionServiceTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Runtime.CompilerServices;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

public sealed class StorageEncryptionServiceTests : IDisposable
{
    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Range(2, 32).Select(i => (byte)i).ToArray());

    private readonly string root = Path.Combine(Path.GetTempPath(), $"enc-service-{Guid.NewGuid():N}");
    private readonly StorageLayout layout;
    private readonly IHostApplicationLifetime lifetime = Substitute.For<IHostApplicationLifetime>();
    private readonly IDiskSpace disk = Substitute.For<IDiskSpace>();
    private readonly IStorageState messages = Substitute.For<IStorageState>();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

    public StorageEncryptionServiceTests()
    {
        this.layout = new StorageLayout(
            Path.Combine(this.root, "state"),
            Path.Combine(this.root, "data"),
            Path.Combine(this.root, "state", "memory", "memory.db"),
            Path.Combine(this.root, "state", "memory", "memories"));
        Directory.CreateDirectory(this.layout.MemoriesDirectory);
        this.disk.AvailableBytes(Arg.Any<string>()).Returns(long.MaxValue);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private StorageEncryptionService Service(StorageKeyRing ring, StorageOptions? options = null) => new(
        StorageEncryptionContext.Resolve(options ?? new StorageOptions(), this.layout),
        ring,
        this.messages,
        new StorageInventory(this.layout),
        this.disk,
        this.lifetime,
        this.time,
        NullLogger<StorageEncryptionService>.Instance,
        memory: null,
        selfRestart: true);

    private static StorageKeyRing Ring(string? key = null)
    {
        var ring = new StorageKeyRing();
        if (key is not null)
        {
            ring.Provide(key);
        }

        return ring;
    }

    private static StorageEncryptionEnableRequestDto Request(string key) => new() { KeyFingerprint = StorageKeyFingerprint.Compute(key) };

    [Fact]
    public async Task Enable_MatchingKey_ErasesEvaluationWritesMarkerAndRestarts()
    {
        var run = Path.Combine(this.layout.EvaluationDirectory, "hybrid", "runs", "r1");
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "messages.db"), "zqevalmarker");
        var service = this.Service(Ring(KeyA));

        var result = service.Enable(Request(KeyA));

        Assert.True(result.Accepted);
        Assert.True(result.RestartScheduled);
        Assert.False(Directory.Exists(this.layout.EvaluationDirectory));
        var marker = StorageEncryptionMarker.Read(this.layout.Marker)!;
        Assert.Equal(StorageKeyFingerprint.Compute(KeyA), marker.KeyFingerprint);
        Assert.Equal(this.time.GetUtcNow(), marker.EnabledAt);
        Assert.True(service.GetStatus().RestartPending);

        this.lifetime.DidNotReceive().StopApplication();
        this.time.Advance(StorageEncryptionService.RestartDelay);
        await WaitUntilAsync(() => this.lifetime.ReceivedCalls().Any());
        this.lifetime.Received(1).StopApplication();
    }

    [Fact]
    public void Enable_WithoutKeyOrWithAnotherKey_RefusedAndNothingWritten()
    {
        Assert.False(this.Service(Ring()).Enable(Request(KeyA)).Accepted);
        var mismatch = this.Service(Ring(KeyB)).Enable(Request(KeyA));

        Assert.False(mismatch.Accepted);
        Assert.Contains(mismatch.Errors, e => e.Contains("different storage key", StringComparison.Ordinal));
        Assert.False(File.Exists(this.layout.Marker));
        this.lifetime.DidNotReceive().StopApplication();
    }

    [Fact]
    public void Enable_NotEnoughSpace_RefusedWithTheNumbers()
    {
        this.disk.AvailableBytes(Arg.Any<string>()).Returns(1024);

        var result = this.Service(Ring(KeyA)).Enable(Request(KeyA));

        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.StartsWith("Not enough free space on the state volume", StringComparison.Ordinal));
        Assert.False(File.Exists(this.layout.Marker));
    }

    [Fact]
    public void Enable_AlreadyEncrypted_NoOp()
    {
        var result = this.Service(Ring(KeyA), new StorageOptions { EncryptAtRest = true }).Enable(Request(KeyA));

        Assert.True(result.AlreadyEnabled);
        Assert.False(result.RestartScheduled);
        Assert.False(File.Exists(this.layout.Marker));
    }

    [Fact]
    public void Status_KeyOfAnotherFingerprintThanTheMarker_ReportedRejected()
    {
        new StorageEncryptionMarker { KeyFingerprint = StorageKeyFingerprint.Compute(KeyA) }.Write(this.layout.Marker);
        var ring = new StorageKeyRing { ExpectedFingerprint = StorageKeyFingerprint.Compute(KeyA) };
        ring.Provide(KeyB);
        this.messages.State.Returns(StorageState.AwaitingKey);

        var status = this.Service(ring).GetStatus();

        Assert.True(status.EncryptAtRest);
        Assert.Equal("marker", status.Source);
        Assert.Equal(StorageKeyStates.Rejected, status.KeyState);
        Assert.Equal(StorageKeyFingerprint.Compute(KeyA), status.ExpectedKeyFingerprint);
        Assert.Equal("awaitingKey", status.MessagesState);
    }

    [Fact]
    public void Preview_ListsWhatIsEncryptedErasedAndNotCovered()
    {
        File.WriteAllBytes(this.layout.MessagesDatabase, [.. "SQLite format 3\0"u8.ToArray(), .. new byte[4080]]);
        Directory.CreateDirectory(Path.Combine(this.layout.EvaluationDirectory, "smoke"));
        File.WriteAllText(Path.Combine(this.layout.EvaluationDirectory, "smoke", "x.json"), "{}");
        Directory.CreateDirectory(Path.Combine(this.layout.StateRoot, "subagents"));
        File.WriteAllText(Path.Combine(this.layout.StateRoot, "subagents", "subagents.db"), "x");

        var preview = this.Service(Ring(KeyA)).Preview();

        Assert.Equal("messages", Assert.Single(preview.WillEncrypt).Id);
        Assert.Equal(StorageInventory.EvaluationId, Assert.Single(preview.WillErase).Id);
        Assert.Contains(preview.NotCovered, i => i.Id == "subagents");
        Assert.Equal(4096 + StorageEncryptionService.MigrationMarginBytes, preview.RequiredFreeBytes);
        Assert.Empty(preview.Blockers);
        Assert.Equal(StorageKeyFingerprint.Compute(KeyA), preview.KeyFingerprint);
    }

    [Fact]
    public async Task Hub_WithoutTheService_ThrowsHubException()
    {
        var hub = (AgentHub)RuntimeHelpers.GetUninitializedObject(typeof(AgentHub));

        Assert.Contains(typeof(IStorageHub), typeof(IAgentHub).GetInterfaces());
        await Assert.ThrowsAsync<HubException>(() => hub.GetStorageEncryptionStatus());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~StorageEncryptionServiceTests" -v q`
Expected: build FAILS — `The type or namespace name 'StorageEncryptionService' could not be found`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Storage/SecureErase.cs`:

```csharp
namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Best-effort erasure of plaintext files: each file is overwritten with zeros and flushed before it is
/// unlinked. Copy-on-write or journaling filesystems and SSD remapping may keep older blocks; like volume
/// snapshots, that residue is outside what the application can reach (documented).
/// </summary>
public static class SecureErase
{
    private const int ChunkBytes = 1024 * 1024;

    /// <summary>Erases every file below <paramref name="directory"/>, then the directory. Returns the number of files erased.</summary>
    public static int Directory(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return 0;
        }

        var files = System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
        foreach (var file in files)
        {
            File(file);
        }

        System.IO.Directory.Delete(directory, recursive: true);
        return files.Count;
    }

    /// <summary>Overwrites <paramref name="path"/> with zeros, flushes it to disk and deletes it.</summary>
    public static void File(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var zeros = new byte[ChunkBytes];
            var remaining = stream.Length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, zeros.Length);
                stream.Write(zeros, 0, count);
                remaining -= count;
            }

            stream.Flush(flushToDisk: true);
        }

        System.IO.File.Delete(path);
    }
}
```

`src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionService.cs`:

```csharp
using System.Globalization;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// The owner's view of and switch for encryption at rest (status, pre-flight preview, enable). Enabling is
/// one-way: it requires the key the Bridge just pushed (by fingerprint), erases the plaintext evaluation
/// copies on request, writes the marker and restarts the agent, which migrates every covered store on the
/// next start (<see cref="EncryptedStorageOpener"/>).
/// </summary>
public sealed partial class StorageEncryptionService
{
    /// <summary>Head room on the state volume besides the largest database and the history index.</summary>
    internal const long MigrationMarginBytes = 64L * 1024 * 1024;

    /// <summary>Delay between the enable response and the self-restart, so the reply reaches the Bridge.</summary>
    internal static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    private readonly StorageEncryptionContext context;
    private readonly StorageKeyRing keys;
    private readonly IStorageState messages;
    private readonly StorageInventory inventory;
    private readonly IDiskSpace disk;
    private readonly IHostApplicationLifetime lifetime;
    private readonly TimeProvider time;
    private readonly ILogger<StorageEncryptionService> logger;
    private readonly EncryptedMemoryStore? memory;
    private readonly bool selfRestart;

    public StorageEncryptionService(
        StorageEncryptionContext context,
        StorageKeyRing keys,
        IStorageState messages,
        StorageInventory inventory,
        IDiskSpace disk,
        IHostApplicationLifetime lifetime,
        TimeProvider time,
        ILogger<StorageEncryptionService> logger,
        EncryptedMemoryStore? memory = null,
        bool selfRestart = false)
    {
        this.context = context;
        this.keys = keys;
        this.messages = messages;
        this.inventory = inventory;
        this.disk = disk;
        this.lifetime = lifetime;
        this.time = time;
        this.logger = logger;
        this.memory = memory;
        this.selfRestart = selfRestart;
    }

    /// <summary>Encryption mode, key state and the state of every stored item.</summary>
    public StorageEncryptionStatusDto GetStatus()
    {
        var covered = this.inventory.Covered();
        var health = this.GetHealth();
        List<StorageItemDto> items = [.. covered];
        if (this.inventory.Evaluation() is { } evaluation)
        {
            items.Add(evaluation);
        }

        items.AddRange(this.inventory.NotCovered());
        return new StorageEncryptionStatusDto
        {
            EncryptAtRest = health.EncryptAtRest,
            Source = health.Source,
            RestartPending = health.RestartPending,
            EnabledAt = this.context.Marker?.EnabledAt,
            KeyState = health.KeyState,
            KeyFingerprint = this.keys.Fingerprint,
            ExpectedKeyFingerprint = this.context.Marker is { KeyFingerprint.Length: > 0 } marker ? marker.KeyFingerprint : null,
            HasEncryptedData = covered.Any(i => i.State is StorageItemStates.Encrypted or StorageItemStates.Mixed),
            MessagesState = health.MessagesState,
            MemoryState = health.MemoryState,
            Items = items,
        };
    }

    /// <summary>The cheap part of <see cref="GetStatus"/> for <c>/health</c> (no file scan, no fingerprints).</summary>
    public StorageHealth GetHealth() => new(
        this.context.Enabled,
        this.context.Source,
        this.context.RestartPending,
        this.keys.IsAvailable ? StorageKeyStates.Accepted : this.keys.RejectedForData ? StorageKeyStates.Rejected : StorageKeyStates.Absent,
        this.context.Enabled ? StateName(this.messages.State) : "plaintext",
        this.memory is null ? "plaintext" : StateName(this.memory.State));

    /// <summary>What enabling would do, and whether it can run now. Changes nothing.</summary>
    public StorageEncryptionPreviewDto Preview()
    {
        var covered = this.inventory.Covered();
        var evaluation = this.inventory.Evaluation();
        var notCovered = this.inventory.NotCovered();
        var largestDatabase = covered.Where(i => i.Id is "messages" or "memory-db").Select(i => i.Bytes).DefaultIfEmpty(0).Max();
        var indexBytes = covered.Where(i => i.Id == "history-index").Sum(i => i.Bytes);
        var requiredFree = largestDatabase + indexBytes + MigrationMarginBytes;
        var availableFree = this.disk.AvailableBytes(this.context.Layout.StateRoot);
        var availableTemp = this.disk.AvailableBytes(Path.GetTempPath());

        var blockers = new List<string>();
        if (availableFree >= 0 && availableFree < requiredFree)
        {
            blockers.Add($"Not enough free space on the state volume: {Megabytes(requiredFree)} needed, {Megabytes(availableFree)} free.");
        }

        if (availableTemp >= 0 && availableTemp < largestDatabase)
        {
            blockers.Add($"Not enough free space in the agent's temporary directory: {Megabytes(largestDatabase)} needed, {Megabytes(availableTemp)} free.");
        }

        var warnings = new List<string>
        {
            "The agent restarts to apply encryption and encrypts its stores on that start; chat pauses meanwhile (longer for a large history).",
            "Encryption cannot be turned off from the UI, and agent images older than this version cannot read encrypted stores: do not roll back.",
            "Keep the recovery kit somewhere safe: without it, losing this Windows profile (DPAPI) loses every message and memory.",
        };
        if (indexBytes > 0)
        {
            warnings.Add("The history search index is rebuilt encrypted; until that finishes, history search covers recent messages only.");
        }

        if (evaluation is not null)
        {
            warnings.Add("Evaluation runs hold plaintext copies of your history and memories; erase them when enabling.");
        }

        if (notCovered.Count > 0)
        {
            warnings.Add("Some stores stay in plaintext; they are listed under \"Not covered\".");
        }

        return new StorageEncryptionPreviewDto
        {
            AlreadyEnabled = this.context.Enabled || this.context.RestartPending,
            KeyFingerprint = this.keys.Fingerprint,
            WillEncrypt = covered.Where(i => i.State != StorageItemStates.Absent).ToList(),
            WillErase = evaluation is null ? [] : [evaluation],
            NotCovered = notCovered,
            RequiredFreeBytes = requiredFree,
            AvailableFreeBytes = availableFree,
            RequiredTempBytes = largestDatabase,
            AvailableTempBytes = availableTemp,
            Blockers = blockers,
            Warnings = warnings,
        };
    }

    /// <summary>Turns encryption on (one-way) and schedules the restart that applies it.</summary>
    public StorageEncryptionEnableResultDto Enable(StorageEncryptionEnableRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (this.context.Enabled)
        {
            return new StorageEncryptionEnableResultDto { Accepted = true, AlreadyEnabled = true };
        }

        if (this.context.RestartPending)
        {
            return new StorageEncryptionEnableResultDto { Accepted = true, RestartScheduled = this.selfRestart };
        }

        var errors = new List<string>();
        if (!this.keys.IsAvailable)
        {
            errors.Add("The agent has not received the storage key from the Bridge.");
        }
        else if (!string.Equals(this.keys.Fingerprint, request.KeyFingerprint, StringComparison.Ordinal))
        {
            errors.Add("The agent holds a different storage key than the one the Bridge backed up; reconnect the agent and try again.");
        }

        errors.AddRange(this.Preview().Blockers);
        if (errors.Count > 0)
        {
            return new StorageEncryptionEnableResultDto { Errors = errors };
        }

        if (request.EraseEvaluationArtifacts)
        {
            var erased = SecureErase.Directory(this.context.Layout.EvaluationDirectory);
            this.LogEvaluationErased(erased);
        }

        var fingerprint = this.keys.Fingerprint!;
        this.context.MarkRestartPending(new StorageEncryptionMarker { KeyFingerprint = fingerprint, EnabledAt = this.time.GetUtcNow() });
        this.LogEnabled(fingerprint, this.selfRestart);
        if (this.selfRestart)
        {
            _ = this.StopAfterDelayAsync();
        }

        return new StorageEncryptionEnableResultDto { Accepted = true, RestartScheduled = this.selfRestart };
    }

    private async Task StopAfterDelayAsync()
    {
        await Task.Delay(RestartDelay, this.time).ConfigureAwait(false);
        this.lifetime.StopApplication();
    }

    private static string StateName(StorageState state) => state switch
    {
        StorageState.Open => "open",
        StorageState.Failed => "failed",
        _ => "awaitingKey",
    };

    private static string Megabytes(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{Math.Ceiling(bytes / 1048576.0):0} MB");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Encryption at rest enabled (key {Fingerprint}); self-restart scheduled: {SelfRestart}")]
    private partial void LogEnabled(string fingerprint, bool selfRestart);

    [LoggerMessage(Level = LogLevel.Information, Message = "Erased {Files} files of plaintext evaluation runs before enabling encryption")]
    private partial void LogEvaluationErased(int files);
}

/// <summary>Encryption state for <c>/health</c>.</summary>
/// <param name="EncryptAtRest">The stores run encrypted.</param>
/// <param name="Source"><c>off</c>, <c>config</c>, <c>marker</c> or <c>detected</c>.</param>
/// <param name="RestartPending">Enabled; applies after the restart.</param>
/// <param name="KeyState">One of <see cref="StorageKeyStates"/>.</param>
/// <param name="MessagesState"><c>plaintext</c>, <c>open</c>, <c>awaitingKey</c> or <c>failed</c>.</param>
/// <param name="MemoryState"><c>plaintext</c>, <c>open</c>, <c>awaitingKey</c> or <c>failed</c>.</param>
public sealed record StorageHealth(bool EncryptAtRest, string Source, bool RestartPending, string KeyState, string MessagesState, string MemoryState)
{
    /// <summary><c>off</c>, <c>ok</c> (every encrypted store open) or <c>degraded</c>.</summary>
    public string Status => !this.EncryptAtRest ? "off" : this.MessagesState == "open" && this.MemoryState == "open" ? "ok" : "degraded";
}
```

`src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Storage.cs`:

```csharp
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary><see cref="IStorageHub"/>: the Bridge Storage page (status, pre-flight preview, enabling encryption at rest).</summary>
public sealed partial class AgentHub
{
    /// <inheritdoc />
    public Task<StorageEncryptionStatusDto> GetStorageEncryptionStatus() => Task.FromResult(this.StorageEncryption.GetStatus());

    /// <inheritdoc />
    public Task<StorageEncryptionPreviewDto> PreviewStorageEncryption() => Task.FromResult(this.StorageEncryption.Preview());

    /// <inheritdoc />
    public Task<StorageEncryptionEnableResultDto> EnableStorageEncryption(StorageEncryptionEnableRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(this.StorageEncryption.Enable(request));
    }

    private Storage.StorageEncryptionService StorageEncryption =>
        this.storageEncryption ?? throw new HubException("Storage encryption is not available on this agent.");
}
```

`AgentHub.cs` — add the field after `recall`:

```csharp
    private readonly Cortex.Contained.Agent.Host.Recall.RecallControlService? recall;
    private readonly Storage.StorageEncryptionService? storageEncryption;
```

Change the last constructor parameter and assign it:

```csharp
        Cortex.Contained.Agent.Host.Recall.RecallControlService? recall = null,
        Storage.StorageEncryptionService? storageEncryption = null)
    {
```

```csharp
        this.recall = recall;
        this.storageEncryption = storageEncryption;
```

`IAgentHub.cs` — compose the new interface:

```csharp
/// <see cref="IMcpHub"/>, <see cref="ISystemHub"/>, <see cref="ISubagentHub"/>, <see cref="IRecallHub"/>,
/// <see cref="IStorageHub"/>) for
/// readability and to let consumers depend on the narrow slice they actually use. All
/// methods still share a single SignalR hub connection and route by method name, so the
/// wire protocol is unchanged.
/// </remarks>
public interface IAgentHub : IChatHub, IMemoryHub, IHistoryHub, IVoiceIdHub, ICodingHub, IMcpHub, ISystemHub, ISubagentHub, IRecallHub, IStorageHub
{
}
```

`Program.cs` — after the `if (storageEncryption.Enabled) { … } else { … }` block from Task 8, add:

```csharp
// --- Owner switch and status of encryption at rest (Bridge Storage page, /health) ---
builder.Services.AddSingleton(new StorageInventory(storageEncryption.Layout));
builder.Services.AddSingleton<IDiskSpace, DriveDiskSpace>();
builder.Services.AddSingleton(sp => new StorageEncryptionService(
    storageEncryption,
    sp.GetRequiredService<StorageKeyRing>(),
    sp.GetRequiredService<IStorageState>(),
    sp.GetRequiredService<StorageInventory>(),
    sp.GetRequiredService<IDiskSpace>(),
    sp.GetRequiredService<IHostApplicationLifetime>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<StorageEncryptionService>>(),
    storageEncryption.Enabled ? sp.GetRequiredService<EncryptedMemoryStore>() : null,
    // Inside the container Docker restarts the agent (restart: unless-stopped) after it stops itself.
    selfRestart: string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)));
```

In the `/health` endpoint, add a `StorageEncryptionService storageEncryptionService` parameter after `rerankStage`. Then add `var storage = storageEncryptionService.GetHealth();` after `var reranker = rerankStage.Snapshot();`, and add this block after the `reranker = new { … },` subsystem:

```csharp
            // Encryption at rest: mode and whether the encrypted stores are open (no fingerprints, no paths).
            storage = new
            {
                status = storage.Status,
                storage.EncryptAtRest,
                storage.Source,
                storage.RestartPending,
                key = storage.KeyState,
                messages = storage.MessagesState,
                memory = storage.MemoryState,
            },
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet build cortex-contained.sln -v q` then `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~StorageEncryptionServiceTests|FullyQualifiedName~AgentHub" -v q` and `dotnet test tests\Cortex.Contained.Integration.Tests -v q`.
Expected: build with 0 warnings. `StorageEncryptionServiceTests` 7 pass. The integration tests pass, including `Health_Endpoint_Returns_Ok_With_Subsystems`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Storage/SecureErase.cs src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionService.cs src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Storage.cs src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs src/Cortex.Contained.Contracts/Hub/IAgentHub.cs src/Cortex.Contained.Agent.Host/Program.cs tests/Cortex.Contained.Agent.Host.Tests/Storage/StorageEncryptionServiceTests.cs
git commit -m "feat(storage): status, preview and the one-way enable over the hub; storage block in /health" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 12: Evaluation CLI guard + engine secure-delete guard

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Storage/EvaluationGuard.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs` (top-level CLI dispatch)
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Storage/PlaintextResidueTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

/// <summary>Plaintext residue outside the encrypted stores: evaluation copies and deleted rows of the small stores.</summary>
[Trait("Category", "Integrity")]
public sealed class PlaintextResidueTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"residue-{Guid.NewGuid():N}");

    public PlaintextResidueTests()
    {
        Directory.CreateDirectory(this.root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CORTEX_STATE_PATH"] = this.root,
            ["CORTEX_DATA_PATH"] = this.root,
            ["MemoryMcp:DataDirectory"] = Path.Combine(this.root, "memory"),
        })
        .Build();

    [Fact]
    public async Task EvaluationCommands_RefuseOnlyWhileEncryptionIsOn()
    {
        using var output = new StringWriter();
        Assert.False(await EvaluationGuard.RefuseWhenEncryptedAsync(this.Configuration(), "history-eval", output));

        new StorageEncryptionMarker { KeyFingerprint = "fp" }.Write(Path.Combine(this.root, StorageEncryptionMarker.FileName));

        Assert.True(await EvaluationGuard.RefuseWhenEncryptedAsync(this.Configuration(), "history-eval", output));
        Assert.Contains("history-eval is unavailable while encryption at rest is on", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// subagents.db, todos.db and tasks.db rely on the SQLite3 Multiple Ciphers build compiling
    /// <c>SQLITE_SECURE_DELETE</c> (default on): deleted rows are zeroed, so retention leaves no text behind.
    /// </summary>
    [Fact]
    public void SqliteStoreBase_DeletedRowLeavesNoTextInTheFile()
    {
        var path = Path.Combine(this.root, "probe.db");
        using (var store = new ProbeStore(path))
        {
            store.Run("CREATE TABLE t (x TEXT); INSERT INTO t VALUES ('zqresiduemarker'); DELETE FROM t;");
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(-1, File.ReadAllBytes(path).AsSpan().IndexOf("zqresiduemarker"u8));
    }

    private sealed class ProbeStore(string path) : SqliteStoreBase(path, enableWalMode: false)
    {
        public void Run(string sql) => this.ExecuteNonQuery(sql);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~PlaintextResidueTests" -v q`
Expected: build FAILS — `The name 'EvaluationGuard' does not exist`. Once it compiles, the secure-delete test passes without code changes: it is the guard of F3.

- [ ] **Step 3: Implement** `src/Cortex.Contained.Agent.Host/Storage/EvaluationGuard.cs`:

```csharp
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Keeps the evaluation commands (<c>history-eval</c>, <c>history-smoke</c>, <c>memory-eval</c>) from making
/// plaintext copies of encrypted stores: their run directories hold raw copies of <c>messages.db</c> and the
/// memory store, so they refuse to run while encryption at rest is on.
/// </summary>
public static class EvaluationGuard
{
    /// <summary>Process exit code of a refused command.</summary>
    public const int RefusedExitCode = 3;

    /// <summary>True (and an explanation on <paramref name="output"/>) when <paramref name="command"/> must not run.</summary>
    public static async Task<bool> RefuseWhenEncryptedAsync(IConfiguration configuration, string command, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);
        var stateRoot = configuration["CORTEX_STATE_PATH"] ?? "/app/state";
        var dataRoot = configuration["CORTEX_DATA_PATH"] ?? "/app/data";
        var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        var memory = configuration.GetSection(MemoryMcpOptions.SectionName).Get<MemoryMcpOptions>() ?? new MemoryMcpOptions();
        if (!StorageEncryptionContext.Resolve(storage, StorageLayout.From(stateRoot, dataRoot, memory)).Enabled)
        {
            return false;
        }

        await output.WriteLineAsync(
            $"{command} is unavailable while encryption at rest is on: its run directories would hold plaintext copies of the encrypted history and memories.")
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>The configuration the agent image uses outside the web host: <c>appsettings.json</c> plus environment variables.</summary>
    public static IConfiguration CliConfiguration() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();
}
```

In `Program.cs`, insert before the first `history-smoke` dispatch:

```csharp
// The evaluation CLIs copy the raw stores into run directories, so they refuse to run while encryption at rest is on.
if (args.Length > 0 && args[0] is "history-smoke" or "history-eval" or "memory-eval"
    && await EvaluationGuard.RefuseWhenEncryptedAsync(EvaluationGuard.CliConfiguration(), args[0], Console.Out).ConfigureAwait(false))
{
    Environment.ExitCode = EvaluationGuard.RefusedExitCode;
    return;
}

```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~PlaintextResidueTests" -v q`
Expected: `Passed: 2`.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Storage/EvaluationGuard.cs src/Cortex.Contained.Agent.Host/Program.cs tests/Cortex.Contained.Agent.Host.Tests/Storage/PlaintextResidueTests.cs
git commit -m "feat(storage): evaluation CLIs refuse to copy encrypted stores; guard the engine's secure delete" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 13: Log scrub + log-template hygiene tests

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Memory/MemoryConsolidationService.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/MemoryCompactionService.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/MemoryExtractionService.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs`
- Modify: `src/Cortex.Contained.Channels.Voice/VoiceChannel.cs`
- Test: `tests/Cortex.Contained.Agent.Host.Tests/Storage/LogTemplateHygieneTests.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/StorageEncryption/BridgeLogTemplateHygieneTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Storage/LogTemplateHygieneTests.cs`:

```csharp
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Tests.Storage;

/// <summary>
/// Agent logs (14 days in the data volume) must not hold message or memory text, or encryption at rest
/// would be bypassed through them: no <c>[LoggerMessage]</c> template may carry a content placeholder.
/// </summary>
public sealed partial class LogTemplateHygieneTests
{
    private static readonly HashSet<string> ContentPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content", "Text", "Title", "Query", "Response", "LlmResponse", "Transcript", "Prompt", "Summary", "Snippet", "Fact", "Utterance",
    };

    [Fact]
    public void AgentHost_LoggerMessages_CarryNoContentPlaceholders()
    {
        var offenders = typeof(Cortex.Contained.Agent.Host.Hubs.AgentHub).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(x => x.Attribute?.Message is not null)
            .SelectMany(x => Placeholder().Matches(x.Attribute!.Message!)
                .Select(p => p.Groups[1].Value)
                .Where(ContentPlaceholders.Contains)
                .Select(p => $"{x.Method.DeclaringType!.Name}.{x.Method.Name}: {{{p}}}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"\{(\w+)(?:[:,][^}]*)?\}")]
    private static partial Regex Placeholder();
}
```

`tests/Cortex.Contained.Bridge.Tests/StorageEncryption/BridgeLogTemplateHygieneTests.cs`:

```csharp
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Tests.StorageEncryption;

/// <summary>
/// Bridge and channel logs (14 days under %LOCALAPPDATA%\Cortex\logs) must not hold message text or
/// transcripts: no <c>[LoggerMessage]</c> template may carry a content placeholder.
/// </summary>
public sealed partial class BridgeLogTemplateHygieneTests
{
    private static readonly HashSet<string> ContentPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content", "Text", "Title", "Query", "Response", "LlmResponse", "Transcript", "Prompt", "Summary", "Snippet", "Fact", "Utterance",
    };

    public static TheoryData<string> Assemblies => new()
    {
        typeof(Cortex.Contained.Bridge.Security.SecretManager).Assembly.GetName().Name!,
        typeof(Cortex.Contained.Channels.Voice.VoiceChannel).Assembly.GetName().Name!,
        typeof(Cortex.Contained.Channels.Discord.DiscordChannel).Assembly.GetName().Name!,
        typeof(Cortex.Contained.Channels.WebChat.WebChatHub).Assembly.GetName().Name!,
        typeof(Cortex.Contained.Channels.CloudMessaging.CloudMessagingChannel).Assembly.GetName().Name!,
    };

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void LoggerMessages_CarryNoContentPlaceholders(string assemblyName)
    {
        var offenders = Assembly.Load(assemblyName).GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(x => x.Attribute?.Message is not null)
            .SelectMany(x => Placeholder().Matches(x.Attribute!.Message!)
                .Select(p => p.Groups[1].Value)
                .Where(ContentPlaceholders.Contains)
                .Select(p => $"{x.Method.DeclaringType!.Name}.{x.Method.Name}: {{{p}}}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"\{(\w+)(?:[:,][^}]*)?\}")]
    private static partial Regex Placeholder();
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~LogTemplateHygieneTests" -v q` and `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~BridgeLogTemplateHygieneTests" -v q`
Expected: FAIL. The agent test lists `MemoryConsolidationService.LogMemoryAdded: {Title}`, `{Content}`, `LogConsolidationNonJson: {LlmResponse}`, `LogParsingFailed: {Response}`, `LogMemoryNoop: {Title}`, `MemoryCompactionService.LogDuplicateClusterFound: {Title}`, `MemoryExtractionService.LogNonJsonResponse: {LlmResponse}`, `LogExtractionRetryFailed: {LlmResponse}`, `AgentHub.LogMemoryCreate: {Title}` and `LogMemorySearch: {Query}`. The Bridge test fails for the `Cortex.Contained.Channels.Voice` assembly (`VoiceChannel.LogTranscribed: {Text}`).

- [ ] **Step 3: Scrub the templates (lengths and fixed causes only)**

`MemoryConsolidationService.cs` call sites — replace each line on the left with the one on the right:

| Before | After |
|---|---|
| `this.LogMemoryAdded(conversationId, ingestResult.MemoryId!, fact.Title ?? "(untitled)", fact.Content);` | `this.LogMemoryAdded(conversationId, ingestResult.MemoryId!, fact.Content.Length);` |
| `this.LogMemoryAdded(conversationId, fallbackResult.MemoryId!, fact.Title ?? "(untitled)", fact.Content);` | `this.LogMemoryAdded(conversationId, fallbackResult.MemoryId!, fact.Content.Length);` |
| `this.LogConsolidationNonJson(json);` | `this.LogConsolidationNonJson(json.Length);` |
| `this.LogMemoryNoop(conversationId, fact.Title ?? "(untitled)", "consolidation returned non-JSON, skipping to avoid duplicate");` | `this.LogMemoryNoop(conversationId, "consolidation returned non-JSON, skipping to avoid duplicate");` |
| `this.LogParsingFailed("consolidation", json, "deserialized to null");` | `this.LogParsingFailed("consolidation", json.Length, "deserialized to null");` |
| `this.LogMemoryAdded(conversationId, addResult.MemoryId!, fact.Title ?? "(untitled)", fact.Content);` | `this.LogMemoryAdded(conversationId, addResult.MemoryId!, fact.Content.Length);` |
| `this.LogMemoryUpdated(conversationId, decision.MemoryId, decision.Reason ?? "merged", content);` | `this.LogMemoryUpdated(conversationId, decision.MemoryId, content.Length);` |
| `this.LogMemoryDeleted(conversationId, decision.MemoryId, decision.Reason ?? "contradicted");` | `this.LogMemoryDeleted(conversationId, decision.MemoryId);` |
| `this.LogMemoryAdded(conversationId, replaceResult.MemoryId!, fact.Title ?? "(untitled)", fact.Content);` | `this.LogMemoryAdded(conversationId, replaceResult.MemoryId!, fact.Content.Length);` |
| `this.LogMemoryNoop(conversationId, fact.Title ?? "(untitled)", decision.Reason ?? "already exists");` | `this.LogMemoryNoop(conversationId, "already exists");` |
| `this.LogParsingFailed("consolidation", unknownAction, "unknown action");` | `this.LogParsingFailed("consolidation", unknownAction.Length, "unknown action");` |
| `this.LogMemoryNoop(conversationId, fact.Title ?? "(untitled)", "unknown action, skipping to avoid duplicate");` | `this.LogMemoryNoop(conversationId, "unknown action, skipping to avoid duplicate");` |
| `this.LogParsingFailed("consolidation", llmResponse, ex.Message);` | `this.LogParsingFailed("consolidation", llmResponse.Length, ex.Message);` |
| `this.LogMemoryNoop(conversationId, fact.Title ?? "(untitled)", "JSON parse error, skipping to avoid duplicate");` | `this.LogMemoryNoop(conversationId, "JSON parse error, skipping to avoid duplicate");` |

and its log declarations:

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "Consolidation LLM returned prose instead of JSON ({ResponseLength} chars), skipping")]
    private partial void LogConsolidationNonJson(int responseLength);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse {Phase} JSON ({ResponseLength} chars): {ErrorMessage}")]
    private partial void LogParsingFailed(string phase, int responseLength, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ConversationId}] Memory ADD: {MemoryId} ({ContentLength} chars)")]
    private partial void LogMemoryAdded(string conversationId, string memoryId, int contentLength);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ConversationId}] Memory UPDATE: {MemoryId} ({ContentLength} chars)")]
    private partial void LogMemoryUpdated(string conversationId, string memoryId, int contentLength);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ConversationId}] Memory DELETE: {MemoryId}")]
    private partial void LogMemoryDeleted(string conversationId, string memoryId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ConversationId}] Memory NOOP: {Cause}")]
    private partial void LogMemoryNoop(string conversationId, string cause);
```

`MemoryCompactionService.cs`:

```csharp
                    this.LogDuplicateClusterFound(memory.MemoryId, duplicates.Count);
```

```csharp
                        this.LogCompactionResult(duplicate.MemoryId, result.Action);
```

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "Duplicate cluster found: memory {MemoryId} has {DuplicateCount} similar memories")]
    private partial void LogDuplicateClusterFound(string memoryId, int duplicateCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Compaction result for {MemoryId}: action={Action}")]
    private partial void LogCompactionResult(string memoryId, MemoryConsolidationService.ConsolidationAction action);
```

`MemoryExtractionService.cs`:

```csharp
        this.LogNonJsonResponse(result.Content?.Length ?? 0);
```

```csharp
            this.LogExtractionRetryFailed(retryResult.Content?.Length ?? 0);
```

```csharp
    [LoggerMessage(Level = LogLevel.Debug, Message = "Extraction LLM returned prose instead of JSON ({ResponseLength} chars), asking follow-up")]
    private partial void LogNonJsonResponse(int responseLength);
```

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "Extraction retry also returned non-JSON ({ResponseLength} chars)")]
    private partial void LogExtractionRetryFailed(int responseLength);
```

`AgentHub.cs`:

```csharp
        this.LogMemoryCreate(request.Title?.Length ?? 0, request.Content.Length);
```

```csharp
        this.LogMemorySearch(request.Query.Length);
```

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "Creating memory ({TitleLength} title chars, {ContentLength} content chars)")]
    private partial void LogMemoryCreate(int titleLength, int contentLength);
```

```csharp
    [LoggerMessage(Level = LogLevel.Debug, Message = "Searching memories ({QueryLength} chars)")]
    private partial void LogMemorySearch(int queryLength);
```

`VoiceChannel.cs`:

```csharp
                this.LogTranscribed(ChannelId, text.Length);
```

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "Voice channel {ChannelId} transcribed {CharCount} chars")]
    private partial void LogTranscribed(string channelId, int charCount);
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet build cortex-contained.sln -v q` then the two hygiene filters from Step 2, plus `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~Memory" -v q`.
Expected: 0 warnings. Agent hygiene 1 pass, Bridge hygiene 5 pass, the memory tests are unchanged and green.

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Agent.Host/Memory/MemoryConsolidationService.cs src/Cortex.Contained.Agent.Host/Memory/MemoryCompactionService.cs src/Cortex.Contained.Agent.Host/Memory/MemoryExtractionService.cs src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs src/Cortex.Contained.Channels.Voice/VoiceChannel.cs tests/Cortex.Contained.Agent.Host.Tests/Storage/LogTemplateHygieneTests.cs tests/Cortex.Contained.Bridge.Tests/StorageEncryption/BridgeLogTemplateHygieneTests.cs
git commit -m "fix(logs): no memory text, LLM output or transcripts in logs; guard the templates" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 14: Bridge key custody (`TryGet`, `Replace`, export timestamps)

**Files:**
- Modify: `src/Cortex.Contained.Bridge/Security/SecretManager.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/TenantStorageKeyTests.cs`

- [ ] **Step 1: Write the failing tests** — insert before `private sealed class BreakableSecretStore`:

```csharp
    [Fact]
    public void TryGetTenantStorageKey_NeverCreates_AndThrowsWhenUndecryptable()
    {
        var store = new BreakableSecretStore();
        var manager = new SecretManager(store, NullLogger<SecretManager>.Instance, this.tempDir);

        Assert.Null(manager.TryGetTenantStorageKey("default"));
        Assert.Null(manager.TryGetTenantStorageKey("default"));
        var key = manager.GetOrCreateTenantStorageKey("default");
        Assert.Equal(key, manager.TryGetTenantStorageKey("default"));

        store.Broken = true;
        Assert.Throws<StorageKeyUnavailableException>(() => manager.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public void ReplaceTenantStorageKey_InstallsAValidKeyOnly()
    {
        var manager = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.tempDir);
        var recovered = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

        Assert.Throws<ArgumentException>(() => manager.ReplaceTenantStorageKey("default", Convert.ToBase64String(new byte[16])));
        manager.ReplaceTenantStorageKey("default", recovered);

        Assert.Equal(recovered, manager.GetOrCreateTenantStorageKey("default"));
    }

    [Fact]
    public void RecordStorageKeyExport_PersistsTheTimestampPerTenant()
    {
        var manager = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.tempDir);
        var at = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

        manager.RecordStorageKeyExport("default", at);

        var reader = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.tempDir);
        Assert.Equal(at, reader.GetStorageKeyExportedAt("default"));
        Assert.Null(reader.GetStorageKeyExportedAt("other"));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~TenantStorageKeyTests" -v q`
Expected: build FAILS — `'SecretManager' does not contain a definition for 'TryGetTenantStorageKey'`.

- [ ] **Step 3: Implement** — in `SecretManager.cs`, add after `GetOrCreateTenantStorageKey`:

```csharp
    /// <summary>
    /// The tenant's storage master key, or null when none exists yet. Never creates one; throws
    /// <see cref="StorageKeyUnavailableException"/> when the stored key cannot be decrypted.
    /// </summary>
    public string? TryGetTenantStorageKey(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        lock (this.fileLock)
        {
            EnsureSecretsDirectory();
            var secrets = LoadSecrets(Path.Combine(this.secretsDir, SecretsFileName));
            if (secrets.TenantStorageKeys is null || !secrets.TenantStorageKeys.TryGetValue(tenantId, out var encrypted))
            {
                return null;
            }

            try
            {
                return this.store.Unprotect(encrypted);
            }
            catch (Exception ex)
            {
                this.LogStorageKeyDecryptionFailed(tenantId, ex.Message);
                throw new StorageKeyUnavailableException($"The storage key of tenant '{tenantId}' cannot be decrypted; it is not regenerated.", ex);
            }
        }
    }

    /// <summary>
    /// Installs a recovered storage key for the tenant (recovery-kit restore). The caller decides whether
    /// replacing is safe; this only validates the key and persists it DPAPI-protected.
    /// </summary>
    public void ReplaceTenantStorageKey(string tenantId, string base64Key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        if (Convert.FromBase64String(base64Key).Length != 32)
        {
            throw new ArgumentException("A storage key has exactly 32 bytes.", nameof(base64Key));
        }

        lock (this.fileLock)
        {
            EnsureSecretsDirectory();
            var filePath = Path.Combine(this.secretsDir, SecretsFileName);
            var secrets = LoadSecrets(filePath);
            secrets.TenantStorageKeys ??= new Dictionary<string, string>();
            secrets.TenantStorageKeys[tenantId] = this.store.Protect(base64Key);
            SaveSecrets(filePath, secrets);
            this.LogStorageKeyRestored(tenantId);
        }
    }

    /// <summary>Records when the owner last exported the tenant's recovery kit (not a secret).</summary>
    public void RecordStorageKeyExport(string tenantId, DateTimeOffset exportedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        lock (this.fileLock)
        {
            EnsureSecretsDirectory();
            var filePath = Path.Combine(this.secretsDir, SecretsFileName);
            var secrets = LoadSecrets(filePath);
            secrets.TenantStorageKeyExports ??= new Dictionary<string, DateTimeOffset>();
            secrets.TenantStorageKeyExports[tenantId] = exportedAt;
            SaveSecrets(filePath, secrets);
            this.LogStorageKeyExported(tenantId);
        }
    }

    /// <summary>When the tenant's recovery kit was last exported, or null.</summary>
    public DateTimeOffset? GetStorageKeyExportedAt(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        lock (this.fileLock)
        {
            EnsureSecretsDirectory();
            var secrets = LoadSecrets(Path.Combine(this.secretsDir, SecretsFileName));
            return secrets.TenantStorageKeyExports is not null && secrets.TenantStorageKeyExports.TryGetValue(tenantId, out var at) ? at : null;
        }
    }
```

Add the log methods after `LogStorageKeyDecryptionFailed`:

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage key of tenant {TenantId} installed from a recovery kit")]
    private partial void LogStorageKeyRestored(string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovery kit of the storage key of tenant {TenantId} exported")]
    private partial void LogStorageKeyExported(string tenantId);
```

Add the field to `EncryptedSecrets` after `TenantStorageKeys`:

```csharp
        /// <summary>When each tenant's recovery kit was last exported (not secret), keyed by tenant ID.</summary>
        public Dictionary<string, DateTimeOffset>? TenantStorageKeyExports { get; set; }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~TenantStorageKeyTests|FullyQualifiedName~SecretManagerTests" -v q`
Expected: all pass (`TenantStorageKeyTests` 8).

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Bridge/Security/SecretManager.cs tests/Cortex.Contained.Bridge.Tests/TenantStorageKeyTests.cs
git commit -m "feat(bridge): read the tenant key without minting it, install recovered keys, record kit exports" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 15: `StorageRecoveryKit`

**Files:**
- Create: `src/Cortex.Contained.Bridge/Security/StorageRecoveryKit.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/StorageEncryption/StorageRecoveryKitTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Bridge.Tests.StorageEncryption;

public sealed class StorageRecoveryKitTests
{
    private const int FastIterations = 1_000;
    private const string Passphrase = "correct horse battery staple";
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly DateTimeOffset At = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static string Kit(int iterations = FastIterations) =>
        Encoding.UTF8.GetString(StorageRecoveryKit.Create("default", Key, Passphrase, At, iterations));

    [Fact]
    public void CreateThenOpen_RoundTripsTheKeyAndHoldsNoKeyMaterial()
    {
        var kit = Kit();

        var recovered = StorageRecoveryKit.Open(kit, Passphrase, minimumIterations: FastIterations);

        Assert.Equal(new RecoveredStorageKey("default", Key, StorageKeyFingerprint.Compute(Key)), recovered);
        Assert.DoesNotContain(Key, kit, StringComparison.Ordinal);
        Assert.Contains(StorageRecoveryKit.Format, kit, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_WrongPassphrase_Refused()
    {
        var ex = Assert.Throws<StorageRecoveryKitException>(() => StorageRecoveryKit.Open(Kit(), "wrong passphrase!", minimumIterations: FastIterations));

        Assert.StartsWith("Wrong passphrase", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tenantId", "other")]
    [InlineData("keyFingerprint", "0000000000000000")]
    public void Open_RelabelledKit_Refused(string field, string value)
    {
        var node = JsonNode.Parse(Kit())!;
        node[field] = value;

        Assert.Throws<StorageRecoveryKitException>(() => StorageRecoveryKit.Open(node.ToJsonString(), Passphrase, minimumIterations: FastIterations));
    }

    [Fact]
    public void Open_TooFewIterationsOrNotAKit_Refused()
    {
        Assert.Throws<StorageRecoveryKitException>(() => StorageRecoveryKit.Open(Kit(), Passphrase));
        Assert.Throws<StorageRecoveryKitException>(() => StorageRecoveryKit.Open("{\"format\":\"x\"}", Passphrase));
        Assert.Throws<StorageRecoveryKitException>(() => StorageRecoveryKit.Open("not json", Passphrase));
    }

    [Fact]
    public void Create_ShortPassphrase_Refused()
    {
        Assert.Throws<ArgumentException>(() => StorageRecoveryKit.Create("default", Key, "short", At, FastIterations));
    }

    [Fact]
    public void Create_DefaultsToTheStrongKdf()
    {
        using var kit = JsonDocument.Parse(StorageRecoveryKit.Create("default", Key, Passphrase, At));

        Assert.Equal(StorageRecoveryKit.DefaultIterations, kit.RootElement.GetProperty("kdf").GetProperty("iterations").GetInt32());
        Assert.Equal(Key, StorageRecoveryKit.Open(kit.RootElement.GetRawText(), Passphrase).Base64Key);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~StorageRecoveryKitTests" -v q`
Expected: build FAILS — `The name 'StorageRecoveryKit' does not exist`.

- [ ] **Step 3: Implement** `src/Cortex.Contained.Bridge/Security/StorageRecoveryKit.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Bridge.Security;

/// <summary>A storage key recovered from a kit.</summary>
/// <param name="TenantId">The tenant the kit was exported for.</param>
/// <param name="Base64Key">The 256-bit master key.</param>
/// <param name="Fingerprint">Its fingerprint (verified against the decrypted key).</param>
public sealed record RecoveredStorageKey(string TenantId, string Base64Key, string Fingerprint);

/// <summary>A recovery kit that cannot be opened (format, passphrase or integrity).</summary>
public sealed class StorageRecoveryKitException : Exception
{
    public StorageRecoveryKitException()
    {
    }

    public StorageRecoveryKitException(string message)
        : base(message)
    {
    }

    public StorageRecoveryKitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The owner's offline backup of a tenant storage key: the key encrypted with AES-256-GCM under a key
/// derived from a passphrase with PBKDF2-HMAC-SHA256 (600,000 iterations, 128-bit salt). Format, tenant and
/// key fingerprint are authenticated as associated data, so a kit cannot be relabelled for another tenant
/// or key. Without the passphrase the file is useless; the key is never logged.
/// </summary>
public static class StorageRecoveryKit
{
    /// <summary>Format identifier written into every kit.</summary>
    public const string Format = "cortex-storage-recovery-kit/1";

    /// <summary>PBKDF2 iterations of a new kit (OWASP 2023 guidance for PBKDF2-HMAC-SHA256).</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Fewest PBKDF2 iterations a kit may declare.</summary>
    public const int MinimumIterations = 100_000;

    /// <summary>Shortest accepted passphrase.</summary>
    public const int MinimumPassphraseLength = 12;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Creates the kit (UTF-8 JSON) for <paramref name="base64Key"/>.</summary>
    public static byte[] Create(string tenantId, string base64Key, string passphrase, DateTimeOffset createdAt, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(passphrase);
        if (passphrase.Length < MinimumPassphraseLength)
        {
            throw new ArgumentException($"The passphrase needs at least {MinimumPassphraseLength} characters.", nameof(passphrase));
        }

        var key = Convert.FromBase64String(base64Key);
        try
        {
            var fingerprint = StorageKeyFingerprint.Compute(key);
            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var ciphertext = new byte[key.Length];
            var tag = new byte[TagBytes];
            var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
            try
            {
                using var aes = new AesGcm(wrappingKey, TagBytes);
                aes.Encrypt(nonce, key, ciphertext, tag, AssociatedData(tenantId, fingerprint));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
            }

            var kit = new KitFile
            {
                Format = Format,
                TenantId = tenantId,
                KeyFingerprint = fingerprint,
                CreatedAt = createdAt,
                Kdf = new KdfSection { Name = "PBKDF2-HMAC-SHA256", Iterations = iterations, Salt = Convert.ToBase64String(salt) },
                Cipher = new CipherSection
                {
                    Name = "AES-256-GCM",
                    Nonce = Convert.ToBase64String(nonce),
                    Tag = Convert.ToBase64String(tag),
                    Ciphertext = Convert.ToBase64String(ciphertext),
                },
            };
            return JsonSerializer.SerializeToUtf8Bytes(kit, Json);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Opens a kit; throws <see cref="StorageRecoveryKitException"/> for a wrong passphrase or a damaged file.</summary>
    public static RecoveredStorageKey Open(string kitJson, string passphrase, int minimumIterations = MinimumIterations)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        KitFile? kit;
        try
        {
            kit = JsonSerializer.Deserialize<KitFile>(kitJson, Json);
        }
        catch (JsonException ex)
        {
            throw new StorageRecoveryKitException("The file is not a recovery kit.", ex);
        }

        if (kit is not { Format: Format, TenantId.Length: > 0, KeyFingerprint.Length: > 0, Kdf: { } kdf, Cipher: { } cipher }
            || kdf.Iterations < minimumIterations
            || kdf.Iterations > 10_000_000)
        {
            throw new StorageRecoveryKitException("The file is not a supported recovery kit.");
        }

        byte[] salt, nonce, tag, ciphertext;
        try
        {
            salt = Convert.FromBase64String(kdf.Salt);
            nonce = Convert.FromBase64String(cipher.Nonce);
            tag = Convert.FromBase64String(cipher.Tag);
            ciphertext = Convert.FromBase64String(cipher.Ciphertext);
        }
        catch (FormatException ex)
        {
            throw new StorageRecoveryKitException("The recovery kit is damaged.", ex);
        }

        if (nonce.Length != NonceBytes || tag.Length != TagBytes || ciphertext.Length != StorageKeyFingerprint.KeyBytes)
        {
            throw new StorageRecoveryKitException("The recovery kit is damaged.");
        }

        var key = new byte[ciphertext.Length];
        var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, kdf.Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(wrappingKey, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, key, AssociatedData(kit.TenantId, kit.KeyFingerprint));
            if (!string.Equals(StorageKeyFingerprint.Compute(key), kit.KeyFingerprint, StringComparison.Ordinal))
            {
                throw new StorageRecoveryKitException("The recovery kit is damaged.");
            }

            return new RecoveredStorageKey(kit.TenantId, Convert.ToBase64String(key), kit.KeyFingerprint);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new StorageRecoveryKitException("Wrong passphrase, or the recovery kit was modified.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] AssociatedData(string tenantId, string fingerprint) =>
        Encoding.UTF8.GetBytes($"{Format}\n{tenantId}\n{fingerprint}");

    private sealed class KitFile
    {
        public string Format { get; set; } = string.Empty;

        public string TenantId { get; set; } = string.Empty;

        public string KeyFingerprint { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }

        public KdfSection? Kdf { get; set; }

        public CipherSection? Cipher { get; set; }
    }

    private sealed class KdfSection
    {
        public string Name { get; set; } = string.Empty;

        public int Iterations { get; set; }

        public string Salt { get; set; } = string.Empty;
    }

    private sealed class CipherSection
    {
        public string Name { get; set; } = string.Empty;

        public string Nonce { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;

        public string Ciphertext { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~StorageRecoveryKitTests" -v q`
Expected: `Passed: 7` (about 1 s; one test runs the full 600,000 iterations).

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Bridge/Security/StorageRecoveryKit.cs tests/Cortex.Contained.Bridge.Tests/StorageEncryption/StorageRecoveryKitTests.cs
git commit -m "feat(bridge): passphrase-wrapped recovery kit for the tenant storage key" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 16: `HubClient.Storage` + key push policy

**Files:**
- Create: `src/Cortex.Contained.Bridge/Hub/HubClient.Storage.cs`
- Create: `src/Cortex.Contained.Bridge/Hosting/StorageKeyPushPolicy.cs`
- Modify: `src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs` (`PushStorageKeysAsync` + one log method)
- Test: `tests/Cortex.Contained.Bridge.Tests/StorageEncryption/StorageKeyPushPolicyTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.StorageEncryption;

/// <summary>The Bridge never mints a storage key that would orphan encrypted data.</summary>
public sealed class StorageKeyPushPolicyTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "cortex-keypush-" + Guid.NewGuid().ToString("N"));
    private readonly SecretManager secrets;

    public StorageKeyPushPolicyTests()
    {
        this.secrets = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task ExistingKey_PushedWithoutAskingTheAgent()
    {
        var key = this.secrets.GetOrCreateTenantStorageKey("default");
        var asked = false;

        var (pushed, decision) = await StorageKeyPushPolicy.ResolveAsync(this.secrets, "default", () =>
        {
            asked = true;
            return Task.FromResult<StorageEncryptionStatusDto?>(null);
        });

        Assert.Equal((key, StorageKeyPushDecision.Push), (pushed, decision));
        Assert.False(asked);
    }

    [Fact]
    public async Task NoKey_AgentHoldsEncryptedData_NothingMinted()
    {
        var status = new StorageEncryptionStatusDto { EncryptAtRest = true, HasEncryptedData = true };

        var (pushed, decision) = await StorageKeyPushPolicy.ResolveAsync(this.secrets, "default", () => Task.FromResult<StorageEncryptionStatusDto?>(status));

        Assert.Null(pushed);
        Assert.Equal(StorageKeyPushDecision.WithholdMissingKey, decision);
        Assert.Null(this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task NoKey_EncryptionOff_NothingMinted()
    {
        var (pushed, decision) = await StorageKeyPushPolicy.ResolveAsync(
            this.secrets, "default", () => Task.FromResult<StorageEncryptionStatusDto?>(new StorageEncryptionStatusDto()));

        Assert.Null(pushed);
        Assert.Equal(StorageKeyPushDecision.Withhold, decision);
        Assert.Null(this.secrets.TryGetTenantStorageKey("default"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoKey_FreshEncryptedSetupOrAgentWithoutStorageHub_Minted(bool olderAgent)
    {
        StorageEncryptionStatusDto? status = olderAgent ? null : new StorageEncryptionStatusDto { EncryptAtRest = true };

        var (pushed, decision) = await StorageKeyPushPolicy.ResolveAsync(this.secrets, "default", () => Task.FromResult(status));

        Assert.Equal(StorageKeyPushDecision.MintAndPush, decision);
        Assert.Equal(pushed, this.secrets.TryGetTenantStorageKey("default"));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~StorageKeyPushPolicyTests" -v q`
Expected: build FAILS — `The name 'StorageKeyPushPolicy' does not exist`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Bridge/Hub/HubClient.Storage.cs`:

```csharp
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Bridge.Hub;

/// <summary>Storage page and key-push calls into the agent (<see cref="IStorageHub"/>).</summary>
public sealed partial class HubClient
{
    /// <summary>Encryption mode, key state and the state of every stored item.</summary>
    public async Task<StorageEncryptionStatusDto> GetStorageEncryptionStatusAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<StorageEncryptionStatusDto>(nameof(IAgentHub.GetStorageEncryptionStatus), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What enabling encryption would do; changes nothing.</summary>
    public async Task<StorageEncryptionPreviewDto> PreviewStorageEncryptionAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<StorageEncryptionPreviewDto>(nameof(IAgentHub.PreviewStorageEncryption), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns encryption at rest on (one-way); the agent restarts to apply it.</summary>
    public async Task<StorageEncryptionEnableResultDto> EnableStorageEncryptionAsync(StorageEncryptionEnableRequestDto request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<StorageEncryptionEnableResultDto>(nameof(IAgentHub.EnableStorageEncryption), request, cancellationToken).ConfigureAwait(false);
    }
}
```

`src/Cortex.Contained.Bridge/Hosting/StorageKeyPushPolicy.cs`:

```csharp
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Hosting;

/// <summary>What the Bridge does with a tenant's storage key on connect.</summary>
internal enum StorageKeyPushDecision
{
    /// <summary>The Bridge holds a key: push it.</summary>
    Push,

    /// <summary>No key yet and the agent needs one for a fresh encrypted setup (or is too old to say): create it, then push.</summary>
    MintAndPush,

    /// <summary>No key and encryption is off: nothing to push; the key is created when the owner enables encryption.</summary>
    Withhold,

    /// <summary>No key but the agent holds encrypted data: never mint (that key could not open it); the owner restores the recovery kit.</summary>
    WithholdMissingKey,
}

/// <summary>
/// Decides whether the Bridge may create a tenant storage key. A new key is created only when no encrypted
/// data exists that it would orphan: a lost or rolled-back <c>secrets.json</c> must lead to a locked agent
/// and a recovery-kit restore, never to a silently minted key.
/// </summary>
internal static class StorageKeyPushPolicy
{
    /// <summary>The decision for a Bridge with or without a key and the agent's status (null: an agent without the storage hub).</summary>
    public static StorageKeyPushDecision Decide(bool bridgeHasKey, StorageEncryptionStatusDto? agent)
    {
        if (bridgeHasKey)
        {
            return StorageKeyPushDecision.Push;
        }

        if (agent is null)
        {
            return StorageKeyPushDecision.MintAndPush;
        }

        if (agent.HasEncryptedData)
        {
            return StorageKeyPushDecision.WithholdMissingKey;
        }

        return agent.EncryptAtRest ? StorageKeyPushDecision.MintAndPush : StorageKeyPushDecision.Withhold;
    }

    /// <summary>The key to push for <paramref name="tenantId"/> (null when none may be pushed) and the decision behind it.</summary>
    public static async Task<(string? Key, StorageKeyPushDecision Decision)> ResolveAsync(
        SecretManager secrets, string tenantId, Func<Task<StorageEncryptionStatusDto?>> agentStatus)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(agentStatus);
        var key = secrets.TryGetTenantStorageKey(tenantId);
        if (key is not null)
        {
            return (key, StorageKeyPushDecision.Push);
        }

        var decision = Decide(bridgeHasKey: false, await agentStatus().ConfigureAwait(false));
        return decision == StorageKeyPushDecision.MintAndPush
            ? (secrets.GetOrCreateTenantStorageKey(tenantId), decision)
            : (null, decision);
    }
}
```

In `CredentialsPusher.cs`, replace `PushStorageKeysAsync` with:

```csharp
    /// <summary>
    /// Pushes each connected tenant's storage master key (D4) from DPAPI storage. A tenant whose key
    /// cannot be decrypted gets nothing, so its agent stays locked instead of starting a new history; a
    /// key is created only when no encrypted data would be orphaned by it (<see cref="StorageKeyPushPolicy"/>).
    /// </summary>
    private async Task PushStorageKeysAsync(CancellationToken cancellationToken)
    {
        foreach (var tenantId in this.tenantRouter.GetConnectedTenantIds())
        {
            var client = this.tenantRouter.GetClient(tenantId);
            if (client?.IsConnected != true)
            {
                continue;
            }

            try
            {
                var (key, decision) = await StorageKeyPushPolicy.ResolveAsync(
                    this.secretManager, tenantId, () => TryGetEncryptionStatusAsync(client, cancellationToken)).ConfigureAwait(false);
                if (key is null)
                {
                    if (decision == StorageKeyPushDecision.WithholdMissingKey)
                    {
                        this.LogStorageKeyMissingForEncryptedData(tenantId);
                    }

                    continue;
                }

                var grant = new StorageKeyGrant { Key = key };
                if (!await client.ProvideStorageKeyAsync(grant, cancellationToken).ConfigureAwait(false))
                {
                    this.LogStorageKeyRejected(tenantId);
                }
            }
#pragma warning disable CA1031 // Individual tenant failures should not block others
            catch (Exception ex)
            {
                this.LogStorageKeyPushFailed(tenantId, ex.Message);
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>The agent's encryption status, or null for an agent that predates the storage hub.</summary>
    private static async Task<StorageEncryptionStatusDto?> TryGetEncryptionStatusAsync(Hub.HubClient client, CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetStorageEncryptionStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.AspNetCore.SignalR.HubException)
        {
            return null;
        }
    }
```

and add after `LogStorageKeyPushFailed`:

```csharp
    [LoggerMessage(Level = LogLevel.Critical, Message = "Tenant {TenantId} holds encrypted data but the Bridge has no storage key for it; no key is created. Restore the recovery kit on the Storage page.")]
    private partial void LogStorageKeyMissingForEncryptedData(string tenantId);
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~StorageKeyPushPolicyTests|FullyQualifiedName~CredentialsPusher" -v q`
Expected: all pass (`StorageKeyPushPolicyTests` 5).

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Bridge/Hub/HubClient.Storage.cs src/Cortex.Contained.Bridge/Hosting/StorageKeyPushPolicy.cs src/Cortex.Contained.Bridge/Hosting/CredentialsPusher.cs tests/Cortex.Contained.Bridge.Tests/StorageEncryption/StorageKeyPushPolicyTests.cs
git commit -m "feat(bridge): never mint a storage key over encrypted data; storage hub client calls" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 17: `IStorageAgentClient`, `BridgeStorageReport`, `VerifyPassword`

**Files:**
- Create: `src/Cortex.Contained.Bridge/Storage/IStorageAgentClient.cs`
- Create: `src/Cortex.Contained.Bridge/Storage/BridgeStorageReport.cs`
- Modify: `src/Cortex.Contained.Bridge/Auth/SessionManager.cs`
- Test: `tests/Cortex.Contained.Bridge.Tests/StorageEncryption/StorageReauthAndReportTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Security.Cryptography;
using Cortex.Contained.Bridge.Auth;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.StorageEncryption;

public sealed class StorageReauthAndReportTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "cortex-reauth-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void VerifyPassword_OnlyTheOwnerPasswordPasses()
    {
        var sessions = new SessionManager(
            new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.dir), NullLogger<SessionManager>.Instance);
        Assert.False(sessions.VerifyPassword("anything"));
        sessions.SetupPassword("owner-password-1");

        Assert.True(sessions.VerifyPassword("owner-password-1"));
        Assert.False(sessions.VerifyPassword("owner-password-2"));
        Assert.False(sessions.VerifyPassword(null));
    }

    [Fact]
    public void Scan_ClassifiesTheBridgeStores()
    {
        Directory.CreateDirectory(Path.Combine(this.dir, "mcp"));
        File.WriteAllBytes(Path.Combine(this.dir, "mcp", "actions.db"), RandomNumberGenerator.GetBytes(4096));
        File.WriteAllBytes(Path.Combine(this.dir, "messages.db"), [.. "SQLite format 3\0"u8.ToArray(), .. new byte[100]]);

        var stores = BridgeStorageReport.Scan(this.dir).ToDictionary(s => s.Id, s => s.State);

        Assert.Equal(StorageItemStates.Encrypted, stores["mcp-actions"]);
        Assert.Equal(StorageItemStates.Plaintext, stores["legacy-messages"]);
        Assert.Equal(StorageItemStates.Absent, BridgeStorageReport.Scan(Path.Combine(this.dir, "none"))[0].State);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~StorageReauthAndReportTests" -v q`
Expected: build FAILS — `'SessionManager' does not contain a definition for 'VerifyPassword'`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Bridge/Storage/IStorageAgentClient.cs`:

```csharp
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Storage;

/// <summary>The agent calls the Storage endpoints make (a seam over <see cref="HubClient"/> for tests).</summary>
public interface IStorageAgentClient
{
    Task<StorageEncryptionStatusDto> GetStatusAsync(CancellationToken cancellationToken);

    Task<StorageEncryptionPreviewDto> PreviewAsync(CancellationToken cancellationToken);

    Task<StorageEncryptionEnableResultDto> EnableAsync(StorageEncryptionEnableRequestDto request, CancellationToken cancellationToken);

    /// <summary>Pushes the storage key; false when the agent refuses it (another key in use or not the data's key).</summary>
    Task<bool> ProvideKeyAsync(string base64Key, CancellationToken cancellationToken);
}

/// <summary>Adapts a connected <see cref="HubClient"/> to <see cref="IStorageAgentClient"/>.</summary>
public sealed class HubStorageAgentClient(HubClient client) : IStorageAgentClient
{
    /// <summary>The adapter for <paramref name="client"/>, or null when it is missing or disconnected.</summary>
    public static IStorageAgentClient? For(HubClient? client) => client is { IsConnected: true } ? new HubStorageAgentClient(client) : null;

    public Task<StorageEncryptionStatusDto> GetStatusAsync(CancellationToken cancellationToken) => client.GetStorageEncryptionStatusAsync(cancellationToken);

    public Task<StorageEncryptionPreviewDto> PreviewAsync(CancellationToken cancellationToken) => client.PreviewStorageEncryptionAsync(cancellationToken);

    public Task<StorageEncryptionEnableResultDto> EnableAsync(StorageEncryptionEnableRequestDto request, CancellationToken cancellationToken) =>
        client.EnableStorageEncryptionAsync(request, cancellationToken);

    public Task<bool> ProvideKeyAsync(string base64Key, CancellationToken cancellationToken) =>
        client.ProvideStorageKeyAsync(new StorageKeyGrant { Key = base64Key }, cancellationToken);
}
```

`src/Cortex.Contained.Bridge/Storage/BridgeStorageReport.cs`:

```csharp
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Bridge.Storage;

/// <summary>A store the Bridge keeps on the Windows host.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Label">Human-readable label.</param>
/// <param name="State"><c>encrypted</c>, <c>plaintext</c> or <c>absent</c>.</param>
/// <param name="Detail">What it holds and how it is protected.</param>
public sealed record BridgeStoreState(string Id, string Label, string State, string Detail);

/// <summary>
/// The Bridge's own SQLite stores under <c>%LOCALAPPDATA%\Cortex</c>, classified by header. The Bridge keeps
/// no message history today (the agent owns <c>messages.db</c>); a <c>messages.db</c> in the data directory is
/// a file of an old version, encrypted with the Bridge database key and unused.
/// </summary>
public static class BridgeStorageReport
{
    /// <summary>The stores below <paramref name="cortexDataDir"/>.</summary>
    public static IReadOnlyList<BridgeStoreState> Scan(string cortexDataDir) =>
    [
        Store(
            "mcp-actions",
            "MCP action ledger (mcp/actions.db)",
            Path.Combine(cortexDataDir, "mcp", "actions.db"),
            "Approvals and outcomes of mutating MCP calls, SQLite3 Multiple Ciphers with the DPAPI-protected Bridge database key."),
        Store(
            "legacy-messages",
            "Old Bridge message history (messages.db)",
            Path.Combine(cortexDataDir, "messages.db"),
            "Written by Bridge versions that kept message history; nothing uses it now. Encrypted with the Bridge database key; delete it once you no longer need it."),
    ];

    private static BridgeStoreState Store(string id, string label, string path, string detail) => new(
        id,
        label,
        SqliteFileHeader.Classify(path) switch
        {
            StoredFileState.Encrypted => StorageItemStates.Encrypted,
            StoredFileState.Plaintext => StorageItemStates.Plaintext,
            _ => StorageItemStates.Absent,
        },
        detail);
}
```

`SessionManager.cs` — add after `ActiveSessionCount`:

```csharp
    /// <summary>
    /// Re-authentication for sensitive actions (exporting or restoring the storage recovery kit, enabling
    /// encryption at rest): the signed-in owner types the Bridge password again. Failures are logged.
    /// </summary>
    public bool VerifyPassword(string? password)
    {
        var storedHash = this.secretManager.GetPasswordHash();
        if (storedHash is null || string.IsNullOrEmpty(password))
        {
            this.LogReauthenticationFailed();
            return false;
        }

        if (BCrypt.Net.BCrypt.EnhancedVerify(password, storedHash))
        {
            return true;
        }

        this.LogReauthenticationFailed();
        return false;
    }
```

and after `LogPasswordChanged`:

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-authentication for a sensitive storage action failed")]
    private partial void LogReauthenticationFailed();
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~StorageReauthAndReportTests|FullyQualifiedName~AuthTests" -v q`
Expected: all pass (`StorageReauthAndReportTests` 2).

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Bridge/Storage/IStorageAgentClient.cs src/Cortex.Contained.Bridge/Storage/BridgeStorageReport.cs src/Cortex.Contained.Bridge/Auth/SessionManager.cs tests/Cortex.Contained.Bridge.Tests/StorageEncryption/StorageReauthAndReportTests.cs
git commit -m "feat(bridge): re-authentication, Bridge store report and the storage agent seam" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 18: `StorageEndpoints`

**Files:**
- Create: `src/Cortex.Contained.Bridge/Storage/StorageEndpoints.cs`
- Modify: `src/Cortex.Contained.Bridge/Program.cs` (map after `app.MapRecallEndpoints();`)
- Test: `tests/Cortex.Contained.Bridge.Tests/StorageEncryption/StorageEndpointsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;
using System.Text.Json;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Storage;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.StorageEncryption;

/// <summary>The Storage page API: re-authentication, confirmations, key custody and the recovery kit.</summary>
public sealed class StorageEndpointsTests : IDisposable
{
    private const string Password = "owner-password";
    private const string Passphrase = "correct horse battery staple";
    private static readonly string OtherKey = Convert.ToBase64String(Enumerable.Range(7, 32).Select(i => (byte)i).ToArray());

    private readonly string dir = Path.Combine(Path.GetTempPath(), "cortex-storage-api-" + Guid.NewGuid().ToString("N"));
    private readonly SecretManager secrets;
    private readonly IStorageAgentClient agent = Substitute.For<IStorageAgentClient>();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

    public StorageEndpointsTests()
    {
        this.secrets = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.dir);
        this.agent.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new StorageEncryptionStatusDto());
        this.agent.ProvideKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        this.agent.EnableAsync(Arg.Any<StorageEncryptionEnableRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new StorageEncryptionEnableResultDto { Accepted = true, RestartScheduled = true });
    }

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static bool Verify(string? password) => password == Password;

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static JsonElement BodyOf(IResult result)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result);
        return JsonSerializer.SerializeToElement(value, value!.GetType(), JsonSerializerOptions.Web);
    }

    private static string KitFor(string tenantId, string key) =>
        Encoding.UTF8.GetString(StorageRecoveryKit.Create(tenantId, key, Passphrase, DateTimeOffset.UnixEpoch, StorageRecoveryKit.MinimumIterations));

    private Task<IResult> Enable(StorageEnableRequest body) =>
        StorageEndpoints.EnableAsync("default", this.agent, this.secrets, Verify, body, CancellationToken.None);

    [Fact]
    public void AllStorageRoutes_RequireAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapStorageEndpoints(this.dir);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();

        Assert.Equal(5, endpoints.Count);
        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<IAuthorizeData>()));
        Assert.All(endpoints, e => Assert.StartsWith("/api/tenants/{tenantId}/storage/", e.RoutePattern.RawText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enable_WrongPasswordOrNoConfirmationOrNoKit_RefusedBeforeTouchingTheKey()
    {
        Assert.Equal(403, StatusOf(await this.Enable(new StorageEnableRequest("wrong", "ENCRYPT", AcceptNoRecoveryKit: true))));
        Assert.Equal(400, StatusOf(await this.Enable(new StorageEnableRequest(Password, "encrypt", AcceptNoRecoveryKit: true))));
        Assert.Equal(400, StatusOf(await this.Enable(new StorageEnableRequest(Password, "ENCRYPT"))));

        Assert.Null(this.secrets.TryGetTenantStorageKey("default"));
        await this.agent.DidNotReceive().EnableAsync(Arg.Any<StorageEncryptionEnableRequestDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enable_Confirmed_PushesTheKeyAndSendsItsFingerprint()
    {
        var result = await this.Enable(new StorageEnableRequest(Password, "ENCRYPT", EraseEvaluationArtifacts: false, AcceptNoRecoveryKit: true));

        Assert.Equal(202, StatusOf(result));
        var key = this.secrets.TryGetTenantStorageKey("default")!;
        await this.agent.Received(1).ProvideKeyAsync(key, Arg.Any<CancellationToken>());
        await this.agent.Received(1).EnableAsync(
            Arg.Is<StorageEncryptionEnableRequestDto>(r => r.KeyFingerprint == StorageKeyFingerprint.Compute(key) && !r.EraseEvaluationArtifacts),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enable_AgentHoldsEncryptedDataWithoutBridgeKey_ConflictAndNothingMinted()
    {
        this.agent.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new StorageEncryptionStatusDto { EncryptAtRest = true, HasEncryptedData = true });

        var result = await this.Enable(new StorageEnableRequest(Password, "ENCRYPT", AcceptNoRecoveryKit: true));

        Assert.Equal(409, StatusOf(result));
        Assert.Null(this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Enable_AgentRefusesTheKey_Conflict()
    {
        this.agent.ProvideKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.Equal(409, StatusOf(await this.Enable(new StorageEnableRequest(Password, "ENCRYPT", AcceptNoRecoveryKit: true))));
        await this.agent.DidNotReceive().EnableAsync(Arg.Any<StorageEncryptionEnableRequestDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportKit_ReturnsAKitThatOpensWithThePassphraseAndRecordsTheExport()
    {
        var result = await StorageEndpoints.ExportKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitExportRequest(Password, Passphrase), this.time, CancellationToken.None);

        var file = Assert.IsType<FileContentHttpResult>(result);
        var recovered = StorageRecoveryKit.Open(Encoding.UTF8.GetString(file.FileContents.ToArray()), Passphrase);
        Assert.Equal(this.secrets.TryGetTenantStorageKey("default"), recovered.Base64Key);
        Assert.Equal(this.time.GetUtcNow(), this.secrets.GetStorageKeyExportedAt("default"));
        Assert.StartsWith("cortex-recovery-kit-default-", file.FileDownloadName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportKit_WrongPasswordOrShortPassphrase_Refused()
    {
        Assert.Equal(403, StatusOf(await StorageEndpoints.ExportKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitExportRequest("wrong", Passphrase), this.time, CancellationToken.None)));
        Assert.Equal(400, StatusOf(await StorageEndpoints.ExportKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitExportRequest(Password, "short"), this.time, CancellationToken.None)));
        Assert.Null(this.secrets.GetStorageKeyExportedAt("default"));
    }

    [Fact]
    public async Task Restore_BridgeLostItsKey_InstallsTheKitKeyAndPushesIt()
    {
        var kit = KitFor("default", OtherKey);
        this.agent.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageEncryptionStatusDto { EncryptAtRest = true, HasEncryptedData = true, ExpectedKeyFingerprint = StorageKeyFingerprint.Compute(OtherKey) });

        var result = await StorageEndpoints.RestoreKitAsync("default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, Passphrase, kit), CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        Assert.True(BodyOf(result).GetProperty("applied").GetBoolean());
        Assert.Equal(OtherKey, this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Restore_WorkingKeyOnTheBridgeAndNoComplaintFromTheAgent_NotReplaced()
    {
        var current = this.secrets.GetOrCreateTenantStorageKey("default");

        var result = await StorageEndpoints.RestoreKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, Passphrase, KitFor("default", OtherKey)), CancellationToken.None);

        Assert.Equal(409, StatusOf(result));
        Assert.Equal(current, this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Restore_AgentDataBelongsToTheKitKey_ReplacesTheWrongBridgeKey()
    {
        this.secrets.GetOrCreateTenantStorageKey("default");
        this.agent.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageEncryptionStatusDto { EncryptAtRest = true, HasEncryptedData = true, ExpectedKeyFingerprint = StorageKeyFingerprint.Compute(OtherKey) });

        var result = await StorageEndpoints.RestoreKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, Passphrase, KitFor("default", OtherKey)), CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(OtherKey, this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Restore_KitOfAnotherTenantOrWrongPassphrase_Refused()
    {
        Assert.Equal(400, StatusOf(await StorageEndpoints.RestoreKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, Passphrase, KitFor("other", OtherKey)), CancellationToken.None)));
        Assert.Equal(400, StatusOf(await StorageEndpoints.RestoreKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, "wrong passphrase", KitFor("default", OtherKey)), CancellationToken.None)));
        Assert.Null(this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Status_AgentDisconnected_503WithTheBridgeView()
    {
        var key = this.secrets.GetOrCreateTenantStorageKey("default");

        var result = await StorageEndpoints.GetStatusAsync("default", null, this.secrets, this.dir, CancellationToken.None);

        Assert.Equal(503, StatusOf(result));
        var body = BodyOf(result);
        Assert.Equal(StorageKeyFingerprint.Compute(key), body.GetProperty("bridge").GetProperty("keyFingerprint").GetString());
        Assert.DoesNotContain(key, body.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(2, body.GetProperty("bridgeStores").GetArrayLength());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~StorageEndpointsTests" -v q`
Expected: build FAILS — `The name 'StorageEndpoints' does not exist`.

- [ ] **Step 3: Implement** `src/Cortex.Contained.Bridge/Storage/StorageEndpoints.cs`:

```csharp
using Cortex.Contained.Bridge.Auth;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Bridge.Storage;

/// <summary>Body of <c>POST /api/tenants/{tenantId}/storage/encryption/enable</c>.</summary>
public sealed record StorageEnableRequest(string? Password, string? Confirm, bool EraseEvaluationArtifacts = true, bool AcceptNoRecoveryKit = false);

/// <summary>Body of <c>POST /api/tenants/{tenantId}/storage/recovery-kit</c>.</summary>
public sealed record RecoveryKitExportRequest(string? Password, string? Passphrase);

/// <summary>Body of <c>POST /api/tenants/{tenantId}/storage/recovery-kit/restore</c>.</summary>
public sealed record RecoveryKitRestoreRequest(string? Password, string? Passphrase, string? Kit);

/// <summary>
/// The Storage page API (all require authorization; the sensitive ones also the Bridge password again):
/// <c>/api/tenants/{tenantId}/storage/status</c> (GET), <c>/storage/encryption/preview</c> (POST),
/// <c>/storage/encryption/enable</c> (POST, one-way), <c>/storage/recovery-kit</c> (POST, download) and
/// <c>/storage/recovery-kit/restore</c> (POST). Keys never appear in responses or logs, only fingerprints.
/// The handlers are internal static seams so they are tested without a web host.
/// </summary>
internal static class StorageEndpoints
{
    /// <summary>What the owner types to confirm enabling encryption.</summary>
    internal const string EnableConfirmation = "ENCRYPT";

    /// <summary>Maps the Storage endpoints onto <paramref name="app"/>.</summary>
    public static void MapStorageEndpoints(this WebApplication app, string cortexDataDir)
    {
        app.MapGet("/api/tenants/{tenantId}/storage/status", (string tenantId, [FromServices] TenantRouter router, [FromServices] SecretManager secrets, CancellationToken ct) =>
            GetStatusAsync(tenantId, HubStorageAgentClient.For(router.GetClient(tenantId)), secrets, cortexDataDir, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/storage/encryption/preview", (string tenantId, [FromServices] TenantRouter router, [FromServices] SecretManager secrets, CancellationToken ct) =>
            PreviewAsync(tenantId, HubStorageAgentClient.For(router.GetClient(tenantId)), secrets, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/storage/encryption/enable", (string tenantId, StorageEnableRequest body, [FromServices] TenantRouter router, [FromServices] SecretManager secrets, [FromServices] SessionManager sessions, CancellationToken ct) =>
            EnableAsync(tenantId, HubStorageAgentClient.For(router.GetClient(tenantId)), secrets, sessions.VerifyPassword, body, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/storage/recovery-kit", async (string tenantId, RecoveryKitExportRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SecretManager secrets, [FromServices] SessionManager sessions, [FromServices] TimeProvider time, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await ExportKitAsync(tenantId, HubStorageAgentClient.For(router.GetClient(tenantId)), secrets, sessions.VerifyPassword, body, time, ct).ConfigureAwait(false);
        }).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/storage/recovery-kit/restore", (string tenantId, RecoveryKitRestoreRequest body, [FromServices] TenantRouter router, [FromServices] SecretManager secrets, [FromServices] SessionManager sessions, CancellationToken ct) =>
            RestoreKitAsync(tenantId, HubStorageAgentClient.For(router.GetClient(tenantId)), secrets, sessions.VerifyPassword, body, ct)).RequireAuthorization();
    }

    internal static async Task<IResult> GetStatusAsync(string tenantId, IStorageAgentClient? agent, SecretManager secrets, string cortexDataDir, CancellationToken ct)
    {
        var bridge = KeyView(secrets, tenantId);
        var bridgeStores = BridgeStorageReport.Scan(cortexDataDir);
        if (agent is null)
        {
            return Results.Json(new { connected = false, bridge, bridgeStores }, statusCode: 503);
        }

        return await RecallEndpoints.RelayAsync(
            () => agent.GetStatusAsync(ct),
            status => Results.Ok(new { connected = true, agent = status, bridge, bridgeStores })).ConfigureAwait(false);
    }

    internal static async Task<IResult> PreviewAsync(string tenantId, IStorageAgentClient? agent, SecretManager secrets, CancellationToken ct)
    {
        if (agent is null)
        {
            return NotConnected();
        }

        var bridge = KeyView(secrets, tenantId);
        return await RecallEndpoints.RelayAsync(() => agent.PreviewAsync(ct), preview => Results.Ok(new { preview, bridge })).ConfigureAwait(false);
    }

    internal static async Task<IResult> EnableAsync(
        string tenantId, IStorageAgentClient? agent, SecretManager secrets, Func<string?, bool> verifyPassword, StorageEnableRequest? body, CancellationToken ct)
    {
        if (body is null)
        {
            return BadRequest("Request body is required");
        }

        if (!verifyPassword(body.Password))
        {
            return Forbidden();
        }

        if (!string.Equals(body.Confirm, EnableConfirmation, StringComparison.Ordinal))
        {
            return BadRequest($"Type {EnableConfirmation} to confirm.");
        }

        if (secrets.GetStorageKeyExportedAt(tenantId) is null && !body.AcceptNoRecoveryKit)
        {
            return BadRequest("Export the recovery kit first, or confirm that losing this Windows profile can lose every message and memory.");
        }

        if (agent is null)
        {
            return NotConnected();
        }

        return await AgentCallAsync(async () =>
        {
            var (key, refusal) = await KeyForAgentAsync(tenantId, agent, secrets, ct).ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }

            if (!await agent.ProvideKeyAsync(key!, ct).ConfigureAwait(false))
            {
                return Conflict("The agent refused the storage key: it already uses another key. Restart the agent and try again.");
            }

            var result = await agent.EnableAsync(
                new StorageEncryptionEnableRequestDto { KeyFingerprint = StorageKeyFingerprint.Compute(key!), EraseEvaluationArtifacts = body.EraseEvaluationArtifacts },
                ct).ConfigureAwait(false);
            return result.Accepted ? Results.Json(result, statusCode: 202) : Results.Json(result, statusCode: 400);
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> ExportKitAsync(
        string tenantId, IStorageAgentClient? agent, SecretManager secrets, Func<string?, bool> verifyPassword, RecoveryKitExportRequest? body, TimeProvider time, CancellationToken ct)
    {
        if (body is null)
        {
            return BadRequest("Request body is required");
        }

        if (!verifyPassword(body.Password))
        {
            return Forbidden();
        }

        if (body.Passphrase is not { Length: >= StorageRecoveryKit.MinimumPassphraseLength } passphrase)
        {
            return BadRequest($"The passphrase needs at least {StorageRecoveryKit.MinimumPassphraseLength} characters.");
        }

        return await AgentCallAsync(async () =>
        {
            string? key;
            try
            {
                key = secrets.TryGetTenantStorageKey(tenantId);
            }
            catch (StorageKeyUnavailableException)
            {
                return Conflict("The storage key of this tenant cannot be decrypted on this Windows profile; restore an earlier recovery kit instead.");
            }

            if (key is null)
            {
                if (agent is null)
                {
                    return NotConnected();
                }

                var (created, refusal) = await KeyForAgentAsync(tenantId, agent, secrets, ct).ConfigureAwait(false);
                if (refusal is not null)
                {
                    return refusal;
                }

                key = created!;
                await agent.ProvideKeyAsync(key, ct).ConfigureAwait(false);
            }

            var now = time.GetUtcNow();
            var kit = StorageRecoveryKit.Create(tenantId, key, passphrase, now);
            secrets.RecordStorageKeyExport(tenantId, now);
            var fingerprint = StorageKeyFingerprint.Compute(key);
            return Results.File(kit, "application/json", $"cortex-recovery-kit-{SafeName(tenantId)}-{fingerprint[..8]}.json");
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> RestoreKitAsync(
        string tenantId, IStorageAgentClient? agent, SecretManager secrets, Func<string?, bool> verifyPassword, RecoveryKitRestoreRequest? body, CancellationToken ct)
    {
        if (body?.Kit is not { Length: > 0 } kitJson)
        {
            return BadRequest("The recovery kit file is required");
        }

        if (!verifyPassword(body.Password))
        {
            return Forbidden();
        }

        RecoveredStorageKey recovered;
        try
        {
            recovered = StorageRecoveryKit.Open(kitJson, body.Passphrase ?? string.Empty);
        }
        catch (StorageRecoveryKitException ex)
        {
            return BadRequest(ex.Message);
        }

        if (!string.Equals(recovered.TenantId, tenantId, StringComparison.Ordinal))
        {
            return BadRequest($"This recovery kit belongs to tenant '{recovered.TenantId}'.");
        }

        string? current;
        try
        {
            current = secrets.TryGetTenantStorageKey(tenantId);
        }
        catch (StorageKeyUnavailableException)
        {
            current = null;
        }

        if (current is not null && string.Equals(StorageKeyFingerprint.Compute(current), recovered.Fingerprint, StringComparison.Ordinal))
        {
            return Results.Ok(new { restored = false, fingerprint = recovered.Fingerprint, message = "This key is already installed." });
        }

        StorageEncryptionStatusDto? status = null;
        if (agent is not null)
        {
            try
            {
                status = await agent.GetStatusAsync(ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // An unreachable agent only removes the extra check below
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status = null;
            }
#pragma warning restore CA1031
        }

        var expected = status?.ExpectedKeyFingerprint;
        if (expected is not null && !string.Equals(expected, recovered.Fingerprint, StringComparison.Ordinal))
        {
            return Conflict("This kit holds another key than the one the agent's data is encrypted with.");
        }

        if (current is not null && expected is null)
        {
            return Conflict("The Bridge already holds a working storage key and the agent does not report another one; it is not replaced.");
        }

        secrets.ReplaceTenantStorageKey(tenantId, recovered.Base64Key);
        var applied = false;
        if (agent is not null)
        {
            try
            {
                applied = await agent.ProvideKeyAsync(recovered.Base64Key, ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The key is installed; the next connect pushes it
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                applied = false;
            }
#pragma warning restore CA1031
        }

        return Results.Ok(new
        {
            restored = true,
            fingerprint = recovered.Fingerprint,
            applied,
            message = applied ? "Key restored and accepted by the agent." : "Key restored. Restart the agent so it picks the key up.",
        });
    }

    /// <summary>The tenant key to give the agent, creating it only when no encrypted data would be orphaned.</summary>
    private static async Task<(string? Key, IResult? Refusal)> KeyForAgentAsync(string tenantId, IStorageAgentClient agent, SecretManager secrets, CancellationToken ct)
    {
        string? key;
        try
        {
            key = secrets.TryGetTenantStorageKey(tenantId);
        }
        catch (StorageKeyUnavailableException)
        {
            return (null, Conflict("The storage key of this tenant cannot be decrypted on this Windows profile; restore the recovery kit."));
        }

        if (key is not null)
        {
            return (key, null);
        }

        var status = await agent.GetStatusAsync(ct).ConfigureAwait(false);
        if (status.HasEncryptedData)
        {
            return (null, Conflict("The agent holds encrypted data but the Bridge has no key for it; restore the recovery kit."));
        }

        return (secrets.GetOrCreateTenantStorageKey(tenantId), null);
    }

    private static object KeyView(SecretManager secrets, string tenantId)
    {
        try
        {
            var key = secrets.TryGetTenantStorageKey(tenantId);
            return new
            {
                keyPresent = key is not null,
                keyUnavailable = false,
                keyFingerprint = key is null ? null : StorageKeyFingerprint.Compute(key),
                recoveryKitExportedAt = secrets.GetStorageKeyExportedAt(tenantId),
            };
        }
        catch (StorageKeyUnavailableException)
        {
            return new { keyPresent = true, keyUnavailable = true, keyFingerprint = (string?)null, recoveryKitExportedAt = secrets.GetStorageKeyExportedAt(tenantId) };
        }
    }

    private static async Task<IResult> AgentCallAsync(Func<Task<IResult>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (HubException ex)
        {
            return BadRequest(ex.Message);
        }
#pragma warning disable CA1031 // Any transport failure is reported as "agent not available", like the other tenant endpoints
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            return Results.Json(new { error = "Agent not available: " + ex.Message }, statusCode: 503);
        }
#pragma warning restore CA1031
    }

    private static string SafeName(string tenantId) => new(tenantId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    private static IResult BadRequest(string error) => Results.Json(new { error }, statusCode: 400);

    private static IResult Forbidden() => Results.Json(new { error = "The password is not correct." }, statusCode: 403);

    private static IResult Conflict(string error) => Results.Json(new { error }, statusCode: 409);

    private static IResult NotConnected() => Results.Json(new { error = "Agent not connected" }, statusCode: 503);
}
```

In `src/Cortex.Contained.Bridge/Program.cs`, after `app.MapRecallEndpoints();`:

```csharp

// --- Storage page API (encryption at rest: status, preview, enable, recovery kit export/restore) ---
Cortex.Contained.Bridge.Storage.StorageEndpoints.MapStorageEndpoints(app, cortexDataDir);
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~StorageEndpointsTests|FullyQualifiedName~RecallEndpointsTests" -v q`
Expected: all pass (`StorageEndpointsTests` 12).

- [ ] **Step 5: Commit**

```powershell
git add src/Cortex.Contained.Bridge/Storage/StorageEndpoints.cs src/Cortex.Contained.Bridge/Program.cs tests/Cortex.Contained.Bridge.Tests/StorageEncryption/StorageEndpointsTests.cs
git commit -m "feat(bridge): Storage API — status, preview, enable, recovery kit export and restore" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 19: Storage page (UI)

**Files:**
- Create: `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-storage.js`
- Modify: `src/Cortex.Contained.Bridge/wwwroot/js/components/layout.js` (route)
- Modify: `src/Cortex.Contained.Bridge/wwwroot/app.html` (sidebar link, tab, page block, script tag)

There are no JavaScript unit tests in this repo. Verification is a syntax check plus the manual walk-through in Task 22.

- [ ] **Step 1: Create** `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-storage.js`:

```javascript
"use strict";

/**
 * Per-tenant Storage page: what the agent stores and whether it is encrypted, the recovery kit
 * (export / restore of the tenant storage key, password-protected) and the one-way switch that
 * turns encryption at rest on (preview with pre-flight checks, re-authentication, typed confirmation).
 */
function tenantStoragePage() {
    return {
        get tenantId() { return Alpine.store("router").tenantId; },
        get base() { return `/api/tenants/${encodeURIComponent(this.tenantId)}/storage`; },

        connected: true,
        status: null,
        bridge: null,
        bridgeStores: [],
        statusTimer: null,

        preview: null,
        previewError: "",
        previewing: false,

        kit: { password: "", passphrase: "", passphrase2: "" },
        exporting: false,

        restore: { password: "", passphrase: "", text: "", fileName: "" },
        restoring: false,

        showEnableModal: false,
        enable: { password: "", confirm: "", erase: true, acceptNoKit: false, snapshotTaken: false },
        enabling: false,
        restarting: false,

        async init() {
            await this.loadStatus();
            this.statusTimer = setInterval(() => this.loadStatus(), 5000);
        },

        destroy() {
            if (this.statusTimer) clearInterval(this.statusTimer);
        },

        async loadStatus() {
            try {
                const data = await api.get(`${this.base}/status`);
                this.applyStatus(data);
                this.connected = true;
            } catch (e) {
                this.applyStatus(e.body || {});
                this.connected = false;
            }
            if (this.restarting && this.status?.encryptAtRest && this.status.messagesState === "open" && this.status.memoryState === "open") {
                this.restarting = false;
                Alpine.store("toast").success("Encryption at rest is on and every encrypted store is open");
            }
        },

        applyStatus(data) {
            this.status = data?.agent || null;
            this.bridge = data?.bridge || this.bridge;
            this.bridgeStores = data?.bridgeStores || this.bridgeStores;
        },

        get encrypted() { return !!this.status?.encryptAtRest; },

        get keyMismatch() {
            const expected = this.status?.expectedKeyFingerprint;
            return !!expected && !!this.bridge?.keyFingerprint && expected !== this.bridge.keyFingerprint;
        },

        get needsRestore() {
            return !!this.status?.hasEncryptedData && (!this.bridge?.keyPresent || this.bridge?.keyUnavailable || this.keyMismatch);
        },

        async loadPreview() {
            this.previewing = true;
            this.previewError = "";
            try {
                const data = await api.post(`${this.base}/encryption/preview`);
                this.preview = data?.preview || null;
                this.bridge = data?.bridge || this.bridge;
            } catch (e) {
                this.previewError = e.message;
            }
            this.previewing = false;
        },

        get kitError() {
            if (this.kit.passphrase.length < 12) return "Use a passphrase of at least 12 characters (four random words work well).";
            if (this.kit.passphrase !== this.kit.passphrase2) return "The passphrases do not match.";
            if (!this.kit.password) return "Enter your Bridge password.";
            return "";
        },

        async exportKit() {
            if (this.kitError) return;
            this.exporting = true;
            try {
                const resp = await fetch(`${this.base}/recovery-kit`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ password: this.kit.password, passphrase: this.kit.passphrase }),
                });
                if (resp.status === 401) {
                    window.location.href = "/login.html";
                    return;
                }
                if (!resp.ok) {
                    const err = await resp.json().catch(() => ({ error: `HTTP ${resp.status}` }));
                    throw new Error(err.error || `HTTP ${resp.status}`);
                }
                const disposition = resp.headers.get("Content-Disposition") || "";
                const match = disposition.match(/filename="?([^";]+)"?/);
                const url = URL.createObjectURL(await resp.blob());
                const a = document.createElement("a");
                a.href = url;
                a.download = match ? match[1] : "cortex-recovery-kit.json";
                a.click();
                URL.revokeObjectURL(url);
                Alpine.store("toast").success("Recovery kit downloaded. Keep the file and the passphrase in separate safe places.");
                this.kit = { password: "", passphrase: "", passphrase2: "" };
                await this.loadStatus();
            } catch (e) {
                Alpine.store("toast").error("Recovery kit: " + e.message);
            }
            this.exporting = false;
        },

        async onKitFile(event) {
            const file = event.target.files?.[0];
            this.restore.text = file ? await file.text() : "";
            this.restore.fileName = file ? file.name : "";
        },

        async restoreKit() {
            this.restoring = true;
            try {
                const r = await api.post(`${this.base}/recovery-kit/restore`, {
                    password: this.restore.password,
                    passphrase: this.restore.passphrase,
                    kit: this.restore.text,
                });
                Alpine.store("toast").success(r?.message || "Key restored");
                this.restore = { password: "", passphrase: "", text: "", fileName: "" };
                await this.loadStatus();
            } catch (e) {
                Alpine.store("toast").error("Restore: " + e.message);
            }
            this.restoring = false;
        },

        get canEnable() {
            return this.preview && !this.preview.alreadyEnabled && this.preview.blockers.length === 0
                && this.enable.password && this.enable.confirm === "ENCRYPT" && this.enable.snapshotTaken
                && (this.bridge?.recoveryKitExportedAt || this.enable.acceptNoKit);
        },

        async openEnable() {
            await this.loadPreview();
            this.enable = { password: "", confirm: "", erase: true, acceptNoKit: false, snapshotTaken: false };
            this.showEnableModal = true;
        },

        async confirmEnable() {
            this.enabling = true;
            try {
                const r = await api.post(`${this.base}/encryption/enable`, {
                    password: this.enable.password,
                    confirm: this.enable.confirm,
                    eraseEvaluationArtifacts: this.enable.erase,
                    acceptNoRecoveryKit: this.enable.acceptNoKit,
                });
                this.showEnableModal = false;
                this.restarting = !!r?.restartScheduled;
                Alpine.store("toast").success(r?.restartScheduled
                    ? "Encryption enabled; the agent restarts and encrypts its stores (see the status below)"
                    : "Encryption enabled; restart the agent to apply it");
            } catch (e) {
                const errors = e.body?.errors?.join(" ") || e.message;
                Alpine.store("toast").error("Not enabled: " + errors);
            }
            this.enabling = false;
            await this.loadStatus();
        },

        badge(state) {
            return state === "encrypted" || state === "open" ? "bg-success"
                : state === "plaintext" ? "bg-warning text-dark"
                : state === "mixed" || state === "awaitingKey" ? "bg-info text-dark"
                : state === "failed" || state === "rejected" ? "bg-danger"
                : "bg-secondary";
        },

        bytes(n) {
            if (!n) return "0 B";
            const units = ["B", "KB", "MB", "GB"];
            let i = 0;
            while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
            return `${n.toFixed(i ? 1 : 0)} ${units[i]}`;
        },

        fingerprint(fp) {
            return fp ? fp.match(/.{1,4}/g).join("-") : "—";
        },

        formatDateTime(ts) {
            return ts ? new Date(ts).toLocaleString() : "never";
        },
    };
}
```

- [ ] **Step 2: Route** — in `js/components/layout.js` replace:

```javascript
            // /tenants/{id}/settings|history|memory|recall
            const match = path.match(/^\/tenants\/([^/]+)\/(settings|history|memory|recall)$/);
```

with:

```javascript
            // /tenants/{id}/settings|history|memory|recall|storage
            const match = path.match(/^\/tenants\/([^/]+)\/(settings|history|memory|recall|storage)$/);
```

(The server already serves `app.html` for `/tenants/{**slug}`.)

- [ ] **Step 3: `app.html` — sidebar, tab, script**

After the sidebar's Recall link (the `<a>` whose label is `Recall`), add:

```html
                                <a :href="`/tenants/${encodeURIComponent(t.tenantId)}/storage`"
                                   class="sidebar-item sidebar-sub-item"
                                   :class="{ active: page === 'tenant-storage' && $store.router.tenantId === t.tenantId }"
                                   @click.prevent="nav(`/tenants/${encodeURIComponent(t.tenantId)}/storage`)">
                                    <span class="sidebar-item-icon"><i class="bi bi-shield-lock"></i></span>
                                    <span class="sidebar-item-label">Storage</span>
                                </a>
```

In the shared tabs block, add `'tenant-storage'` to the page list:

```html
        <template x-if="['tenant-settings','tenant-history','tenant-channel-history','tenant-memory','tenant-recall','tenant-storage'].includes($store.router.page)">
```

and after the Recall tab button:

```html
                        <button class="btn btn-sm"
                                :class="isActive('storage') ? 'btn-primary' : 'btn-outline-secondary'"
                                :disabled="isActive('storage')" @click="navSub('storage')">
                            <i class="bi bi-shield-lock me-1"></i>Storage
                        </button>
```

After `<script src="/js/pages/tenant-recall.js"></script>` add:

```html
<script src="/js/pages/tenant-storage.js"></script>
```

- [ ] **Step 4: `app.html` — the page block.** Insert it right before `<!-- ── Global Settings ────────────────────────────────── -->`, after the closing `</template>` of the Recall page:

```html
        <!-- ── Tenant Storage (encryption at rest, recovery kit) ── -->
        <template x-if="$store.router.page === 'tenant-storage'">
        <div x-data="tenantStoragePage()" class="container pb-3" style="max-width: 860px;">
            <!-- Sub-nav is rendered above by the shared tenantTabs() block. -->

            <div x-show="!connected" class="alert alert-warning py-2">
                <i class="bi bi-plug me-1"></i>The agent is not connected; its storage status is unavailable.
            </div>
            <div x-show="needsRestore" class="alert alert-danger py-2">
                <i class="bi bi-shield-exclamation me-1"></i>The agent's data is encrypted with a key the Bridge does not hold. Restore the recovery kit below.
            </div>
            <div x-show="restarting" class="alert alert-info py-2">
                <span class="spinner-border spinner-border-sm me-1"></span>The agent is restarting and encrypting its stores…
            </div>

            <!-- ═══ Encryption at rest ═══ -->
            <div class="card mb-3">
                <div class="card-body">
                    <h6 class="text-uppercase text-secondary border-bottom pb-2 mb-3">Encryption at rest</h6>
                    <template x-if="status">
                    <div>
                        <p class="mb-2">
                            <span class="badge" :class="encrypted ? 'bg-success' : 'bg-warning text-dark'" x-text="encrypted ? 'On' : (status.restartPending ? 'Pending restart' : 'Off')"></span>
                            <span class="small text-body-secondary ms-1" x-show="encrypted" x-text="'since ' + formatDateTime(status.enabledAt) + ' (' + status.source + ')'"></span>
                        </p>
                        <div class="small mb-2">
                            Key: <span class="badge" :class="badge(status.keyState)" x-text="status.keyState"></span>
                            fingerprint <code x-text="fingerprint(status.keyFingerprint)"></code>
                            · messages <span class="badge" :class="badge(status.messagesState)" x-text="status.messagesState"></span>
                            · memory <span class="badge" :class="badge(status.memoryState)" x-text="status.memoryState"></span>
                        </div>
                        <table class="table table-sm small mb-2">
                            <thead><tr><th>Store</th><th>Path</th><th>State</th><th class="text-end">Size</th></tr></thead>
                            <tbody>
                                <template x-for="item in status.items" :key="item.id">
                                    <tr :title="item.detail || ''">
                                        <td x-text="item.label"></td>
                                        <td><code x-text="item.path"></code></td>
                                        <td><span class="badge" :class="badge(item.state)" x-text="item.state"></span></td>
                                        <td class="text-end" x-text="bytes(item.bytes) + (item.files != null ? ' · ' + item.files + ' files' : '')"></td>
                                    </tr>
                                </template>
                            </tbody>
                        </table>
                    </div>
                    </template>
                    <div class="fw-semibold small mb-1">On this Windows host (Bridge)</div>
                    <ul class="small mb-3">
                        <template x-for="store in bridgeStores" :key="store.id">
                            <li><span x-text="store.label"></span> <span class="badge" :class="badge(store.state)" x-text="store.state"></span>
                                <span class="text-body-secondary" x-text="' — ' + store.detail"></span></li>
                        </template>
                    </ul>
                    <button class="btn btn-sm btn-outline-danger" x-show="status && !encrypted && !status.restartPending" :disabled="previewing" @click="openEnable()">
                        <i class="bi bi-shield-lock me-1"></i>Turn on encryption…
                    </button>
                </div>
            </div>

            <!-- ═══ Recovery kit ═══ -->
            <div class="card mb-3">
                <div class="card-body">
                    <h6 class="text-uppercase text-secondary border-bottom pb-2 mb-3">Recovery kit</h6>
                    <p class="small text-body-secondary">
                        The storage key lives in this Windows profile (DPAPI). If the profile is reset or lost, the encrypted history and
                        memories can only be opened with the recovery kit: the key encrypted with a passphrase you choose.
                        Bridge key fingerprint <code x-text="fingerprint(bridge?.keyFingerprint)"></code>;
                        last exported <span x-text="formatDateTime(bridge?.recoveryKitExportedAt)"></span>.
                    </p>
                    <div class="row g-2 mb-2">
                        <div class="col-md-4"><input type="password" class="form-control form-control-sm" placeholder="Bridge password" x-model="kit.password" autocomplete="current-password"></div>
                        <div class="col-md-4"><input type="password" class="form-control form-control-sm" placeholder="Kit passphrase (12+ chars)" x-model="kit.passphrase" autocomplete="new-password"></div>
                        <div class="col-md-4"><input type="password" class="form-control form-control-sm" placeholder="Repeat passphrase" x-model="kit.passphrase2" autocomplete="new-password"></div>
                    </div>
                    <div class="small text-danger mb-2" x-show="kit.passphrase && kitError" x-text="kitError"></div>
                    <button class="btn btn-sm btn-primary" :disabled="!!kitError || exporting" @click="exportKit()">
                        <span x-show="exporting" class="spinner-border spinner-border-sm me-1"></span><i class="bi bi-download me-1"></i>Download recovery kit
                    </button>

                    <details class="mt-3" :open="needsRestore">
                        <summary class="small">Restore a recovery kit</summary>
                        <p class="small text-body-secondary mt-2">
                            Only when the Bridge lost the key (new Windows profile, restored backup). A working key is never replaced.
                        </p>
                        <div class="row g-2 mb-2">
                            <div class="col-md-4"><input type="file" accept=".json,application/json" class="form-control form-control-sm" @change="onKitFile($event)"></div>
                            <div class="col-md-4"><input type="password" class="form-control form-control-sm" placeholder="Kit passphrase" x-model="restore.passphrase"></div>
                            <div class="col-md-4"><input type="password" class="form-control form-control-sm" placeholder="Bridge password" x-model="restore.password" autocomplete="current-password"></div>
                        </div>
                        <button class="btn btn-sm btn-outline-primary" :disabled="!restore.text || !restore.passphrase || !restore.password || restoring" @click="restoreKit()">
                            <span x-show="restoring" class="spinner-border spinner-border-sm me-1"></span>Restore key
                        </button>
                    </details>
                </div>
            </div>

            <!-- Enable confirmation modal -->
            <div x-show.important="showEnableModal" class="modal d-block" tabindex="-1" style="background: rgba(0,0,0,0.5);">
                <div class="modal-dialog modal-lg">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title">Turn on encryption at rest</h5>
                            <button type="button" class="btn-close" @click="showEnableModal = false"></button>
                        </div>
                        <div class="modal-body small" x-show="preview">
                            <template x-for="w in preview?.warnings || []" :key="w"><div class="alert alert-warning py-1 px-2 mb-1" x-text="w"></div></template>
                            <template x-for="b in preview?.blockers || []" :key="b"><div class="alert alert-danger py-1 px-2 mb-1" x-text="b"></div></template>
                            <div class="fw-semibold mt-2">Will be encrypted</div>
                            <ul class="mb-1"><template x-for="i in preview?.willEncrypt || []" :key="i.id"><li x-text="i.label + ' (' + bytes(i.bytes) + ')'"></li></template></ul>
                            <div class="fw-semibold" x-show="preview?.willErase?.length">Will be erased (if checked below)</div>
                            <ul class="mb-1"><template x-for="i in preview?.willErase || []" :key="i.id"><li x-text="i.label + ' (' + bytes(i.bytes) + ')'"></li></template></ul>
                            <div class="fw-semibold" x-show="preview?.notCovered?.length">Not covered (stay in plaintext)</div>
                            <ul class="mb-2"><template x-for="i in preview?.notCovered || []" :key="i.id"><li><span x-text="i.label"></span> <span class="text-body-secondary" x-text="'— ' + i.detail"></span></li></template></ul>
                            <p class="mb-2" x-text="'Free space: ' + bytes(preview?.availableFreeBytes) + ' (needs ' + bytes(preview?.requiredFreeBytes) + ').'"></p>
                            <div class="form-check"><input class="form-check-input" type="checkbox" id="enc-snapshot" x-model="enable.snapshotTaken">
                                <label class="form-check-label" for="enc-snapshot">I took a snapshot of the agent's state volume (see the guide)</label></div>
                            <div class="form-check" x-show="!bridge?.recoveryKitExportedAt"><input class="form-check-input" type="checkbox" id="enc-nokit" x-model="enable.acceptNoKit">
                                <label class="form-check-label" for="enc-nokit">I have not downloaded a recovery kit and accept that losing this Windows profile loses every message and memory</label></div>
                            <div class="form-check mb-2" x-show="preview?.willErase?.length"><input class="form-check-input" type="checkbox" id="enc-erase" x-model="enable.erase">
                                <label class="form-check-label" for="enc-erase">Erase the plaintext evaluation copies</label></div>
                            <div class="row g-2">
                                <div class="col-md-6"><input type="password" class="form-control form-control-sm" placeholder="Bridge password" x-model="enable.password" autocomplete="current-password"></div>
                                <div class="col-md-6"><input type="text" class="form-control form-control-sm" placeholder="Type ENCRYPT" x-model="enable.confirm"></div>
                            </div>
                        </div>
                        <div class="modal-footer">
                            <button class="btn btn-outline-secondary btn-sm" @click="showEnableModal = false">Cancel</button>
                            <button class="btn btn-danger btn-sm" :disabled="!canEnable || enabling" @click="confirmEnable()">
                                <span x-show="enabling" class="spinner-border spinner-border-sm me-1"></span>Encrypt and restart the agent
                            </button>
                        </div>
                    </div>
                </div>
            </div>
        </div>
        </template>

```

- [ ] **Step 5: Check**

Run: `node --check src\Cortex.Contained.Bridge\wwwroot\js\pages\tenant-storage.js; node --check src\Cortex.Contained.Bridge\wwwroot\js\components\layout.js; dotnet build src\Cortex.Contained.Bridge -v q`
Expected: no output from `node`; the build succeeds with 0 warnings.

- [ ] **Step 6: Commit**

```powershell
git add src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-storage.js src/Cortex.Contained.Bridge/wwwroot/js/components/layout.js src/Cortex.Contained.Bridge/wwwroot/app.html
git commit -m "feat(ui): Storage page — encryption status, recovery kit, one-way enable with pre-flight" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 20: End-to-end integration test

**Files:**
- Test: `tests/Cortex.Contained.Integration.Tests/StorageEncryptionIntegrationTests.cs`

- [ ] **Step 1: Write the test** (it exercises Tasks 1–11 together and needs no production change):

```csharp
using System.Text;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Contained.Integration.Tests;

/// <summary>
/// The owner's switch end to end: a plaintext agent is enabled over the hub, "restarts" (a second host on the
/// same volumes), receives the key and encrypts messages and memories, leaving no plaintext marker on disk.
/// </summary>
[Trait("Category", "Integrity")]
public sealed class StorageEncryptionIntegrationTests : IDisposable
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), "cortex-integration-tests", "enc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task EnableThenRestart_EncryptsMessagesAndMemoriesAndLeavesNoPlaintext()
    {
        await using (var plaintext = new StorageHostFactory(this.root))
        {
            await using var hub = await ConnectAsync(plaintext);
            await plaintext.Services.GetRequiredService<MessageStore>().SaveMessageAsync("u", "ch", "user", "zqe2emessage", T0);
            await plaintext.Services.GetRequiredService<IMemoryStore>().StoreMemoryAsync("m1", "zqe2ememory", Chunk("m1"), [Vector()]);

            Assert.False((await hub.InvokeAsync<StorageEncryptionStatusDto>(nameof(IAgentHub.GetStorageEncryptionStatus))).EncryptAtRest);
            var result = await hub.InvokeAsync<StorageEncryptionEnableResultDto>(
                nameof(IAgentHub.EnableStorageEncryption), new StorageEncryptionEnableRequestDto { KeyFingerprint = StorageKeyFingerprint.Compute(Key) });

            Assert.True(result.Accepted);
            Assert.True((await hub.InvokeAsync<StorageEncryptionStatusDto>(nameof(IAgentHub.GetStorageEncryptionStatus))).RestartPending);
        }

        SqliteConnection.ClearAllPools();
        await using (var encrypted = new StorageHostFactory(this.root))
        {
            await using var hub = await ConnectAsync(encrypted);
            var status = await WaitForOpenAsync(hub);

            Assert.Equal("marker", status.Source);
            Assert.Equal("zqe2emessage", Assert.Single(await encrypted.Services.GetRequiredService<MessageStore>().GetMessagesAsync("ch")).Content);
            Assert.Equal("zqe2ememory", (await encrypted.Services.GetRequiredService<IMemoryStore>().GetMemoryAsync("m1"))!.Content);
            Assert.All(status.Items.Where(i => i.Id is "messages" or "memory-db" or "memory-content"), i => Assert.Equal(StorageItemStates.Encrypted, i.State));
        }

        SqliteConnection.ClearAllPools();
        Assert.False(this.AnyStateFileContains("zqe2emessage"));
        Assert.False(this.AnyStateFileContains("zqe2ememory"));
    }

    private static async Task<HubConnection> ConnectAsync(StorageHostFactory factory)
    {
        var hub = factory.CreateHubConnection();
        await hub.StartAsync();
        Assert.True(await hub.InvokeAsync<bool>(nameof(IAgentHub.ProvideStorageKey), new StorageKeyGrant { Key = Key }));
        return hub;
    }

    private static async Task<StorageEncryptionStatusDto> WaitForOpenAsync(HubConnection hub)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = await hub.InvokeAsync<StorageEncryptionStatusDto>(nameof(IAgentHub.GetStorageEncryptionStatus));
            if (status.MessagesState == "open" && status.MemoryState == "open")
            {
                return status;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The encrypted stores did not open.");
    }

    private static List<ChunkRecord> Chunk(string memoryId) =>
    [
        new ChunkRecord { MemoryId = memoryId, ChunkIndex = 0, StartOffset = 0, Length = 11, Title = null, Tags = [], CreatedAt = T0, UpdatedAt = T0 },
    ];

    private static float[] Vector()
    {
        var vector = new float[1024];
        vector[0] = 1f;
        return vector;
    }

    private bool AnyStateFileContains(string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(this.root, "state"), "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (copy.ToArray().AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class StorageHostFactory(string root) : WebApplicationFactory<AgentHub>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("CORTEX_HUB_TOKEN", AgentHostFactory.TestHubToken);
            builder.UseSetting("CORTEX_DATA_PATH", Path.Combine(root, "data"));
            builder.UseSetting("CORTEX_STATE_PATH", Path.Combine(root, "state"));
            builder.UseSetting("MemoryMcp:DataDirectory", Path.Combine(root, "state", "memory"));
            builder.UseSetting("MemoryMcp:Ollama:Endpoint", "http://127.0.0.1:1");
            Directory.CreateDirectory(Path.Combine(root, "data"));
            Directory.CreateDirectory(Path.Combine(root, "state"));
        }

        public HubConnection CreateHubConnection()
        {
            var server = this.Server;
            return new HubConnectionBuilder()
                .WithUrl(new Uri(server.BaseAddress, "/hub/agent"), options =>
                {
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(AgentHostFactory.TestHubToken);
                })
                .Build();
        }
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests\Cortex.Contained.Integration.Tests --filter "FullyQualifiedName~StorageEncryptionIntegrationTests" -v q`
Expected: `Passed: 1` (about 10 s).

- [ ] **Step 3: Commit**

```powershell
git add tests/Cortex.Contained.Integration.Tests/StorageEncryptionIntegrationTests.cs
git commit -m "test(storage): enable over the hub, restart, migrate — no plaintext left in the state volume" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 21: Documentation

**Files:**
- Create: `docs/encryption-at-rest.md`
- Modify: `docs/security.md`, `docs/event-log.md`, `docs/architecture.md`, `docs/api-reference.md`

- [ ] **Step 1: Create `docs/encryption-at-rest.md`**

````markdown
# Encryption at rest

Cortex can encrypt everything the agent keeps about your conversations and memories on disk. It uses a per-tenant
key that the Bridge holds under Windows DPAPI and pushes to the agent, which keeps it in memory only.

Encryption is **off by default**. You turn it on once, per tenant, on the **Storage** page, and it stays on.
Design: [encryption at rest, complete](superpowers/specs/2026-11-22-encryption-at-rest-complete-design.md).

## What is covered

| Store | Where (agent state volume) | How |
|---|---|---|
| Message history and event log | `messages.db` (+ WAL) | SQLite3 Multiple Ciphers, migrated in place (VACUUM + rekey) |
| History search index | `history/history-index.<n>.db` | SQLite3 Multiple Ciphers; rebuilt encrypted |
| Memory index (metadata, vectors, keyword index) | `memory/memory.db` | SQLite3 Multiple Ciphers, migrated in place |
| Memory content | `memory/memories/*.memory.data` | AES-256-GCM per file, written atomically; plaintext overwritten during migration |
| Live sessions saved at shutdown | `sessions-snapshot.json` | AES-256-GCM; never written in plaintext once encryption is on |

Each store uses its own key, derived with HKDF-SHA256 from the tenant master key: `messages.db`,
`history-index`, `memory.db`, `memory-content` and `sessions-snapshot`. Without the key nothing opens and nothing
is created. A wrong key changes no byte on disk.

## What is not covered

| Item | Why |
|---|---|
| Subagent transcripts (`subagents/`) | kept 24 h (records 7 days), erased by history deletion |
| To-do lists, scheduled tasks, coding sessions, voiceprints, maintenance counters | small operational state, or no message text |
| `self-notes.md`, `context-bootstrap.md` | notes the agent reads and edits every turn |
| Tool sandbox (`/app/data`), including `tool-output/` spill files | the agent's tools must read them; spill files are deleted after 7 days and by history deletion |
| Logs | contain no message or memory text (guarded by tests) |
| Bridge recording sessions | opt-in debugging recorder |
| Volume snapshots, host backups, provider-side copies, filesystem free space | outside what the application can reach |

Evaluation runs (`eval/`) hold raw plaintext copies of your history and memories. Enabling erases them by default,
and `history-eval`, `history-smoke` and `memory-eval` refuse to run while encryption is on.

The Bridge itself keeps no message history. `%LOCALAPPDATA%\Cortex\messages.db`, if present, is an unused file
from older versions. It is encrypted with the Bridge database key, so you can delete it.

## Turning it on

You need:

- a deployed build with the Storage page;
- the Bridge password;
- free space on the state volume, at least the size of the largest database plus the history index plus 64 MB
  (the preview checks this).

### 1. Snapshot the state volume

The snapshot is your way back. It is **plaintext**: keep it offline and destroy it once everything is verified
(step 5).

```powershell
docker stop cortex-agent
New-Item -ItemType Directory -Force "$env:USERPROFILE\cortex-backups" | Out-Null
docker run --rm --user root --entrypoint tar `
  -v cortex-contained_cortex-state:/state:ro `
  -v "$env:USERPROFILE\cortex-backups:/backup" `
  cortex-agent:latest czf /backup/cortex-state-before-encryption.tgz -C /state .
docker start cortex-agent
```

### 2. Download the recovery kit

1. On **Tenants → *tenant* → Storage → Recovery kit**, enter the Bridge password and a passphrase of at least 12
   characters (four random words are a good passphrase).
2. Click **Download recovery kit**.
3. Store the file and the passphrase in different safe places, for example the file on a USB stick and the
   passphrase in a password manager.

The kit holds the tenant key encrypted with PBKDF2-HMAC-SHA256 (600,000 iterations) and AES-256-GCM. Without the
passphrase it reveals only the key fingerprint.

### 3. Preview and enable

1. Click **Turn on encryption…**. The preview lists what will be encrypted, what will be erased and what stays
   plaintext, checks the free space, and shows warnings.
2. Tick the snapshot checkbox.
3. Type the Bridge password and `ENCRYPT`.
4. Click **Encrypt and restart the agent**.

The agent writes `storage-encryption.json` (one-way) and restarts. After the Bridge pushes the key, the agent
encrypts `messages.db`, `memory.db` and the memory files. The history index rebuilds; until it finishes, history
search covers recent messages only.

### 4. Verify

```powershell
(Invoke-RestMethod http://127.0.0.1:5100/health).subsystems.storage    # status ok, messages open, memory open
docker exec cortex-agent sh -c "head -c 16 /app/state/messages.db | od -c | head -1; head -c 16 /app/state/memory/memory.db | od -c | head -1"
# Neither line may show "S Q L i t e   f o r m a t   3".
# Replace PHRASE with words you know are in a recent message and in a memory:
docker exec cortex-agent sh -c "grep -rl --binary-files=text 'PHRASE' /app/state/messages.db* /app/state/history /app/state/memory /app/state/sessions-snapshot.json 2>/dev/null || echo 'no plaintext hits'"
```

Then check four things:

- The Storage page shows every covered item as `encrypted`.
- In chat, ask the agent about something from last week (history search).
- Open the Memory page.
- Ask the agent to recall a memory.

### 5. Clean up the plaintext copies

- Delete `cortex-state-before-encryption.tgz` once you are confident (it is a plaintext copy of everything).
- Entries listed as *Not recognized* on the Storage page (for example old manual backups) may hold plaintext. Delete
  them with `docker exec cortex-agent rm -rf /app/state/<name>` if they do.
- Optionally delete the legacy `%LOCALAPPDATA%\Cortex\messages.db`.

## Restoring after a Windows profile reset

DPAPI keys belong to the Windows user profile. After a profile reset the Bridge cannot decrypt the tenant key. It
never creates a new one over encrypted data, so the agent stays locked, and the Storage page and the log say so.

To restore access:

1. Open **Storage → Restore a recovery kit**.
2. Choose the kit file, then enter its passphrase and the Bridge password.

The Bridge installs the key and pushes it. If the agent has already accepted another key in this process, restart
it.

A working key is never replaced. A kit is accepted only when the Bridge has no usable key, or when the agent
reports that its data belongs to the kit's key.

## Status and health

- **Storage page.** Shows the mode and why it is on (`config`, `marker`, `detected`), the key state and
  fingerprint, every store with its state and size, and the Bridge stores.
- **`/health`.** Has `subsystems.storage = { status: off|ok|degraded, encryptAtRest, source, restartPending, key,
  messages, memory }`.

## Rollback and downgrades

- Encryption cannot be turned off from the UI. The way back is the snapshot from step 1, which loses everything
  written since.
- **Do not install an agent image older than this feature** on encrypted volumes. It cannot open the stores and
  fails at startup. It never writes plaintext next to them, but the agent is down until you upgrade again.
  Self-Update rolls back only to the previously deployed image.
- If the configuration is reset or the marker is deleted, the agent detects the encrypted files and stays
  encrypted.
- Bridges since increment 2 keep unknown `secrets.json` fields. Older Bridges drop the tenant keys: keep the
  recovery kit.

## Configuration (advanced)

- `Storage:EncryptAtRest` (env `Storage__EncryptAtRest`) also turns encryption on. With the Storage page you do not
  need it.
- `Storage:KeyWaitSeconds` (default 120) is how long requests wait for the key before failing closed.
````

- [ ] **Step 2: Update `docs/security.md`**

  1. **Secret storage → Contents.** Replace `- Database encryption key (for message history)` with:

     ```markdown
     - Bridge database key (encrypts the MCP action ledger `mcp\actions.db`)
     - Per-tenant storage keys (encryption at rest of the agent's stores; never regenerated) and the time each tenant's recovery kit was last exported
     ```

  2. **"## Message history encryption".** Replace the whole section (heading and paragraph) with:

     ```markdown
     ## Bridge-side stores

     The Bridge keeps no message history: the agent owns `messages.db`. A `%LOCALAPPDATA%\Cortex\messages.db` left by older Bridge versions is SQLite3 Multiple Ciphers-encrypted with the DPAPI-held Bridge database key and unused; delete it when you no longer need it. The Storage page reports both Bridge stores.
     ```

  3. **"## Curated memory keyword index".** Replace the sentence (it wraps over two lines) `Like those files and the vectors, the index is **not** encrypted by \`Storage:EncryptAtRest\` (the memory store is outside that key).` with:

     ```markdown
     With encryption at rest the whole memory store — the index, the vectors and the `.memory.data` files — is encrypted with the tenant key (see below).
     ```

  4. **"## Agent storage encryption and event payloads".** Replace the first paragraph (from `With \`Storage:EncryptAtRest\`` to `attachment bytes are never persisted.`) with:

     ```markdown
     Encryption at rest is turned on per tenant from the Bridge **Storage** page (one-way marker `storage-encryption.json` in the state volume; `Storage:EncryptAtRest` still works) and covers `messages.db`, the history index generations, `memory.db` (metadata, sqlite-vec vectors, FTS5 keyword index), every `.memory.data` file (AES-256-GCM) and the shutdown session snapshot. Each store has its own HKDF-derived key; the per-tenant master key is DPAPI-protected on the Bridge, never regenerated, pushed before credentials and held in memory only, and the agent fails closed without it. Logs carry no message or memory text (guarded by tests). Not covered: `subagents.db` (24 h transcripts), to-do lists, scheduled tasks, voiceprints, self-notes and the tool sandbox — see [encryption-at-rest.md](encryption-at-rest.md). The owner can export a passphrase-wrapped recovery kit (re-authentication required; never logged) to survive a Windows profile reset. Tool payloads are persisted under an exact allowlist with `SensitiveDataRedactor.RedactForPersistence`; `mcp__*` payloads, file/page/mail bodies and attachment bytes are never persisted.
     ```

  5. **The "**Downgrades:**" paragraph.** Append:

     ```markdown
      Agent images older than the Storage page cannot open encrypted stores and stop at startup rather than write plaintext; the Bridge never mints a tenant key while the agent reports encrypted data, and the agent rejects any key whose fingerprint differs from the one recorded when encryption was enabled.
     ```

- [ ] **Step 3: Update `docs/event-log.md` → "## Encryption at rest"**

Replace the first paragraph with:

```markdown
Encryption at rest is enabled from the Bridge **Storage** page (or `Storage:EncryptAtRest: true`) and covers `messages.db`, every history index generation, the memory store and the session snapshot (see [encryption-at-rest.md](encryption-at-rest.md)). The per-tenant master key lives in the Bridge's DPAPI secrets file (never regenerated if it cannot be decrypted) and is pushed to the agent before credentials on every connect; the agent holds it in memory only and derives one key per store.
```

Add a row to the table:

```markdown
| Plaintext memory store | `memory.db` migrated like `messages.db`; content files encrypted one by one (crash-safe, plaintext overwritten) |
```

Replace the last paragraph with:

```markdown
Encryption is one-way: the marker `storage-encryption.json` keeps it on across configuration resets, and older images cannot read the encrypted files. Keep the recovery kit — losing the Windows profile that holds the DPAPI key otherwise loses the history. The evaluation CLIs (`history-smoke`, `history-eval`, `memory-eval`) refuse to run while encryption is on.
```

- [ ] **Step 4: Update `docs/architecture.md` (Data persistence)**

In "Inside Docker container" replace the row ``| Memories (encrypted DB) | `/app/state/memory/memory.db` | SQLite (SQLCipher) + sqlite-vec |`` with:

```markdown
| Memories | `/app/state/memory/memory.db` | SQLite + sqlite-vec + FTS5; SQLite3MC-encrypted with encryption at rest |
| Memory content | `/app/state/memory/memories/*.memory.data` | Text files; AES-256-GCM with encryption at rest |
| Session snapshot | `/app/state/sessions-snapshot.json` | Live sessions saved at shutdown; AES-256-GCM with encryption at rest |
| Encryption marker | `/app/state/storage-encryption.json` | One-way switch written by the Storage page |
```

In "Outside container" replace ``| Message history | `messages.db` | Encrypted SQLite |`` with:

```markdown
| MCP action ledger | `mcp\actions.db` | SQLite3MC-encrypted (Bridge database key) |
| Legacy message history (unused) | `messages.db` | Left by older Bridge versions; encrypted, nothing writes it |
```

- [ ] **Step 5: Update `docs/api-reference.md`**

Under `### Bridge`, after the `/api/recall/embeddings-gpu` PUT row, add:

```markdown
| GET | `/api/tenants/{tenantId}/storage/status` | Yes | `{ connected, agent: StorageEncryptionStatusDto, bridge: { keyPresent, keyUnavailable, keyFingerprint, recoveryKitExportedAt }, bridgeStores: [{ id, label, state, detail }] }` (`503` with `connected: false` and the Bridge view while disconnected). Never returns key material |
| POST | `/api/tenants/{tenantId}/storage/encryption/preview` | Yes | Changes nothing. `{ preview: { alreadyEnabled, keyFingerprint, willEncrypt[], willErase[], notCovered[], requiredFreeBytes, availableFreeBytes, requiredTempBytes, availableTempBytes, blockers[], warnings[] }, bridge }` |
| POST | `/api/tenants/{tenantId}/storage/encryption/enable` | Yes + password | Body `{ password, confirm: "ENCRYPT", eraseEvaluationArtifacts = true, acceptNoRecoveryKit = false }`. `403` wrong password; `400` without the confirmation or without an exported kit (unless accepted); `409` when the agent holds encrypted data the Bridge has no key for, or refuses the key. Pushes the key, then `202` with `{ accepted, alreadyEnabled, restartScheduled, errors[] }` (one-way; the agent restarts) |
| POST | `/api/tenants/{tenantId}/storage/recovery-kit` | Yes + password | Body `{ password, passphrase }` (passphrase ≥ 12). Returns the kit file (`application/json`, `Cache-Control: no-store`) and records the export time |
| POST | `/api/tenants/{tenantId}/storage/recovery-kit/restore` | Yes + password | Body `{ password, passphrase, kit }`. Installs the kit key only when the Bridge has no usable key or the agent reports its data belongs to the kit's key; `409` otherwise; `400` wrong passphrase or another tenant's kit. Returns `{ restored, fingerprint, applied, message }` |
```

After the `#### Recall (\`IRecallHub\`)` table, add:

```markdown
#### Storage (`IStorageHub`)

| Method | Description |
|--------|-------------|
| `GetStorageEncryptionStatus()` -> `StorageEncryptionStatusDto` | Mode and source (`off`/`config`/`marker`/`detected`), restart pending, key state and fingerprints, store states, every item (header-only inventory) |
| `PreviewStorageEncryption()` -> `StorageEncryptionPreviewDto` | What enabling would encrypt, erase and leave alone; disk-space pre-flight; changes nothing |
| `EnableStorageEncryption(StorageEncryptionEnableRequestDto)` -> `StorageEncryptionEnableResultDto` | Requires the pushed key with the given fingerprint; erases evaluation copies on request; writes the one-way marker; stops the agent so Docker restarts it encrypted |
```

In the Agent Host table, change the `/health` description to: `Health check with subsystem status (incl. \`storage\`: encryption mode and whether the encrypted stores are open)`.

- [ ] **Step 6: Check names and links**

Run: `Select-String -Path docs\encryption-at-rest.md,docs\security.md -Pattern 'C:\\Users\\'`
Expected: no output (no host paths or user names in the docs).

- [ ] **Step 7: Commit**

```powershell
git add docs/encryption-at-rest.md docs/security.md docs/event-log.md docs/architecture.md docs/api-reference.md
git commit -m "docs: encryption at rest — owner guide, coverage, recovery kit, rollback; Bridge store facts corrected" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 22: Final verification

- [ ] **Step 1: Whole solution, zero warnings**

Run: `dotnet build cortex-contained.sln -v q`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 2: Every unit/integration test project (the Self-Update test gate)**

```powershell
Get-ChildItem tests -Directory -Filter *.Tests | ForEach-Object { dotnet test $_.FullName --no-build -v q }
dotnet test lib\memory-mcp\tests\MemoryMcp.Core.Tests -v q
```

Expected: every project reports `Passed!`.
- If `SubAgentSetGoalToolTests` fails, rerun `tests\Cortex.Contained.Agent.Host.Tests` once (pre-existing load flake).
- Counts from the plan's replay: Agent.Host ≈ 2,826, Bridge ≈ 1,990, Contracts 265, Integration 18, memory-mcp 142.

- [ ] **Step 3: Integrity gate**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "Category=Integrity" -v q` and `dotnet test tests\Cortex.Contained.Integration.Tests --filter "Category=Integrity" -v q`
Expected: Agent.Host 63 passed, Integration 1 passed.

- [ ] **Step 4: Local startup smoke (no Docker, nothing deployed)**

```powershell
$root = Join-Path (Get-Location) ".enc-smoke"; New-Item -ItemType Directory -Force "$root\state","$root\data" | Out-Null
$env:CORTEX_HUB_TOKEN='smoke'; $env:CORTEX_STATE_PATH="$root\state"; $env:CORTEX_DATA_PATH="$root\data"
$env:MemoryMcp__DataDirectory="$root\state\memory"; $env:Storage__EncryptAtRest='true'; $env:MemoryMcp__Ollama__Endpoint='http://127.0.0.1:1'
dotnet run --project src\Cortex.Contained.Agent.Host --no-build   # in a second terminal:
(Invoke-RestMethod http://localhost:5208/health).subsystems.storage
Test-Path "$root\state\messages.db"; Test-Path "$root\state\memory\memory.db"
```

Expected:
- `/health` reports `status: degraded`, `encryptAtRest: true`, `source: config`, `key: absent`, `messages: awaitingKey`, `memory: awaitingKey`.
- Both `Test-Path` calls return `False`: nothing is created before the key arrives.

Stop the process (`Stop-Process -Id <pid>`) and delete `.enc-smoke`.

- [ ] **Step 5: Hygiene**

```powershell
git status --short                       # only intended files; version.json untouched
git --no-pager diff main --stat -- version.json   # empty
git -C lib\memory-mcp log --oneline -2   # the atomic-write commit on feat/encryption-at-rest
```

- [ ] **Step 6: Manual UI walk-through (after the build is deployed with `.\scripts\Self-Update.ps1 -Schedule -SkipPull`)**

1. Open **Tenants → default → Storage**.
   - The status table lists the covered stores (`plaintext`), the not-covered items with reasons, and the Bridge stores.
   - The recovery-kit card shows the Bridge key fingerprint.
2. Download a kit and open it in an editor.
   - The file holds `format`, `tenantId`, `keyFingerprint`, `kdf` and `cipher`.
   - No 44-character Base64 key appears in it.
3. Open **Turn on encryption…**.
   - The preview shows warnings, the free space and the will-encrypt list.
   - The button stays disabled until the snapshot box is ticked, the password is entered and `ENCRYPT` is typed.
   - **Cancel** — the owner procedure (`docs/encryption-at-rest.md`) does the real switch.

---

## Spec coverage

| Spec item | Task(s) |
|---|---|
| D1 one-way marker, effective mode = config ∨ marker ∨ detected, self-restart | 4, 11 |
| D2 Storage page: status, kit, preview modal, snapshot check, re-auth, `ENCRYPT` | 17, 18, 19 |
| D3 encrypted memory store (SQLite3MC `memory.db`, AES-GCM content, HKDF labels, fail closed) | 2, 3, 7, 8 |
| D3 atomic content writes in memory-mcp | 5 |
| D4 crash-safe content migration, plaintext overwritten, wrong key changes nothing | 6, 7 |
| D5 encrypted session snapshot, deferred restore, never plaintext | 9 |
| D6 coverage: eval copies erased + CLIs refuse; not-covered list; secure-delete guard | 10, 11, 12 |
| D7 status (hub, Bridge view, `/health`) | 10, 11, 17, 18 |
| D8 recovery kit export/restore with rules | 14, 15, 18 |
| D9 rollback guards: detection, expected fingerprint, push policy, downgrade docs | 2, 4, 16, 21 |
| D10 logs scrubbed and guarded | 13 |
| F2 sqlite-vec under SQLite3MC verified | 7 |
| F4 Bridge store facts, docs corrected | 17, 21 |
| §8 integrity gate incl. end-to-end | 6, 7, 8, 9, 12, 20, 22 |
| §9 owner procedure | 21, 22 |
