# Encrypted Evaluation and the Sanitised Evaluation Corpus — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the quality-gate evaluations (Gate H for history and memory, Gate R) work on an install with encryption at rest, without plaintext copies, and add the sanitised, versioned evaluation corpus that is the off-machine backup (D6): a deterministic pseudonymiser with an owner dictionary and a fail-closed leakage check, frozen versions with registration hashes, a passphrase-encrypted export package attached to the SafeExchange secret `cortex-recovery-kit`, D3 handling of purges, and the raw-vs-sanitised comparison report the master spec requires (§4.2).

**Architecture:**
- **Jobs in the agent process.** Only the agent process holds the tenant key. `history-eval`, `memory-eval` and the new `eval-corpus` run there as background jobs (`EvaluationJobService`), started by the Bridge over the hub after a password re-check (`/api/tenants/{t}/eval/jobs`, `scripts/Invoke-CortexEval.ps1`). The in-container CLIs keep working on unencrypted installs and refuse to copy encrypted stores.
- **Evaluation key ring.** `StorageKeyRing.Derive("eval")`: every copy (run directories, frozen corpora) is encrypted with keys derived from it, written by `VACUUM INTO` a keyed target. `EvalProtection` tells each component how to read its source and protect its copies; datasets, seeds and the dictionary are sealed (AES-256-GCM, file name as associated data) and edited through the Bridge (`scripts/Edit-CortexEvalFile.ps1`).
- **Sanitised frozen corpus.** `TextSanitiser` (shared identifier detectors + owner lexicon + URL/handle/number rules, HMAC pseudonyms per version) → `FrozenCorpusBuilder` (sanitised `Messages`, memories, datasets, plans, registration, manifest) → `LeakageScanner` (fail closed) → `FrozenCorpusPackage` (tar in a PBKDF2 + AES-256-GCM chunked stream) → Bridge download → SafeExchange file attachment.
- **D3.** The existing `smoke-artifacts` erasure step destroys overlapping versions; exported ones enter a ledger that the purge confirmation and the Recall page surface until the owner deletes the attachment and confirms.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, SignalR, Microsoft.Data.Sqlite + SQLite3 Multiple Ciphers (`VACUUM INTO 'file:…?key=…'`), memory-mcp (`SqliteVecMemoryStore`, `WordChunkingService`, `Preregistration`, `EvalStatistics`), System.Security.Cryptography (HKDF, HMACSHA256, AesGcm, PBKDF2), System.Formats.Tar, xUnit + NSubstitute, Alpine.js + Bootstrap (Bridge UI), PowerShell 7 scripts.

**Design:** [docs/superpowers/specs/2026-11-23-encrypted-eval-sanitised-corpus-design.md](../specs/2026-11-23-encrypted-eval-sanitised-corpus-design.md). Decisions are referred to as E1–E14; the master spec's as D3, D5, D6.

---

## Ground rules

- Branch `feat/encrypted-eval-corpus` is checked out (from `main` at `c49dbaf`). No submodule change: memory-mcp stays at its pinned commit.
- Commit after every task with the trailer `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`. **Never stage `version.json`**: every commit step lists explicit paths.
- Do not push.
- Public repository: no real names, places or host paths in code, tests or docs. Test data uses invented names (`Alvenia`, `Krøllvik`, `Dorvin`, `Morvenby`, `PersonA`, `TownB`), `example.*` / `.test` hosts and synthetic identifiers.
- SafeExchange: the only secret any procedure touches is `cortex-recovery-kit` (file attachments, `secret_get` metadata, `file_delete`, `audit_read`). Never create, list or read another secret.
- Every code task was replayed in a scratch worktree from `c49dbaf`, one commit per task: each commit builds the three test projects with `0 Warning(s) 0 Error(s)` and its tests pass with the counts given in the task. Modified files are shown as unified diffs against the state after the previous task (context lines unchanged); new files in full.

## Gotchas (read before Task 1)

1. **Warnings are errors** (`TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`). Analyzers that bit while replaying: **CA1711** (no type name ending in `Dictionary` — hence `SanitiserLexicon`), **CA1859** (private members return/hold concrete collections), **CA1861** (constant arrays as `static readonly` fields, also in tests), **CA1822** (static helpers), **CA1845** (span `Concat` instead of `Substring` in tests), **CA1031** (broad catches need the repo's pragma with a reason).
2. **SQLite3 Multiple Ciphers gives an unkeyed `VACUUM INTO`/`ATTACH` target the source's key.** A plaintext copy needs an explicit empty key (`file:…?key=`); `EncryptedSqlite.CopyDatabase` does that. `VACUUM INTO` works on a read-only connection and does not need the `vec0` module for `memory.db`.
3. **Namespaces:** inside `namespace Cortex.Contained.Agent.Host.History`, `Evaluation.X` resolves to `History.Evaluation`, not to the new `Cortex.Contained.Agent.Host.Evaluation` — use the fully qualified name there (`ErasureStepHandlers.cs`). In `Recall` and `Hubs`, `Evaluation.` resolves correctly.
4. **Pre-registration hashes the sealed bytes** (memory-mcp `Preregistration`, unchanged): re-sealing identical text changes the hash. `EvaluationFiles` therefore never rewrites identical content, and frozen and imported workspaces register again (their integrity anchor is the manifest's plaintext hashes).
5. **Windows file locks:** open SQLite files cannot be read with `File.ReadAllBytes`; byte scans of a live copy use `FileShare.ReadWrite | FileShare.Delete`. Call `SqliteConnection.ClearAllPools()` before deleting or moving directories with databases.
6. **`TarReader`:** an empty entry has `DataStream == null` (an empty `memories.jsonl` must still import).
7. **PBKDF2 at 600,000 iterations** costs ~0.3 s per derivation: cipher unit tests pass `PassphraseKdf.MinimumIterations`; export/import tests use the default once each.
8. **Job options:** jobs refuse `--dir`, `--source`, `--labels`, `--embedder`, `--reranker`; `HistoryEvalCli.RunInAgentAsync` appends the live `--source` itself after that check.
9. **`MemoryEvalWorkspace.Register` needs ≥ 250 valid queries**, so a frozen memory workspace is only produced from a registered raw one; the memory snapshot path is tested at the corpus level.
10. **Files are CRLF.** Use the editor's replace tools or apply the diffs; PowerShell here-strings with LF do not match. New files may be written with LF; Git normalises them.
11. **Raw string literals** cannot start with ```` ``` ```` on the opening line — the suggester test builds that LLM reply by concatenation.
12. **Pre-existing load flakes:** `SubAgentSetGoalToolTests` and `MaintenanceStoreTests.PurgeOldRuns_RemovesOldCompletedRuns` (timestamp granularity) can fail when the whole Agent.Host project runs; both pass alone. Rerun before investigating; this plan touches neither.

## File structure

| Path | New/Mod | Responsibility |
|---|---|---|
| `src/Cortex.Contained.Agent.Host/Storage/StorageKeyRing.cs` | mod | `Derive(purpose)`: sub-ring for evaluation copies |
| `src/Cortex.Contained.Agent.Host/Storage/EncryptedSqlite.cs` | mod | `CopyDatabase`: `VACUUM INTO` a keyed (or explicitly plaintext) target |
| `src/Cortex.Contained.Agent.Host/Evaluation/EvalProtection.cs` | new | source/copy keys, sealed text files |
| `src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs`, `EvalWorkspace.cs`, `EvalDataset.cs`, `HistoryEvalCli.cs`, `HistoryRerankEval.cs` | mod | encrypted copies, sealed dataset, `RunInAgentAsync`, `--corpus frozen:vN` |
| `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs`, `MemoryEvalWorkspace.cs`, `MemoryEvalDataset.cs`, `MemoryEvalCli.cs`, `MemoryRerankEval.cs` | mod | encrypted copies, sealed dataset and seeds, snapshot corpus, `RunInAgentAsync`, `--corpus` |
| `src/Cortex.Contained.Agent.Host/Evaluation/EvaluationJobService.cs` | new | in-process jobs |
| `src/Cortex.Contained.Agent.Host/Evaluation/EvaluationFiles.cs` | new | allow-listed workspace files |
| `src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs` | new | DI of jobs, files, frozen store |
| `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Evaluation.cs`, `AgentHub.cs` | new/mod | `IEvaluationHub` |
| `src/Cortex.Contained.Agent.Host/Reranking/Evaluation/RerankEvaluation.cs` | mod | reranker with the Bridge-granted key |
| `src/Cortex.Contained.Agent.Host/Storage/EvaluationGuard.cs` | mod | in-container refusal only for copying commands |
| `src/Cortex.Contained.Agent.Host/Program.cs` | mod | guard call, `AddEvaluation`, frozen store for Recall |
| `src/Cortex.Contained.Agent.Host/Evaluation/Sanitising/*` | new | `PseudonymMap`, `SanitiserLexicon`, `TextSanitiser`, `LeakageScanner`, `LexiconSuggester` |
| `src/Cortex.Contained.Agent.Host/Evaluation/Frozen/*` | new | `FrozenCorpusManifest`, `FrozenCorpusStore`, `FrozenCorpusBuilder`, `PackageCipher`, `FrozenCorpusPackage`, `SanitisedComparison`, `EvalCorpusCli` |
| `src/Cortex.Contained.Agent.Host/History/ErasureStepHandlers.cs` | mod | D3 for frozen versions |
| `src/Cortex.Contained.Agent.Host/Recall/RecallControlService.cs` | mod | purge preview and status carry frozen data |
| `src/Cortex.Contained.Agent.Host/Storage/StorageInventory.cs`, `StorageEncryptionService.cs` | mod | evaluation directory reported encrypted |
| `src/Cortex.Contained.Contracts/Hub/IEvaluationHub.cs`, `FrozenCorpusTypes.cs` | new | hub surface + DTOs |
| `src/Cortex.Contained.Contracts/Hub/IAgentHub.cs`, `RecallTypes.cs` | mod | composition; preview/status fields |
| `src/Cortex.Contained.Contracts/Security/SensitiveDataRedactor.Persistence.cs` | mod | `FindPersonalIdentifiers` |
| `src/Cortex.Contained.Contracts/Security/PassphraseKdf.cs` | new | shared passphrase KDF |
| `src/Cortex.Contained.Bridge/Security/StorageRecoveryKit.cs` | mod | uses `PassphraseKdf` |
| `src/Cortex.Contained.Bridge/Hub/HubClient.Evaluation.cs`, `Evaluation/IEvaluationAgentClient.cs`, `Evaluation/EvaluationEndpoints.cs` | new | Bridge relay |
| `src/Cortex.Contained.Bridge/Program.cs`, `Endpoints/RecallEndpoints.cs`, `Recall/HistoryPurgeConfirmation.cs` | mod | mapping, preview projection, confirmation text |
| `src/Cortex.Contained.Bridge/wwwroot/app.html`, `js/pages/tenant-recall.js`, `js/pages/tenant-storage.js` | mod | Recall banner, Storage card |
| `scripts/Invoke-CortexEval.ps1`, `Edit-CortexEvalFile.ps1`, `Export-CortexEvalCorpus.ps1` | new | owner entry points |
| `docs/evaluation-runbook.md` | new | owner runbook |
| `docs/encryption-at-rest.md`, `security.md`, `api-reference.md`, `architecture.md`, master spec | mod | docs |
| tests (listed per task) | new/mod | — |

## Task overview

| # | Task | Project(s) |
|---|---|---|
| 1 | Evaluation key ring and encrypted database copies | Agent |
| 2 | `EvalProtection` | Agent |
| 3 | `EvalCorpus` encrypted copies | Agent |
| 4 | `MemoryEvalCorpus` encrypted copies | Agent |
| 5 | history-eval through `EvalProtection` | Agent |
| 6 | memory-eval through `EvalProtection` | Agent |
| 7 | Evaluation jobs and workspace files (agent services, `IEvaluationHub`) | Agent, Contracts |
| 8 | Hub, DI, in-agent entry points, in-container guard | Agent, Contracts |
| 9 | Bridge evaluation endpoints and owner scripts | Bridge, scripts |
| 10 | `FindPersonalIdentifiers` | Contracts |
| 11 | Sanitiser core | Agent |
| 12 | Leakage check | Agent |
| 13 | Frozen corpus versions (freeze) | Agent, Contracts |
| 14 | Dictionary suggestions | Agent |
| 15 | Passphrase KDF and package cipher | Contracts, Agent, Bridge |
| 16 | Package export and import | Agent |
| 17 | Evaluating a frozen corpus (`--corpus frozen:vN`) | Agent |
| 18 | `eval-corpus` command and comparison report | Agent |
| 19 | D3 for frozen versions | Agent |
| 20 | Recall and Storage surfacing, package download | Agent, Contracts, Bridge, UI, scripts |
| 21 | Documentation and runbook | docs |
| 22 | Final verification | all |

---

### Task 1: Evaluation key ring and encrypted database copies

`StorageKeyRing.Derive(purpose)` returns a ring whose master key is the tenant key's HKDF key for that purpose, so every passphrase an evaluation copy uses differs from the live stores' (E2). `EncryptedSqlite.CopyDatabase` writes a consistent copy with `VACUUM main INTO` a keyed target, so no plaintext form ever reaches the disk (E3). An unkeyed target would inherit the source key under SQLite3 Multiple Ciphers, so a plaintext copy is requested with an empty key.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Storage/EncryptedSqlite.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Storage/StorageKeyRing.cs`
- Test (modify): `tests/Cortex.Contained.Agent.Host.Tests/Events/EncryptedSqliteTests.cs`
- Test (modify): `tests/Cortex.Contained.Agent.Host.Tests/Events/StorageKeyRingTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Events/EncryptedSqliteTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Agent.Host.Tests/Events/EncryptedSqliteTests.cs
+++ b/tests/Cortex.Contained.Agent.Host.Tests/Events/EncryptedSqliteTests.cs
@@ -1,5 +1,6 @@
 using Cortex.Contained.Agent.Host.Storage;
+using Cortex.Contained.Contracts.Security;
 using Microsoft.Data.Sqlite;
 
 namespace Cortex.Contained.Agent.Host.Tests.Events;
 
@@ -401,8 +402,51 @@ public sealed class EncryptedSqliteTests : IDisposable
 
         Assert.Equal(EncryptedOpenOutcome.Migrated, EncryptedSqlite.Prepare(this.DbPath, KeyA));
     }
 
+    [Fact]
+    public void CopyDatabase_EncryptedSourceToAnotherKey_CopyOpensOnlyWithTheTargetKey()
+    {
+        this.Write(KeyA, "zqcopied");
+        var copy = Path.Combine(this.dir, "copy.db");
+
+        EncryptedSqlite.CopyDatabase(this.DbPath, KeyA, copy, KeyB);
+
+        Assert.Equal(StoredFileState.Encrypted, SqliteFileHeader.Classify(copy));
+        Assert.DoesNotContain("zqcopied", File.ReadAllText(copy), StringComparison.Ordinal);
+        Assert.Equal(EncryptedOpenOutcome.Opened, EncryptedSqlite.Prepare(copy, KeyB));
+        Assert.Throws<StorageKeyMismatchException>(() => EncryptedSqlite.Prepare(copy, KeyA));
+        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(copy, KeyB, SqliteOpenMode.ReadOnly, pooling: false));
+        connection.Open();
+        using var cmd = connection.CreateCommand();
+        cmd.CommandText = "SELECT x FROM t;";
+        Assert.Equal("zqcopied", cmd.ExecuteScalar());
+    }
+
+    [Fact]
+    public void CopyDatabase_PlaintextToPlaintext_KeepsRowsAndNeverOverwrites()
+    {
+        this.Write(null, "zqplain");
+        var copy = Path.Combine(this.dir, "copy.db");
+
+        EncryptedSqlite.CopyDatabase(this.DbPath, null, copy, null);
+
+        Assert.Contains("zqplain", File.ReadAllText(copy), StringComparison.Ordinal);
+        Assert.Throws<IOException>(() => EncryptedSqlite.CopyDatabase(this.DbPath, null, copy, null));
+    }
+
+    [Fact]
+    public void CopyDatabase_EncryptedToPlaintext_CopyIsPlaintext()
+    {
+        this.Write(KeyA, "zqdecrypted");
+        var copy = Path.Combine(this.dir, "copy.db");
+
+        EncryptedSqlite.CopyDatabase(this.DbPath, KeyA, copy, null);
+
+        Assert.Equal(StoredFileState.Plaintext, SqliteFileHeader.Classify(copy));
+        Assert.Contains("zqdecrypted", File.ReadAllText(copy), StringComparison.Ordinal);
+    }
+
     [Fact]
     public void DirectorySync_NeverThrows_AndFlushesOnUnix()
     {
         Assert.Equal(!OperatingSystem.IsWindows(), DirectorySync.TryFlush(this.dir));
```

`tests/Cortex.Contained.Agent.Host.Tests/Events/StorageKeyRingTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Agent.Host.Tests/Events/StorageKeyRingTests.cs
+++ b/tests/Cortex.Contained.Agent.Host.Tests/Events/StorageKeyRingTests.cs
@@ -75,5 +75,20 @@ public sealed class StorageKeyRingTests
         Assert.Equal(32, content.Length);
         Assert.NotEqual(content, ring.KeyFor("sessions-snapshot"));
         Assert.Equal(ring.PasswordFor("memory-content"), Convert.ToHexStringLower(content));
     }
+
+    [Fact]
+    public void Derive_RingOfAPurpose_HasItsOwnPasswordsAndRequiresTheKey()
+    {
+        var ring = new StorageKeyRing();
+        Assert.Throws<StorageLockedException>(() => ring.Derive("eval"));
+        ring.Provide(KeyA);
+
+        var eval = ring.Derive("eval");
+
+        Assert.True(eval.IsAvailable);
+        Assert.NotEqual(ring.PasswordFor("messages.db"), eval.PasswordFor("messages.db"));
+        Assert.Equal(StorageKeyRing.DerivePassword(ring.KeyFor("eval"), "messages.db"), eval.PasswordFor("messages.db"));
+        Assert.Equal(eval.PasswordFor("messages.db"), ring.Derive("eval").PasswordFor("messages.db"));
+    }
 }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~StorageKeyRingTests|FullyQualifiedName~EncryptedSqliteTests" -v q`
Expected: build FAILS — `'StorageKeyRing' does not contain a definition for 'Derive'` and `'EncryptedSqlite' does not contain a definition for 'CopyDatabase'`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Storage/EncryptedSqlite.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Storage/EncryptedSqlite.cs
+++ b/src/Cortex.Contained.Agent.Host/Storage/EncryptedSqlite.cs
@@ -112,8 +112,41 @@ public static partial class EncryptedSqlite
         var match = VersionPattern().Match(text);
         return match.Success ? Version.Parse(match.Value) : new Version(0, 0, 0);
     }
 
+    /// <summary>
+    /// Writes a consistent copy of <paramref name="source"/> (read with <paramref name="sourcePassword"/>, null for
+    /// plaintext; the database may be in use) to <paramref name="target"/>, encrypted with <paramref name="targetPassword"/>
+    /// (null: plaintext). <c>VACUUM INTO</c> writes the target pages already encrypted, so no other form of the copy
+    /// ever reaches the disk. Never overwrites: throws <see cref="IOException"/> when <paramref name="target"/> exists.
+    /// </summary>
+    public static void CopyDatabase(string source, string? sourcePassword, string target, string? targetPassword)
+    {
+        ArgumentException.ThrowIfNullOrWhiteSpace(source);
+        ArgumentException.ThrowIfNullOrWhiteSpace(target);
+        if (sourcePassword is not null)
+        {
+            EnsureHex(sourcePassword);
+        }
+
+        if (targetPassword is not null)
+        {
+            EnsureHex(targetPassword);
+        }
+
+        if (File.Exists(target))
+        {
+            throw new IOException($"{Path.GetFileName(target)} already exists.");
+        }
+
+        using var connection = new SqliteConnection(ConnectionString(source, sourcePassword, SqliteOpenMode.ReadOnly, pooling: false));
+        connection.Open();
+
+        // SQLite3 Multiple Ciphers gives a target without a key the source's key; an empty key makes it plaintext.
+        var into = ExportTarget(target, targetPassword ?? string.Empty);
+        Execute(connection, $"VACUUM main INTO '{into}';");
+    }
+
     /// <summary>A connection string for <paramref name="path"/>, encrypted when <paramref name="password"/> is set.</summary>
     public static string ConnectionString(string path, string? password, SqliteOpenMode mode, bool pooling) =>
         new SqliteConnectionStringBuilder
         {
```

`src/Cortex.Contained.Agent.Host/Storage/StorageKeyRing.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Storage/StorageKeyRing.cs
+++ b/src/Cortex.Contained.Agent.Host/Storage/StorageKeyRing.cs
@@ -116,8 +116,28 @@ public sealed class StorageKeyRing
         var key = await this.master.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
         return DeriveKey(key, purpose);
     }
 
+    /// <summary>
+    /// A key ring whose master key is this ring's key for <paramref name="purpose"/> (HKDF-SHA256), so every
+    /// passphrase and key it derives differs from the ones of the live stores. Evaluation copies use the ring
+    /// derived for <c>eval</c>. Throws <see cref="StorageLockedException"/> before the key arrived.
+    /// </summary>
+    public StorageKeyRing Derive(string purpose)
+    {
+        var key = this.KeyFor(purpose);
+        try
+        {
+            var ring = new StorageKeyRing();
+            ring.Provide(Convert.ToBase64String(key));
+            return ring;
+        }
+        finally
+        {
+            CryptographicOperations.ZeroMemory(key);
+        }
+    }
+
     /// <summary>Deterministic, purpose-bound passphrase (64 hex characters).</summary>
     public static string DerivePassword(byte[] masterKey, string purpose)
     {
         var derived = DeriveKey(masterKey, purpose);
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~StorageKeyRingTests|FullyQualifiedName~EncryptedSqliteTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 40 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Storage\EncryptedSqlite.cs src\Cortex.Contained.Agent.Host\Storage\StorageKeyRing.cs tests\Cortex.Contained.Agent.Host.Tests\Events\EncryptedSqliteTests.cs tests\Cortex.Contained.Agent.Host.Tests\Events\StorageKeyRingTests.cs
git commit -m "feat(storage): evaluation key ring and encrypted database copies" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: `EvalProtection`: source and copy keys, sealed workspace files

One object tells every evaluation component how to read its source and protect its copies (E2, E4): plaintext on an unencrypted install; on an encrypted one the live ring for the source, the `eval` ring for copies, and AES-256-GCM sealing (`eval-files` key, file name as associated data) for files that carry personal text. Encrypted mode refuses plaintext copies of such files; plaintext mode refuses sealed ones.

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/EvalProtection.cs`
- Test (new): `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/EvalProtectionTests.cs`
- Test (new): `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/EvalTestKeys.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/EvalProtectionTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation;

public sealed class EvalProtectionTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"eval-protection-{Guid.NewGuid():N}");

    public EvalProtectionTests() => Directory.CreateDirectory(this.dir);

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void WriteText_Encrypted_SealsTheFileAndReadsItBack()
    {
        var protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
        var path = Path.Combine(this.dir, "dataset.jsonl");

        protection.WriteLines(path, ["{\"id\":\"e1\",\"text\":\"zqsecret\"}", string.Empty]);

        Assert.True(EvalProtection.IsSealed(path));
        Assert.DoesNotContain("zqsecret", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(["{\"id\":\"e1\",\"text\":\"zqsecret\"}"], protection.ReadLines(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ReadText_EncryptedModeAndPlaintextFile_RefusedFailClosed()
    {
        var path = Path.Combine(this.dir, "dataset.jsonl");
        File.WriteAllText(path, "{\"id\":\"e1\"}");

        var error = Assert.Throws<InvalidOperationException>(() => EvalProtection.Encrypted(EvalTestKeys.Ring()).ReadText(path));

        Assert.Contains("plaintext on an encrypted install", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadText_PlaintextModeAndSealedFile_Refused()
    {
        var path = Path.Combine(this.dir, "dataset.jsonl");
        EvalProtection.Encrypted(EvalTestKeys.Ring()).WriteText(path, "x");

        Assert.Throws<InvalidOperationException>(() => EvalProtection.Plaintext.ReadText(path));
    }

    [Fact]
    public void ReadText_SealedFileRenamed_FailsAuthentication()
    {
        var protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
        var seeds = Path.Combine(this.dir, "seeds.jsonl");
        protection.WriteText(seeds, "zqsecret");
        var dataset = Path.Combine(this.dir, "dataset.jsonl");
        File.Move(seeds, dataset);

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protection.ReadText(dataset));
    }

    [Fact]
    public void Passwords_CopiesUseTheEvaluationRing_FrozenSourceReadsWithIt()
    {
        var live = EvalTestKeys.Ring();
        var protection = EvalProtection.Encrypted(live);

        Assert.Equal(live.PasswordFor(MessageStore.PasswordPurpose), protection.SourcePassword(MessageStore.PasswordPurpose));
        Assert.Equal(live.Derive(EvalProtection.Purpose).PasswordFor(MessageStore.PasswordPurpose), protection.CopyPassword(MessageStore.PasswordPurpose));
        Assert.Equal(protection.CopyPassword(MessageStore.PasswordPurpose), protection.ForFrozenSource().SourcePassword(MessageStore.PasswordPurpose));
        Assert.Null(EvalProtection.Plaintext.SourcePassword(MessageStore.PasswordPurpose));
        Assert.Null(EvalProtection.Plaintext.CopyMessageEncryption);
    }
}
```

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/EvalTestKeys.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation;

/// <summary>A tenant key ring with a fixed test key (never a real key).</summary>
internal static class EvalTestKeys
{
    public static readonly string Key = Convert.ToBase64String(Enumerable.Range(7, 32).Select(i => (byte)i).ToArray());

    public static StorageKeyRing Ring()
    {
        var ring = new StorageKeyRing();
        ring.Provide(Key);
        return ring;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~EvalProtectionTests" -v q`
Expected: build FAILS — `The type or namespace name 'EvalProtection' could not be found`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/EvalProtection.cs` (new):

```csharp
using System.Text;
using Cortex.Contained.Agent.Host.Storage;
using MemoryMcp.Core.Security;

namespace Cortex.Contained.Agent.Host.Evaluation;

/// <summary>
/// How an evaluation command protects what it reads and copies (D4, D6). <see cref="Plaintext"/> on an
/// unencrypted install. <see cref="Encrypted"/> on an encrypted install, inside the agent process only (the
/// storage key never leaves it): the live stores are read with their own passphrases, every copy (run
/// directories, frozen corpora) is encrypted with keys of the ring derived for <see cref="Purpose"/>, and the
/// owner-authored files that carry text (datasets, query seeds, the sanitiser dictionary) are sealed with
/// AES-256-GCM. Reports, verdicts and manifests hold ids and numbers only and stay plaintext.
/// </summary>
public sealed class EvalProtection
{
    /// <summary>Purpose of the evaluation key ring (HKDF from the tenant key).</summary>
    public const string Purpose = "eval";

    /// <summary>Purpose of the key that seals workspace text files (derived from the evaluation ring).</summary>
    public const string SealedFilesPurpose = "eval-files";

    // The copy ring already holds its key, so this wait never elapses.
    private static readonly TimeSpan CopyKeyWait = TimeSpan.FromSeconds(5);

    private readonly StorageKeyRing? source;

    private EvalProtection(StorageKeyRing? source, StorageKeyRing? ring)
    {
        this.source = source;
        this.Ring = ring;
    }

    /// <summary>Nothing is encrypted (an install without encryption at rest).</summary>
    public static EvalProtection Plaintext { get; } = new(null, null);

    /// <summary>The ring the copies are encrypted with, or null in plaintext mode.</summary>
    public StorageKeyRing? Ring { get; }

    /// <summary>True when copies and sealed files are encrypted.</summary>
    public bool IsEncrypted => this.Ring is not null;

    /// <summary>Encryption of a <see cref="MessageStore"/> opened on a copy, or null when plaintext.</summary>
    public MessageStoreEncryption? CopyMessageEncryption => this.Ring is null ? null : new MessageStoreEncryption(this.Ring, CopyKeyWait);

    /// <summary>Protection on an encrypted install: <paramref name="live"/> must hold the tenant key.</summary>
    public static EvalProtection Encrypted(StorageKeyRing live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return new EvalProtection(live, live.Derive(Purpose));
    }

    /// <summary>True when <paramref name="path"/> exists and is sealed.</summary>
    public static bool IsSealed(string path) => File.Exists(path) && StorageFileCipher.IsEncryptedFile(path);

    /// <summary>
    /// The same protection reading a frozen corpus instead of the live stores: the source is itself an
    /// evaluation copy, encrypted with the evaluation ring.
    /// </summary>
    public EvalProtection ForFrozenSource() => this.Ring is null ? this : new EvalProtection(this.Ring, this.Ring);

    /// <summary>Passphrase of the source database for <paramref name="purpose"/> (e.g. <c>messages.db</c>), or null when plaintext.</summary>
    public string? SourcePassword(string purpose) => this.source?.PasswordFor(purpose);

    /// <summary>Passphrase of a copy for <paramref name="purpose"/>, or null when plaintext.</summary>
    public string? CopyPassword(string purpose) => this.Ring?.PasswordFor(purpose);

    /// <summary>Cipher of the source's memory content files.</summary>
    public IContentEncryptor SourceContent() =>
        this.source is null ? new NullContentEncryptor() : new StorageFileCipher(this.source.KeyFor(EncryptedMemoryStore.ContentPurpose), EncryptedMemoryStore.ContentPurpose);

    /// <summary>Cipher of a copy's memory content files.</summary>
    public IContentEncryptor CopyContent() =>
        this.Ring is null ? new NullContentEncryptor() : new StorageFileCipher(this.Ring.KeyFor(EncryptedMemoryStore.ContentPurpose), EncryptedMemoryStore.ContentPurpose);

    /// <summary>
    /// Reads a text file that may carry personal text. Encrypted mode accepts only sealed files (fail closed: a
    /// plaintext dataset on an encrypted install is refused; upload it through the Bridge instead). Plaintext mode
    /// refuses sealed files (they belong to an encrypted install).
    /// </summary>
    public string ReadText(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        var sealedFile = StorageFileCipher.IsEncrypted(bytes);
        if (this.Ring is null)
        {
            return sealedFile
                ? throw new InvalidOperationException($"{Path.GetFileName(path)} is sealed with the storage key: run the command through the Bridge (scripts/Invoke-CortexEval.ps1).")
                : Encoding.UTF8.GetString(bytes);
        }

        if (!sealedFile)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} is plaintext on an encrypted install: upload it through the Bridge (scripts/Edit-CortexEvalFile.ps1) so it is sealed, then delete the plaintext copy.");
        }

        return Encoding.UTF8.GetString(this.FileCipher(path).Decrypt(bytes));
    }

    /// <summary>Non-blank lines of <see cref="ReadText"/>; an absent file has none.</summary>
    public IReadOnlyList<string> ReadLines(string path) =>
        File.Exists(path)
            ? this.ReadText(path).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
            : [];

    /// <summary>Writes <paramref name="text"/> atomically, sealed in encrypted mode.</summary>
    public void WriteText(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (this.Ring is not null)
        {
            bytes = this.FileCipher(path).Encrypt(bytes);
        }

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Writes one line per element (see <see cref="WriteText"/>).</summary>
    public void WriteLines(string path, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        this.WriteText(path, string.Concat(lines.Select(l => l + "\n")));
    }

    // The file name is bound as associated data: a sealed dataset cannot be swapped for sealed seeds.
    private StorageFileCipher FileCipher(string path) =>
        new(this.Ring!.KeyFor(SealedFilesPurpose), SealedFilesPurpose + "/" + Path.GetFileName(path));
}
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~EvalProtectionTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 5 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Evaluation\EvalProtection.cs tests\Cortex.Contained.Agent.Host.Tests\Evaluation\EvalProtectionTests.cs tests\Cortex.Contained.Agent.Host.Tests\Evaluation\EvalTestKeys.cs
git commit -m "feat(eval): EvalProtection for encrypted copies and sealed workspace files" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: `EvalCorpus` copies encrypted with the evaluation ring

The history-eval corpus copy and its index generations use `EvalProtection` (default plaintext, so existing callers are unchanged). The copy is made with `CopyDatabase`; the manifest (corpus range for D3) is still written before the index build.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs`
- Test (modify): `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryEvalCorpusTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/History/HistoryEvalCorpusTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Agent.Host.Tests/History/HistoryEvalCorpusTests.cs
+++ b/tests/Cortex.Contained.Agent.Host.Tests/History/HistoryEvalCorpusTests.cs
@@ -71,8 +71,37 @@ public sealed class HistoryEvalCorpusTests : IAsyncDisposable
 
         Assert.NotEmpty(run.Hits);
     }
 
+    [Fact]
+    public async Task CreateAsync_EncryptedSource_CopyAndIndexesEncryptedWithTheEvaluationRing()
+    {
+        var live = Cortex.Contained.Agent.Host.Tests.Evaluation.EvalTestKeys.Ring();
+        Directory.CreateDirectory(this.root);
+        var source = Path.Combine(this.root, "messages.db");
+        await using (var store = new MessageStore(source, NullLogger<MessageStore>.Instance, new MessageStoreEncryption(live, TimeSpan.FromSeconds(5))))
+        {
+            await store.SaveMessageAsync("u", "ch", "user", "we booked the railway journey to TownB zqrawtext", T0);
+        }
+
+        var run = Path.Combine(this.root, "run");
+        var hybrid = new AblationConfig { Name = "hybrid-rrf60" };
+        await using (var corpus = await EvalCorpus.CreateAsync(
+            source, run, new HistoryOptions { Enabled = true, TimeZone = "UTC" }, [hybrid], _ => new FakeHistoryEmbedder(), CancellationToken.None,
+            protection: Cortex.Contained.Agent.Host.Evaluation.EvalProtection.Encrypted(live)))
+        {
+            Assert.NotEmpty((await corpus.SearchAsync(hybrid, Query with { Text = "railway" }, CancellationToken.None)).Hits);
+        }
+
+        SqliteConnection.ClearAllPools();
+        var copy = Path.Combine(run, "messages.db");
+        Assert.Equal(Cortex.Contained.Contracts.Security.StoredFileState.Encrypted, Cortex.Contained.Contracts.Security.SqliteFileHeader.Classify(copy));
+        Assert.Throws<StorageKeyMismatchException>(() => EncryptedSqlite.Prepare(copy, live.PasswordFor(MessageStore.PasswordPurpose)));
+        Assert.All(
+            Directory.EnumerateFiles(run, "*", SearchOption.AllDirectories),
+            f => Assert.DoesNotContain("zqrawtext", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(f)), StringComparison.Ordinal));
+    }
+
     private sealed class SlowQueryEmbedder(FakeHistoryEmbedder inner) : IHistoryEmbedder
     {
         public TimeSpan QueryDelay { get; set; }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~HistoryEvalCorpusTests" -v q`
Expected: build FAILS — `The best overload for 'CreateAsync' does not have a parameter named 'protection'`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs
+++ b/src/Cortex.Contained.Agent.Host/History/Evaluation/EvalCorpus.cs
@@ -1,5 +1,6 @@
 using System.Diagnostics;
+using Cortex.Contained.Agent.Host.Evaluation;
 using Cortex.Contained.Agent.Host.Reranking;
 using Cortex.Contained.Agent.Host.Storage;
 using Microsoft.Data.Sqlite;
 using Microsoft.Extensions.Logging.Abstractions;
@@ -9,9 +10,10 @@ namespace Cortex.Contained.Agent.Host.History.Evaluation;
 
 /// <summary>
 /// A private copy of <c>messages.db</c> with one fully embedded index generation per keyword variant
 /// (§4.2 ablations). The live database and index are never touched; everything stays in the run
-/// directory, which holds raw message text (D6): outputs beside it are numbers only. A
+/// directory, which holds raw message text (D6): outputs beside it are numbers only. On an encrypted
+/// install the copy and its index generations are encrypted with the evaluation ring (<see cref="EvalProtection"/>). A
 /// <c>manifest.json</c> with the copied corpus's time range is written first, so a purge overlapping
 /// that range destroys the run directory (purge erasure covers the <c>runs</c> of the configured
 /// <c>History:EvalArtifactsDirectory</c>); the owner otherwise deletes the run directory after use.
 /// </summary>
@@ -35,46 +37,44 @@ public sealed class EvalCorpus : IAsyncDisposable
     /// <summary>
     /// Copies <paramref name="sourceDb"/> and builds an embedded generation for every keyword variant in
     /// <paramref name="configs"/>. <paramref name="embedderFactory"/> receives the options monitor the
     /// corpus switches per configuration (so the query-instruction ablation reaches the embedder).
-    /// <paramref name="reranker"/> serves configurations with a rerank budget (§4.3).
+    /// <paramref name="reranker"/> serves configurations with a rerank budget (§4.3). <paramref name="protection"/>
+    /// (default plaintext) decides how the source is read and how the copy is encrypted.
     /// </summary>
     public static async Task<EvalCorpus> CreateAsync(
         string sourceDb,
         string runDirectory,
         HistoryOptions baseline,
         IReadOnlyList<AblationConfig> configs,
         Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory,
         CancellationToken cancellationToken,
-        IRerankStage? reranker = null)
+        IRerankStage? reranker = null,
+        EvalProtection? protection = null)
     {
         ArgumentNullException.ThrowIfNull(baseline);
         ArgumentNullException.ThrowIfNull(configs);
         ArgumentNullException.ThrowIfNull(embedderFactory);
+        protection ??= EvalProtection.Plaintext;
         Directory.CreateDirectory(runDirectory);
         var copyPath = Path.Combine(runDirectory, "messages.db");
-        using (var source = new SqliteConnection($"Data Source={sourceDb};Mode=ReadOnly;Pooling=False"))
-        using (var target = new SqliteConnection($"Data Source={copyPath};Pooling=False"))
-        {
-            source.Open();
-            target.Open();
-            source.BackupDatabase(target);
-        }
+        var copyPassword = protection.CopyPassword(MessageStore.PasswordPurpose);
+        EncryptedSqlite.CopyDatabase(sourceDb, protection.SourcePassword(MessageStore.PasswordPurpose), copyPath, copyPassword);
 
-        var manifest = ReadCorpusRange(copyPath);
+        var manifest = ReadCorpusRange(copyPath, copyPassword);
         EvalWorkspace.WriteJson(Path.Combine(runDirectory, "manifest.json"), manifest);
 
         var monitor = new MutableOptionsMonitor(baseline);
         var embedder = embedderFactory(monitor);
-        var corpus = new EvalCorpus(new MessageStore(copyPath, NullLogger<MessageStore>.Instance), monitor, reranker);
+        var corpus = new EvalCorpus(new MessageStore(copyPath, NullLogger<MessageStore>.Instance, protection.CopyMessageEncryption), monitor, reranker);
         foreach (var variant in configs.Select(c => c.KeywordVariant).Distinct())
         {
             monitor.CurrentValue = new AblationConfig { Name = "build", KeywordVariant = variant }.Apply(baseline);
             var clock = new EvalClock();
             var gate = new HistoryAccessGate();
             var status = new HistoryStatus();
             var projector = new HistoryProjector(
-                corpus.store, corpus.store, new HistoryIndexGenerations(Path.Combine(runDirectory, "index-" + variant.ToString().ToLowerInvariant())),
+                corpus.store, corpus.store, new HistoryIndexGenerations(Path.Combine(runDirectory, "index-" + variant.ToString().ToLowerInvariant()), protection.Ring),
                 gate, monitor, status, NullLogger<HistoryProjector>.Instance, embedder, clock);
             await projector.InitializeAsync(cancellationToken).ConfigureAwait(false);
             await projector.RebuildAsync(cancellationToken).ConfigureAwait(false);
             clock.Now += TimeSpan.FromDays(1); // every open segment is past its debounce
@@ -188,11 +188,11 @@ public sealed class EvalCorpus : IAsyncDisposable
 
         await this.store.DisposeAsync().ConfigureAwait(false);
     }
 
-    private static SmokeRunManifest ReadCorpusRange(string copyPath)
+    private static SmokeRunManifest ReadCorpusRange(string copyPath, string? password)
     {
-        using var connection = new SqliteConnection($"Data Source={copyPath};Mode=ReadOnly;Pooling=False");
+        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(copyPath, password, SqliteOpenMode.ReadOnly, pooling: false));
         connection.Open();
         using var cmd = connection.CreateCommand();
         cmd.CommandText = "SELECT MIN(Timestamp), MAX(Timestamp) FROM Messages;";
         using var reader = cmd.ExecuteReader();
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~HistoryEvalCorpusTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 3 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\History\Evaluation\EvalCorpus.cs tests\Cortex.Contained.Agent.Host.Tests\History\HistoryEvalCorpusTests.cs
git commit -m "feat(eval): encrypted history-eval corpus copies" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: `MemoryEvalCorpus` copies encrypted with the evaluation ring

The memory store copy (database and content files) is encrypted with the evaluation ring for its short life: content files are decrypted with the live content key and re-encrypted with the copy key in memory. The copy is still deleted when the command ends.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs`
- Test (modify): `tests/Cortex.Contained.Agent.Host.Tests/Memory/Evaluation/MemoryEvalCorpusTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Memory/Evaluation/MemoryEvalCorpusTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Agent.Host.Tests/Memory/Evaluation/MemoryEvalCorpusTests.cs
+++ b/tests/Cortex.Contained.Agent.Host.Tests/Memory/Evaluation/MemoryEvalCorpusTests.cs
@@ -184,5 +184,50 @@ public sealed class MemoryEvalCorpusTests : IDisposable
     {
         await Assert.ThrowsAsync<InvalidOperationException>(() =>
             MemoryEvalCorpus.CreateAsync(Path.Combine(this.root, "missing"), Path.Combine(this.root, "run"), this.live, new FakeMemoryEmbedder(), CancellationToken.None));
     }
+
+    /// <summary>Seeds an encrypted "live" store the way <c>EncryptedMemoryStore</c> keeps it.</summary>
+    internal static async Task SeedEncryptedAsync(MemoryMcpOptions options, Cortex.Contained.Agent.Host.Storage.StorageKeyRing keys, params (string Id, string? Title, string Content, string[] Tags)[] memories)
+    {
+        var cipher = new Cortex.Contained.Agent.Host.Storage.StorageFileCipher(
+            keys.KeyFor(Cortex.Contained.Agent.Host.Storage.EncryptedMemoryStore.ContentPurpose), Cortex.Contained.Agent.Host.Storage.EncryptedMemoryStore.ContentPurpose);
+        using var store = new SqliteVecMemoryStore(
+            Options.Create(options), cipher, NullLogger<SqliteVecMemoryStore>.Instance, keys.PasswordFor(Cortex.Contained.Agent.Host.Storage.EncryptedMemoryStore.DatabasePurpose));
+        await store.InitializeAsync(CancellationToken.None);
+        var now = DateTimeOffset.UtcNow;
+        foreach (var (id, title, content, tags) in memories)
+        {
+            var chunk = new ChunkRecord { MemoryId = id, ChunkIndex = 0, Length = content.Length, Title = title, Tags = [.. tags], CreatedAt = now, UpdatedAt = now };
+            await store.StoreMemoryAsync(id, content, [chunk], [FakeMemoryEmbedder.Embed(content)], CancellationToken.None);
+        }
+    }
+
+    [Fact]
+    public async Task CreateAsync_EncryptedStore_CopyEncryptedWithTheEvaluationRingAndSearchable()
+    {
+        var keys = Cortex.Contained.Agent.Host.Tests.Evaluation.EvalTestKeys.Ring();
+        await SeedEncryptedAsync(this.live, keys, ("mem-invoice", null, "Invoice INV-2041 zqmemtext was paid in March", ["finance"]));
+        var runDirectory = Path.Combine(this.root, "run");
+        var protection = Cortex.Contained.Agent.Host.Evaluation.EvalProtection.Encrypted(keys);
+
+        await using (var corpus = await MemoryEvalCorpus.CreateAsync(this.live.DataDirectory, runDirectory, this.live, new FakeMemoryEmbedder(), CancellationToken.None, protection: protection))
+        {
+            var corpusFiles = Directory.EnumerateFiles(Path.Combine(runDirectory, "corpus"), "*", SearchOption.AllDirectories).ToList();
+            Assert.All(corpusFiles, f => Assert.DoesNotContain("zqmemtext", System.Text.Encoding.UTF8.GetString(ReadShared(f)), StringComparison.Ordinal));
+            var keyword = await corpus.SearchAsync(
+                new MemoryAblationConfig { Name = "keyword", Mode = MemorySearchMode.Keyword }, new MemoryEvalQuery { Id = "m1", Text = "INV-2041", Category = "identifier" }, CancellationToken.None);
+            Assert.Equal(["mem-invoice"], keyword.Memories.Select(m => m.MemoryId));
+            Assert.Contains("zqmemtext", (await corpus.ListMemoriesAsync(CancellationToken.None)).Single().Content, StringComparison.Ordinal);
+        }
+
+        Assert.False(Directory.Exists(Path.Combine(runDirectory, "corpus")));
+    }
+
+    private static byte[] ReadShared(string path)
+    {
+        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
+        using var buffer = new MemoryStream();
+        stream.CopyTo(buffer);
+        return buffer.ToArray();
+    }
 }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~MemoryEvalCorpusTests" -v q`
Expected: build FAILS — `The best overload for 'CreateAsync' does not have a parameter named 'protection'`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs
@@ -1,8 +1,9 @@
 using System.Diagnostics;
+using Cortex.Contained.Agent.Host.Evaluation;
 using Cortex.Contained.Agent.Host.Reranking;
+using Cortex.Contained.Agent.Host.Storage;
 using MemoryMcp.Core.Configuration;
-using MemoryMcp.Core.Security;
 using MemoryMcp.Core.Services;
 using MemoryMcp.Core.Storage;
 using Microsoft.Data.Sqlite;
 using Microsoft.Extensions.Logging.Abstractions;
@@ -14,9 +15,10 @@ namespace Cortex.Contained.Agent.Host.Memory.Evaluation;
 /// A private copy of the curated-memory store (<c>memory.db</c> + content files) for one evaluation
 /// command. The live store is never touched: the copy is opened with its own <see cref="SqliteVecMemoryStore"/>
 /// (which backfills the keyword index in the copy) and searched with <see cref="HybridMemorySearch"/> per
 /// configuration. The copy holds raw memory text (D6), so <see cref="DisposeAsync"/> deletes it: no raw
-/// copy outlives the command, and outputs beside it are ids and numbers only.
+/// copy outlives the command, and outputs beside it are ids and numbers only. On an encrypted install the
+/// copy is encrypted with the evaluation ring (<see cref="EvalProtection"/>) for its short life as well.
 /// </summary>
 public sealed class MemoryEvalCorpus : IAsyncDisposable
 {
     /// <summary>Memories returned per query (the metrics read the first 10).</summary>
@@ -29,65 +31,76 @@ public sealed class MemoryEvalCorpus : IAsyncDisposable
     private readonly MemoryMcpOptions options;
     private readonly SqliteVecMemoryStore store;
     private readonly HybridMemorySearch search;
     private readonly IRerankStage? reranker;
+    private readonly string? password;
 
-    private MemoryEvalCorpus(string corpusDirectory, FileStream runLock, MemoryMcpOptions options, SqliteVecMemoryStore store, IEmbeddingService embedding, IRerankStage? reranker)
+    private MemoryEvalCorpus(string corpusDirectory, FileStream runLock, MemoryMcpOptions options, SqliteVecMemoryStore store, IEmbeddingService embedding, IRerankStage? reranker, string? password)
     {
         this.reranker = reranker;
         this.corpusDirectory = corpusDirectory;
         this.runLock = runLock;
         this.options = options;
         this.store = store;
+        this.password = password;
         this.search = new HybridMemorySearch(embedding, store, new StaticOptionsMonitor(options));
     }
 
     /// <summary>
     /// Copies the store under <paramref name="sourceDirectory"/> (named like <paramref name="live"/>) into
     /// <paramref name="runDirectory"/><c>/corpus</c> and opens it. Fails when the keyword index cannot be built.
+    /// <paramref name="protection"/> (default plaintext) decides how the source is read and how the copy
+    /// (database and content files) is encrypted.
     /// </summary>
     public static async Task<MemoryEvalCorpus> CreateAsync(
-        string sourceDirectory, string runDirectory, MemoryMcpOptions live, IEmbeddingService embedding, CancellationToken cancellationToken, IRerankStage? reranker = null)
+        string sourceDirectory,
+        string runDirectory,
+        MemoryMcpOptions live,
+        IEmbeddingService embedding,
+        CancellationToken cancellationToken,
+        IRerankStage? reranker = null,
+        EvalProtection? protection = null)
     {
         ArgumentNullException.ThrowIfNull(live);
         ArgumentNullException.ThrowIfNull(embedding);
+        protection ??= EvalProtection.Plaintext;
         var sourceDb = Path.Combine(sourceDirectory, live.DatabaseFileName);
         if (!File.Exists(sourceDb))
         {
             throw new InvalidOperationException($"No memory database at {sourceDb}.");
         }
 
         var corpusDirectory = Path.Combine(runDirectory, "corpus");
-        var options = new MemoryMcpOptions
-        {
-            DataDirectory = corpusDirectory,
-            DatabaseFileName = live.DatabaseFileName,
-            MemoriesSubdirectory = live.MemoriesSubdirectory,
-            SearchMaxContentLength = live.SearchMaxContentLength,
-            Ollama = live.Ollama,
-            Search = live.Search,
-        };
+        var options = CopyOptions(corpusDirectory, live);
 
         // Held for the life of the copy: a later command sweeps a copy only when nobody holds this lock.
         Directory.CreateDirectory(runDirectory);
         var runLock = new FileStream(Path.Combine(runDirectory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
+        var copyContent = protection.CopyContent();
         try
         {
             Directory.CreateDirectory(options.MemoriesDirectory);
-            using (var source = new SqliteConnection($"Data Source={sourceDb};Mode=ReadOnly;Pooling=False"))
-            using (var target = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False"))
-            {
-                source.Open();
-                target.Open();
-                source.BackupDatabase(target);
-            }
+            EncryptedSqlite.CopyDatabase(
+                sourceDb,
+                protection.SourcePassword(EncryptedMemoryStore.DatabasePurpose),
+                options.DatabasePath,
+                protection.CopyPassword(EncryptedMemoryStore.DatabasePurpose));
 
             var sourceMemories = Path.Combine(sourceDirectory, live.MemoriesSubdirectory);
             if (Directory.Exists(sourceMemories))
             {
+                var sourceContent = protection.SourceContent();
                 foreach (var file in Directory.EnumerateFiles(sourceMemories, "*.memory.data"))
                 {
-                    File.Copy(file, Path.Combine(options.MemoriesDirectory, Path.GetFileName(file)));
+                    var target = Path.Combine(options.MemoriesDirectory, Path.GetFileName(file));
+                    if (protection.IsEncrypted)
+                    {
+                        File.WriteAllBytes(target, copyContent.Encrypt(sourceContent.Decrypt(File.ReadAllBytes(file))));
+                    }
+                    else
+                    {
+                        File.Copy(file, target);
+                    }
                 }
             }
         }
         catch
@@ -96,10 +109,27 @@ public sealed class MemoryEvalCorpus : IAsyncDisposable
             runLock.Dispose();
             throw;
         }
 
-        var store = new SqliteVecMemoryStore(Options.Create(options), new NullContentEncryptor(), NullLogger<SqliteVecMemoryStore>.Instance);
-        var corpus = new MemoryEvalCorpus(corpusDirectory, runLock, options, store, embedding, reranker);
+        return await OpenAsync(corpusDirectory, runLock, options, embedding, reranker, protection, cancellationToken).ConfigureAwait(false);
+    }
+
+    private static MemoryMcpOptions CopyOptions(string corpusDirectory, MemoryMcpOptions live) => new()
+    {
+        DataDirectory = corpusDirectory,
+        DatabaseFileName = live.DatabaseFileName,
+        MemoriesSubdirectory = live.MemoriesSubdirectory,
+        SearchMaxContentLength = live.SearchMaxContentLength,
+        Ollama = live.Ollama,
+        Search = live.Search,
+    };
+
+    private static async Task<MemoryEvalCorpus> OpenAsync(
+        string corpusDirectory, FileStream runLock, MemoryMcpOptions options, IEmbeddingService embedding, IRerankStage? reranker, EvalProtection protection, CancellationToken cancellationToken)
+    {
+        var password = protection.CopyPassword(EncryptedMemoryStore.DatabasePurpose);
+        var store = new SqliteVecMemoryStore(Options.Create(options), protection.CopyContent(), NullLogger<SqliteVecMemoryStore>.Instance, password);
+        var corpus = new MemoryEvalCorpus(corpusDirectory, runLock, options, store, embedding, reranker, password);
         try
         {
             await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
             if (store.KeywordIndexUnavailableReason is { } reason)
@@ -161,9 +191,9 @@ public sealed class MemoryEvalCorpus : IAsyncDisposable
     /// <summary>Every memory with its title, tags and content (query seeds and the paraphrase guard; never written to reports).</summary>
     public async Task<IReadOnlyList<MemorySeedSource>> ListMemoriesAsync(CancellationToken cancellationToken)
     {
         var ids = new List<string>();
-        using (var connection = new SqliteConnection($"Data Source={this.options.DatabasePath};Mode=ReadOnly;Pooling=False"))
+        using (var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(this.options.DatabasePath, this.password, SqliteOpenMode.ReadOnly, pooling: false)))
         {
             await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
             using var cmd = connection.CreateCommand();
             cmd.CommandText = "SELECT MemoryId FROM chunks WHERE ChunkIndex = 0 ORDER BY MemoryId";
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~MemoryEvalCorpusTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 8 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Memory\Evaluation\MemoryEvalCorpus.cs tests\Cortex.Contained.Agent.Host.Tests\Memory\Evaluation\MemoryEvalCorpusTests.cs
git commit -m "feat(eval): encrypted memory-eval corpus copies" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: history-eval reads its dataset through `EvalProtection`

`EvalWorkspace` carries the protection and reads the dataset with `ReadDataset()` (sealed on an encrypted install). `HistoryEvalCli` gains an optional `protection` argument that reaches the workspace and every `EvalCorpus`; `HistoryRerankEval` uses the workspace's protection. Without the argument nothing changes.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/EvalDataset.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/EvalWorkspace.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryRerankEval.cs`
- Test (modify): `tests/Cortex.Contained.Agent.Host.Tests/History/HistoryEvalCliTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/History/HistoryEvalCliTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Agent.Host.Tests/History/HistoryEvalCliTests.cs
+++ b/tests/Cortex.Contained.Agent.Host.Tests/History/HistoryEvalCliTests.cs
@@ -200,8 +200,54 @@ public sealed class HistoryEvalCliTests : IAsyncDisposable
         Assert.Equal(1, await this.RunAsync("kappa", "--labels", labels));
         Assert.Contains("below 0.6", this.output.ToString(), StringComparison.Ordinal);
     }
 
+    [Fact]
+    public async Task Ablate_EncryptedInstall_DatasetSealedCopiesEncryptedReportsNumbersOnly()
+    {
+        var keys = Cortex.Contained.Agent.Host.Tests.Evaluation.EvalTestKeys.Ring();
+        var protection = Cortex.Contained.Agent.Host.Evaluation.EvalProtection.Encrypted(keys);
+        Directory.CreateDirectory(this.root);
+        long railway, invoice;
+        await using (var store = new MessageStore(this.source, NullLogger<MessageStore>.Instance, new MessageStoreEncryption(keys, TimeSpan.FromSeconds(5))))
+        {
+            railway = await store.SaveMessageAsync("u", "ch", "user", "we booked the railway journey to TownB zqrawtext", T0);
+            invoice = await store.SaveMessageAsync("u", "ch", "user", "invoice INV2041 is paid", T0.AddHours(5));
+        }
+
+        Task<int> Run(params string[] args) => HistoryEvalCli.RunAsync(
+            [.. args, "--dir", this.workspace, "--source", this.source, "--tz", "UTC"],
+            this.output,
+            _ => new FakeHistoryEmbedder(new Dictionary<string, string> { ["train"] = "railway", ["trip"] = "journey" }),
+            CancellationToken.None,
+            protection: protection);
+
+        Assert.Equal(0, await Run("init"));
+        this.WriteDataset(railway, invoice);
+        Assert.Equal(2, await Run("validate"));
+        Assert.Contains("plaintext on an encrypted install", this.output.ToString(), StringComparison.Ordinal);
+
+        var dataset = Path.Combine(this.workspace, "dataset.jsonl");
+        protection.WriteLines(dataset, File.ReadAllLines(dataset));
+        new EvalWorkspace(this.workspace).WritePlan(new AblationPlan
+        {
+            Configs = [new AblationConfig { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword }, new AblationConfig { Name = "hybrid-rrf60" }],
+        });
+        Assert.Equal(0, await Run("validate"));
+        Assert.Equal(0, await Run("ablate", "--split", "all"));
+
+        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
+        var runDir = Directory.GetDirectories(Path.Combine(this.workspace, "runs")).Single();
+        Assert.Equal(
+            Cortex.Contained.Contracts.Security.StoredFileState.Encrypted,
+            Cortex.Contained.Contracts.Security.SqliteFileHeader.Classify(Path.Combine(runDir, "messages.db")));
+        Assert.All(
+            Directory.EnumerateFiles(this.workspace, "*", SearchOption.AllDirectories),
+            f => Assert.DoesNotContain("zqrawtext", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(f)), StringComparison.Ordinal));
+        Assert.DoesNotContain("train trip", await File.ReadAllTextAsync(dataset), StringComparison.Ordinal);
+        Assert.True(File.Exists(Path.Combine(runDir, "report.md")));
+    }
+
     [Fact]
     public async Task Guard_ParaphraseWithLowOverlap_PassesAndReportsNumbersOnly()
     {
         var (railway, invoice) = await this.SeedAsync();
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~HistoryEvalCliTests|FullyQualifiedName~HistoryRerankEvalTests" -v q`
Expected: build FAILS — `The best overload for 'RunAsync' does not have a parameter named 'protection'`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/History/Evaluation/EvalDataset.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/History/Evaluation/EvalDataset.cs
+++ b/src/Cortex.Contained.Agent.Host/History/Evaluation/EvalDataset.cs
@@ -82,12 +82,15 @@ public static class EvalDataset
     public static IReadOnlyList<string> Categories { get; } =
         ["lookup", "paraphrase", "cross_language", "temporal", "identifier", "enumeration", "abstention"];
 
     /// <summary>Reads one query per line; blank lines are skipped.</summary>
-    public static IReadOnlyList<EvalQuery> Read(string path) =>
-        File.ReadLines(path)
+    public static IReadOnlyList<EvalQuery> Read(string path) => Parse(File.ReadLines(path), path);
+
+    /// <summary>Parses one query per line of <paramref name="source"/>; blank lines are skipped.</summary>
+    public static IReadOnlyList<EvalQuery> Parse(IEnumerable<string> lines, string source) =>
+        lines
             .Where(line => !string.IsNullOrWhiteSpace(line))
-            .Select(line => JsonSerializer.Deserialize<EvalQuery>(line, Json) ?? throw new InvalidDataException($"Bad line in {path}"))
+            .Select(line => JsonSerializer.Deserialize<EvalQuery>(line, Json) ?? throw new InvalidDataException($"Bad line in {source}"))
             .ToList();
 
     /// <summary>Serialises one query as a JSONL line.</summary>
     public static string ToLine(EvalQuery query) => JsonSerializer.Serialize(query, Json);
```

`src/Cortex.Contained.Agent.Host/History/Evaluation/EvalWorkspace.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/History/Evaluation/EvalWorkspace.cs
+++ b/src/Cortex.Contained.Agent.Host/History/Evaluation/EvalWorkspace.cs
@@ -1,6 +1,7 @@
 using System.Text.Json;
 using System.Text.Json.Serialization;
+using Cortex.Contained.Agent.Host.Evaluation;
 
 namespace Cortex.Contained.Agent.Host.History.Evaluation;
 
 /// <summary>Which configuration the held-out run tests against which baseline, and the non-inferiority margin.</summary>
@@ -38,18 +39,22 @@ public sealed class EvalWorkspace
         DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
         Converters = { new JsonStringEnumConverter() },
     };
 
-    public EvalWorkspace(string directory)
+    public EvalWorkspace(string directory, EvalProtection? protection = null)
     {
         ArgumentException.ThrowIfNullOrWhiteSpace(directory);
         this.Directory = directory;
+        this.Protection = protection ?? EvalProtection.Plaintext;
     }
 
     /// <summary>Workspace root.</summary>
     public string Directory { get; }
 
-    /// <summary>Labelled queries (<see cref="EvalQuery"/> per line).</summary>
+    /// <summary>How the dataset is sealed and the corpus copies are encrypted.</summary>
+    public EvalProtection Protection { get; }
+
+    /// <summary>Labelled queries (<see cref="EvalQuery"/> per line; sealed on an encrypted install).</summary>
     public string DatasetPath => Path.Combine(this.Directory, "dataset.jsonl");
 
     /// <summary>Pre-registered analysis plan.</summary>
     public string AnalysisPlanPath => Path.Combine(this.Directory, "analysis-plan.md");
@@ -65,8 +70,14 @@ public sealed class EvalWorkspace
 
     /// <summary>Writes indented JSON.</summary>
     public static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
 
+    /// <summary>Reads <c>dataset.jsonl</c> through <see cref="Protection"/>.</summary>
+    public IReadOnlyList<EvalQuery> ReadDataset() =>
+        File.Exists(this.DatasetPath)
+            ? EvalDataset.Parse(this.Protection.ReadLines(this.DatasetPath), this.DatasetPath)
+            : throw new InvalidOperationException("dataset.jsonl is missing.");
+
     /// <summary>Reads <c>ablation.json</c>.</summary>
     public AblationPlan ReadPlan() =>
         File.Exists(this.AblationPlanPath)
             ? JsonSerializer.Deserialize<AblationPlan>(File.ReadAllText(this.AblationPlanPath), Json) ?? new AblationPlan()
```

`src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs
+++ b/src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs
@@ -1,5 +1,6 @@
 using System.Globalization;
+using Cortex.Contained.Agent.Host.Evaluation;
 using Cortex.Contained.Agent.Host.Reranking;
 using Cortex.Contained.Agent.Host.Reranking.Evaluation;
 using MemoryMcp.Core.Configuration;
 using MemoryMcp.Core.Services;
@@ -68,9 +69,10 @@ public static class HistoryEvalCli
         TextWriter output,
         Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory,
         CancellationToken cancellationToken,
         HistoryOptions? configured = null,
-        Func<RerankerOptions, IReranker>? rerankerFactory = null)
+        Func<RerankerOptions, IReranker>? rerankerFactory = null,
+        EvalProtection? protection = null)
     {
         ArgumentNullException.ThrowIfNull(args);
         ArgumentNullException.ThrowIfNull(output);
         if (args.Count == 0)
@@ -80,9 +82,9 @@ public static class HistoryEvalCli
         }
 
         configured ??= HistoryCliConfiguration.Load();
         rerankerFactory ??= RerankEvalSetup.LiveReranker;
-        var workspace = new EvalWorkspace(Option(args, "--dir") ?? configured.EvalArtifactsDirectory);
+        var workspace = new EvalWorkspace(Option(args, "--dir") ?? configured.EvalArtifactsDirectory, protection);
         var source = Option(args, "--source") ?? "/app/state/messages.db";
         var baseline = new HistoryOptions { Enabled = true, TimeZone = Option(args, "--tz") ?? string.Empty };
         Directory.CreateDirectory(workspace.Directory);
         if (HistoryCliConfiguration.ArtifactsWarning(workspace.Directory, configured.EvalArtifactsDirectory, "History:EvalArtifactsDirectory") is { } warning)
@@ -150,9 +152,9 @@ public static class HistoryEvalCli
     }
 
     private static async Task<int> ValidateAsync(EvalWorkspace workspace, TextWriter output)
     {
-        var queries = EvalDataset.Read(workspace.DatasetPath);
+        var queries = workspace.ReadDataset();
         var errors = EvalDataset.Validate(queries);
         foreach (var error in errors)
         {
             await output.WriteLineAsync("invalid " + error).ConfigureAwait(false);
@@ -166,12 +168,12 @@ public static class HistoryEvalCli
 
     private static async Task<int> GuardAsync(
         EvalWorkspace workspace, string source, HistoryOptions baseline, Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory, TextWriter output, CancellationToken cancellationToken)
     {
-        var queries = EvalDataset.Read(workspace.DatasetPath).Where(q => q.Category == "paraphrase").ToList();
+        var queries = workspace.ReadDataset().Where(q => q.Category == "paraphrase").ToList();
         var runDir = NewRunDirectory(workspace, "guard");
         AblationConfig[] keywordOnly = [new AblationConfig { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword }];
-        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, keywordOnly, embedderFactory, cancellationToken).ConfigureAwait(false);
+        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, keywordOnly, embedderFactory, cancellationToken, protection: workspace.Protection).ConfigureAwait(false);
         var segments = corpus.SegmentCount();
         var checks = new List<ParaphraseCheck>();
         foreach (var query in queries)
         {
@@ -218,18 +220,18 @@ public static class HistoryEvalCli
         string kind,
         Func<IOptionsMonitor<HistoryOptions>, IHistoryEmbedder> embedderFactory,
         CancellationToken cancellationToken)
     {
-        var all = EvalDataset.Read(workspace.DatasetPath);
+        var all = workspace.ReadDataset();
         var errors = EvalDataset.Validate(all);
         if (errors.Count > 0)
         {
             throw new InvalidOperationException($"dataset.jsonl has {errors.Count} problems; run 'validate'.");
         }
 
         var queries = split == "all" ? all : all.Where(q => EvalSplits.Assign(q.Id) == split).ToList();
         var runDir = NewRunDirectory(workspace, kind + "-" + split);
-        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, configs, embedderFactory, cancellationToken).ConfigureAwait(false);
+        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, configs, embedderFactory, cancellationToken, protection: workspace.Protection).ConfigureAwait(false);
         var report = await AblationRunner.RunAsync(configs, queries, corpus.SearchAsync, baselineConfig, cancellationToken).ConfigureAwait(false);
         EvalWorkspace.WriteJson(Path.Combine(runDir, "results.json"), report);
         await File.WriteAllTextAsync(
             Path.Combine(runDir, "report.md"),
```

`src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryRerankEval.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryRerankEval.cs
+++ b/src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryRerankEval.cs
@@ -45,9 +45,9 @@ internal static class HistoryRerankEval
         var queries = ReadQueries(workspace, split);
         var (reranker, stage) = CreateStage(rerankerFactory, Option(args, "--reranker"), RerankEvalSetup.IntOption(Option(args, "--timeout-ms"), RerankEvalSetup.CurveTimeoutMs));
 
         var runDir = NewRunDirectory(workspace, $"rerank-curve-{condition}-{split}");
-        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, [baseConfig], embedderFactory, cancellationToken, stage).ConfigureAwait(false);
+        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, [baseConfig], embedderFactory, cancellationToken, stage, workspace.Protection).ConfigureAwait(false);
         string? coldNote = null;
         double? readyMs = null;
         if (condition == RerankConditions.Cold)
         {
@@ -105,9 +105,9 @@ internal static class HistoryRerankEval
         var queries = ReadQueries(workspace, EvalSplits.Test);
         var (_, stage) = CreateStage(rerankerFactory, Option(args, "--reranker"), RerankEvalSetup.IntOption(Option(args, "--timeout-ms"), new RerankerOptions().TimeoutMs));
 
         var runDir = NewRunDirectory(workspace, "gate-r");
-        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, [candidate], embedderFactory, cancellationToken, stage).ConfigureAwait(false);
+        await using var corpus = await EvalCorpus.CreateAsync(source, runDir, baseline, [candidate], embedderFactory, cancellationToken, stage, workspace.Protection).ConfigureAwait(false);
         var report = await AblationRunner.RunAsync([unreranked, candidate], queries, corpus.SearchAsync, unreranked.Name, cancellationToken).ConfigureAwait(false);
         var busy = new List<double>();
         await using (StartEmbedderLoad(embedderFactory, baseline))
         {
@@ -129,9 +129,9 @@ internal static class HistoryRerankEval
     }
 
     private static List<EvalQuery> ReadQueries(EvalWorkspace workspace, string split)
     {
-        var all = EvalDataset.Read(workspace.DatasetPath);
+        var all = workspace.ReadDataset();
         if (EvalDataset.Validate(all) is { Count: > 0 } errors)
         {
             throw new InvalidOperationException($"dataset.jsonl has {errors.Count} problems; run 'validate'.");
         }
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~HistoryEvalCliTests|FullyQualifiedName~HistoryRerankEvalTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 16 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\History\Evaluation\EvalDataset.cs src\Cortex.Contained.Agent.Host\History\Evaluation\EvalWorkspace.cs src\Cortex.Contained.Agent.Host\History\Evaluation\HistoryEvalCli.cs src\Cortex.Contained.Agent.Host\History\Evaluation\HistoryRerankEval.cs tests\Cortex.Contained.Agent.Host.Tests\History\HistoryEvalCliTests.cs
git commit -m "feat(eval): history-eval encrypted-workspace mode" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: memory-eval reads and writes through `EvalProtection`

Same for memory: `MemoryEvalWorkspace.ReadDataset()`, sealed `seeds.jsonl` from `sample`, the protection passed to every corpus copy and to `MemoryRerankEval`.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalDataset.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalWorkspace.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryRerankEval.cs`
- Test (modify): `tests/Cortex.Contained.Agent.Host.Tests/Memory/Evaluation/MemoryEvalCliTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Memory/Evaluation/MemoryEvalCliTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Agent.Host.Tests/Memory/Evaluation/MemoryEvalCliTests.cs
+++ b/tests/Cortex.Contained.Agent.Host.Tests/Memory/Evaluation/MemoryEvalCliTests.cs
@@ -171,5 +171,32 @@ public sealed class MemoryEvalCliTests : IDisposable
 
         Assert.Equal(0, result.Code);
         Assert.Contains("usable", result.Output, StringComparison.Ordinal);
     }
+
+    [Fact]
+    public async Task SampleAndAblate_EncryptedInstall_SeedsAndDatasetSealedNoRawTextOnDisk()
+    {
+        var keys = Cortex.Contained.Agent.Host.Tests.Evaluation.EvalTestKeys.Ring();
+        var protection = Cortex.Contained.Agent.Host.Evaluation.EvalProtection.Encrypted(keys);
+        await MemoryEvalCorpusTests.SeedEncryptedAsync(
+            this.live, keys, ("mem-invoice", null, "Invoice INV-2041 zqmemtext was paid in March", ["finance"]), ("mem-train", "Trip", "Train trip to TownB", ["travel"]));
+        async Task<int> Run(params string[] args)
+        {
+            using var output = new StringWriter();
+            return await MemoryEvalCli.RunAsync([.. args, "--dir", this.workspace.Directory], output, this.live, _ => new FakeMemoryEmbedder(), CancellationToken.None, protection: protection);
+        }
+
+        Assert.Equal(0, await Run("init"));
+        Assert.Equal(0, await Run("sample", "--count", "5"));
+        Assert.True(Cortex.Contained.Agent.Host.Evaluation.EvalProtection.IsSealed(this.workspace.SeedsPath));
+        protection.WriteLines(this.workspace.DatasetPath, [MemoryEvalDataset.ToLine(new MemoryEvalQuery { Id = "m001", Text = "invoice paid", Category = "lookup", Judgments = [new MemoryJudgment("mem-invoice", 2)] })]);
+        this.workspace.WritePlan(new MemoryAblationPlan { Configs = MemoryAblationGrid.Default().Where(c => c.Name is "dense" or "hybrid-rrf60").ToList() });
+
+        Assert.Equal(0, await Run("ablate", "--split", "all"));
+
+        Assert.All(
+            Directory.EnumerateFiles(this.workspace.Directory, "*", SearchOption.AllDirectories),
+            f => Assert.DoesNotContain("zqmemtext", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(f)), StringComparison.Ordinal));
+        Assert.Single(Directory.EnumerateFiles(this.workspace.RunsDirectory, "report.md", SearchOption.AllDirectories));
+    }
 }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~MemoryEvalCliTests|FullyQualifiedName~MemoryRerankEvalTests|FullyQualifiedName~MemoryEvalCorpusTests" -v q`
Expected: build FAILS — `The best overload for 'RunAsync' does not have a parameter named 'protection'`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs
@@ -1,7 +1,8 @@
 using System.Globalization;
 using System.Runtime.InteropServices;
 using System.Text.Json;
+using Cortex.Contained.Agent.Host.Evaluation;
 using Cortex.Contained.Agent.Host.Reranking;
 using Cortex.Contained.Agent.Host.Reranking.Evaluation;
 using Cortex.Contained.Contracts.Config.Yaml;
 using MemoryMcp.Core.Configuration;
@@ -124,9 +125,10 @@ public static class MemoryEvalCli
         TextWriter output,
         MemoryMcpOptions live,
         Func<MemoryMcpOptions, IEmbeddingService> embedderFactory,
         CancellationToken cancellationToken,
-        Func<RerankerOptions, IReranker>? rerankerFactory = null)
+        Func<RerankerOptions, IReranker>? rerankerFactory = null,
+        EvalProtection? protection = null)
     {
         ArgumentNullException.ThrowIfNull(args);
         rerankerFactory ??= RerankEvalSetup.LiveReranker;
         ArgumentNullException.ThrowIfNull(output);
@@ -137,9 +139,9 @@ public static class MemoryEvalCli
             await output.WriteLineAsync(Usage).ConfigureAwait(false);
             return 2;
         }
 
-        var workspace = new MemoryEvalWorkspace(Option(args, "--dir") ?? MemoryEvalWorkspace.DefaultDirectory);
+        var workspace = new MemoryEvalWorkspace(Option(args, "--dir") ?? MemoryEvalWorkspace.DefaultDirectory, protection);
         Directory.CreateDirectory(workspace.Directory);
         if (MemoryEvalCorpus.SweepAbandoned(workspace.RunsDirectory) is var swept and > 0)
         {
             await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
@@ -249,17 +251,17 @@ public static class MemoryEvalCli
     {
         await using var corpus = await context.OpenCorpusAsync("sample", cancellationToken).ConfigureAwait(false);
         var memories = await corpus.ListMemoriesAsync(cancellationToken).ConfigureAwait(false);
         var seeds = MemoryQuerySeeds.Select(memories, count);
-        await File.WriteAllLinesAsync(context.Workspace.SeedsPath, seeds.Select(s => JsonSerializer.Serialize(s, Json)), cancellationToken).ConfigureAwait(false);
+        context.Workspace.Protection.WriteLines(context.Workspace.SeedsPath, seeds.Select(s => JsonSerializer.Serialize(s, Json)));
         await context.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
             $"{seeds.Count} seeds from {memories.Count} memories: {context.Workspace.SeedsPath}. It holds raw memory text: delete it once dataset.jsonl is written.")).ConfigureAwait(false);
         return 0;
     }
 
     private static async Task<int> ValidateAsync(MemoryEvalWorkspace workspace, TextWriter output)
     {
-        var queries = MemoryEvalDataset.Read(workspace.DatasetPath);
+        var queries = workspace.ReadDataset();
         var errors = MemoryEvalDataset.ValidateForRegistration(queries);
         foreach (var error in errors)
         {
             await output.WriteLineAsync("invalid " + error).ConfigureAwait(false);
@@ -273,9 +275,9 @@ public static class MemoryEvalCli
     }
 
     private static async Task<int> GuardAsync(CommandContext context, CancellationToken cancellationToken)
     {
-        var queries = MemoryEvalDataset.Read(context.Workspace.DatasetPath).Where(q => q.Category == "paraphrase").ToList();
+        var queries = context.Workspace.ReadDataset().Where(q => q.Category == "paraphrase").ToList();
         var runDir = NewRunDirectory(context.Workspace, "guard");
         List<ParaphraseCheck> checks;
         await using (var corpus = await context.OpenCorpusAsync(runDir, cancellationToken).ConfigureAwait(false))
         {
@@ -302,9 +304,9 @@ public static class MemoryEvalCli
     }
 
     private static async Task<int> PoolAsync(CommandContext context, int depth, CancellationToken cancellationToken)
     {
-        var queries = MemoryEvalDataset.Read(context.Workspace.DatasetPath);
+        var queries = context.Workspace.ReadDataset();
         var plan = context.Workspace.ReadPlan();
         var lines = new List<string>();
         await using (var corpus = await context.OpenCorpusAsync("pool", cancellationToken).ConfigureAwait(false))
         {
@@ -351,9 +353,9 @@ public static class MemoryEvalCli
 
     private static async Task<(string RunDirectory, AblationReport Report)> RunAsync(
         CommandContext context, IReadOnlyList<MemoryAblationConfig> configs, string baseline, string split, string kind, CancellationToken cancellationToken)
     {
-        var all = MemoryEvalDataset.Read(context.Workspace.DatasetPath);
+        var all = context.Workspace.ReadDataset();
         var errors = MemoryEvalDataset.Validate(all);
         if (errors.Count > 0)
         {
             throw new InvalidOperationException($"dataset.jsonl has {errors.Count} problems; run 'validate'.");
@@ -416,9 +418,9 @@ public static class MemoryEvalCli
         /// <summary>Opens a copy of the live store under <paramref name="runDirectory"/> (a name is placed under <c>runs/</c>).</summary>
         public Task<MemoryEvalCorpus> OpenCorpusAsync(string runDirectory, CancellationToken cancellationToken)
         {
             var directory = Path.IsPathRooted(runDirectory) ? runDirectory : NewRunDirectory(this.Workspace, runDirectory);
-            return MemoryEvalCorpus.CreateAsync(this.Live.DataDirectory, directory, this.Live, this.EmbedderFactory(this.Live), cancellationToken);
+            return MemoryEvalCorpus.CreateAsync(this.Live.DataDirectory, directory, this.Live, this.EmbedderFactory(this.Live), cancellationToken, protection: this.Workspace.Protection);
         }
     }
 
     private sealed class StaticOptionsMonitor(MemoryMcpOptions value) : IOptionsMonitor<MemoryMcpOptions>
```

`src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalDataset.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalDataset.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalDataset.cs
@@ -75,12 +75,15 @@ public static class MemoryEvalDataset
     public static IReadOnlyList<string> Categories { get; } =
         ["lookup", "paraphrase", "cross_language", "identifier", "tag_filtered", "multi", "abstention"];
 
     /// <summary>Reads one query per line; blank lines are skipped.</summary>
-    public static IReadOnlyList<MemoryEvalQuery> Read(string path) =>
-        File.ReadLines(path)
+    public static IReadOnlyList<MemoryEvalQuery> Read(string path) => Parse(File.ReadLines(path), path);
+
+    /// <summary>Parses one query per line of <paramref name="source"/>; blank lines are skipped.</summary>
+    public static IReadOnlyList<MemoryEvalQuery> Parse(IEnumerable<string> lines, string source) =>
+        lines
             .Where(line => !string.IsNullOrWhiteSpace(line))
-            .Select(line => JsonSerializer.Deserialize<MemoryEvalQuery>(line, Json) ?? throw new InvalidDataException($"Bad line in {path}"))
+            .Select(line => JsonSerializer.Deserialize<MemoryEvalQuery>(line, Json) ?? throw new InvalidDataException($"Bad line in {source}"))
             .ToList();
 
     /// <summary>Serialises one query as a JSONL line.</summary>
     public static string ToLine(MemoryEvalQuery query) => JsonSerializer.Serialize(query, Json);
```

`src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalWorkspace.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalWorkspace.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalWorkspace.cs
@@ -1,6 +1,7 @@
 using System.Text.Json;
 using System.Text.Json.Serialization;
+using Cortex.Contained.Agent.Host.Evaluation;
 
 namespace Cortex.Contained.Agent.Host.Memory.Evaluation;
 
 /// <summary>Which configuration the held-out run tests against the baseline, and the non-inferiority margin.</summary>
@@ -42,21 +43,25 @@ public sealed class MemoryEvalWorkspace
         DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
         Converters = { new JsonStringEnumConverter() },
     };
 
-    public MemoryEvalWorkspace(string directory)
+    public MemoryEvalWorkspace(string directory, EvalProtection? protection = null)
     {
         ArgumentException.ThrowIfNullOrWhiteSpace(directory);
         this.Directory = directory;
+        this.Protection = protection ?? EvalProtection.Plaintext;
     }
 
     /// <summary>Workspace root.</summary>
     public string Directory { get; }
 
-    /// <summary>Labelled queries (<see cref="MemoryEvalQuery"/> per line).</summary>
+    /// <summary>How the dataset and seeds are sealed and the corpus copies are encrypted.</summary>
+    public EvalProtection Protection { get; }
+
+    /// <summary>Labelled queries (<see cref="MemoryEvalQuery"/> per line; sealed on an encrypted install).</summary>
     public string DatasetPath => Path.Combine(this.Directory, "dataset.jsonl");
 
-    /// <summary>Query-authoring seeds (raw memory text; delete after authoring).</summary>
+    /// <summary>Query-authoring seeds (raw memory text, sealed on an encrypted install; delete after authoring).</summary>
     public string SeedsPath => Path.Combine(this.Directory, "seeds.jsonl");
 
     /// <summary>Unjudged memory ids per query, for the judge (ids only).</summary>
     public string PoolPath => Path.Combine(this.Directory, "pool.jsonl");
@@ -78,8 +83,14 @@ public sealed class MemoryEvalWorkspace
 
     /// <summary>Writes indented JSON.</summary>
     public static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
 
+    /// <summary>Reads <c>dataset.jsonl</c> through <see cref="Protection"/>.</summary>
+    public IReadOnlyList<MemoryEvalQuery> ReadDataset() =>
+        File.Exists(this.DatasetPath)
+            ? MemoryEvalDataset.Parse(this.Protection.ReadLines(this.DatasetPath), this.DatasetPath)
+            : throw new InvalidOperationException("dataset.jsonl is missing.");
+
     /// <summary>Reads <c>ablation.json</c>.</summary>
     public MemoryAblationPlan ReadPlan() =>
         File.Exists(this.AblationPlanPath)
             ? JsonSerializer.Deserialize<MemoryAblationPlan>(File.ReadAllText(this.AblationPlanPath), Json) ?? new MemoryAblationPlan()
@@ -101,9 +112,9 @@ public sealed class MemoryEvalWorkspace
             {
                 throw new InvalidOperationException("ablation.json has no holdout section: choose the configuration on the dev split before registering.");
             }
 
-            var errors = MemoryEvalDataset.ValidateForRegistration(MemoryEvalDataset.Read(this.DatasetPath));
+            var errors = MemoryEvalDataset.ValidateForRegistration(this.ReadDataset());
             if (errors.Count > 0)
             {
                 throw new InvalidOperationException($"dataset.jsonl is not ready for registration ({errors.Count} problems); run 'validate'.");
             }
```

`src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryRerankEval.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryRerankEval.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryRerankEval.cs
@@ -50,9 +50,9 @@ internal static class MemoryRerankEval
         IReadOnlyList<RerankCurveRow> rows;
         AblationReport report;
         int? loadCompleted = null;
         int? loadFailed = null;
-        await using (var corpus = await MemoryEvalCorpus.CreateAsync(live.DataDirectory, runDir, live, embedderFactory(live), cancellationToken, stage).ConfigureAwait(false))
+        await using (var corpus = await MemoryEvalCorpus.CreateAsync(live.DataDirectory, runDir, live, embedderFactory(live), cancellationToken, stage, workspace.Protection).ConfigureAwait(false))
         {
             if (condition == RerankConditions.Cold)
             {
                 readyMs = await WaitReadyAsync(reranker, cancellationToken).ConfigureAwait(false);
@@ -109,9 +109,9 @@ internal static class MemoryRerankEval
 
         var runDir = NewRunDirectory(workspace, "gate-r");
         AblationReport report;
         var busy = new List<double>();
-        await using (var corpus = await MemoryEvalCorpus.CreateAsync(live.DataDirectory, runDir, live, embedderFactory(live), cancellationToken, stage).ConfigureAwait(false))
+        await using (var corpus = await MemoryEvalCorpus.CreateAsync(live.DataDirectory, runDir, live, embedderFactory(live), cancellationToken, stage, workspace.Protection).ConfigureAwait(false))
         {
             report = await MemoryAblationRunner.RunAsync([unreranked, candidate], queries, corpus.SearchAsync, unreranked.Name, cancellationToken).ConfigureAwait(false);
             await using (StartEmbedderLoad(embedderFactory, live))
             {
@@ -134,9 +134,9 @@ internal static class MemoryRerankEval
     }
 
     private static List<MemoryEvalQuery> ReadQueries(MemoryEvalWorkspace workspace, string split)
     {
-        var all = MemoryEvalDataset.Read(workspace.DatasetPath);
+        var all = workspace.ReadDataset();
         if (MemoryEvalDataset.Validate(all) is { Count: > 0 } errors)
         {
             throw new InvalidOperationException($"dataset.jsonl has {errors.Count} problems; run 'validate'.");
         }
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~MemoryEvalCliTests|FullyQualifiedName~MemoryRerankEvalTests|FullyQualifiedName~MemoryEvalCorpusTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 16 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Memory\Evaluation\MemoryEvalCli.cs src\Cortex.Contained.Agent.Host\Memory\Evaluation\MemoryEvalDataset.cs src\Cortex.Contained.Agent.Host\Memory\Evaluation\MemoryEvalWorkspace.cs src\Cortex.Contained.Agent.Host\Memory\Evaluation\MemoryRerankEval.cs tests\Cortex.Contained.Agent.Host.Tests\Memory\Evaluation\MemoryEvalCliTests.cs
git commit -m "feat(eval): memory-eval encrypted-workspace mode" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 7: Evaluation jobs and workspace files (agent services)

`EvaluationJobService` runs evaluation commands in the agent process: one at a time, in the background, output capped at 64 KiB, options that point at other files or endpoints refused, the passphrase never echoed, refused while the storage key is missing (E1). `EvaluationFiles` reads and writes the allow-listed owner files and does not rewrite identical content (registration hashes stay valid, E5). The hub contract `IEvaluationHub` is added here (composed into `IAgentHub` in Task 8).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/EvaluationFiles.cs`
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/EvaluationJobService.cs`
- Create: `src/Cortex.Contained.Contracts/Hub/IEvaluationHub.cs`
- Test (new): `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/EvaluationFilesTests.cs`
- Test (new): `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/EvaluationJobServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/EvaluationFilesTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation;

public sealed class EvaluationFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"eval-files-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());
    private readonly EvaluationFiles files;

    public EvaluationFilesTests()
    {
        this.files = new EvaluationFiles(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["history-eval"] = Path.Combine(this.root, "hybrid") },
            () => this.protection);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Write_Dataset_SealedAndReadBack_IdenticalContentNotRewritten()
    {
        var request = new EvaluationFileWriteDto { Tool = "history-eval", Name = "dataset.jsonl", Content = "{\"id\":\"e1\",\"text\":\"zqsecret\"}\n" };

        var written = this.files.Write(request);
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(this.root, "hybrid", "dataset.jsonl"));
        var again = this.files.Write(request);
        var read = this.files.Read(new EvaluationFileRequestDto { Tool = "history-eval", Name = "dataset.jsonl" });

        Assert.True(written is { Written: true, Sealed: true });
        Assert.True(again is { Written: false, Unchanged: true });
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(this.root, "hybrid", "dataset.jsonl")));
        Assert.Equal(request.Content, read.Content);
        Assert.True(read.Sealed);
        Assert.DoesNotContain("zqsecret", File.ReadAllText(Path.Combine(this.root, "hybrid", "dataset.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_AnalysisPlan_StaysPlaintext()
    {
        Assert.True(this.files.Write(new EvaluationFileWriteDto { Tool = "history-eval", Name = "analysis-plan.md", Content = "# plan" }).Written);

        Assert.Equal("# plan", File.ReadAllText(Path.Combine(this.root, "hybrid", "analysis-plan.md")));
    }

    [Theory]
    [InlineData("history-eval", "../messages.db")]
    [InlineData("history-eval", "preregistration.json")]
    [InlineData("history-eval", "runs")]
    [InlineData("memory-eval", "dataset.jsonl")]
    public void Write_NameOutsideTheAllowList_Refused(string tool, string name)
    {
        var result = this.files.Write(new EvaluationFileWriteDto { Tool = tool, Name = name, Content = "x" });

        Assert.False(result.Written);
        Assert.NotNull(result.Error);
    }
}
```

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/EvaluationJobServiceTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation;

public sealed class EvaluationJobServiceTests : IDisposable
{
    private readonly TaskCompletionSource<int> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<EvaluationRun> runs = [];
    private readonly EvaluationJobService service;

    public EvaluationJobServiceTests()
    {
        var commands = new Dictionary<string, EvaluationCommand>(StringComparer.Ordinal)
        {
            ["history-eval"] = async (run, ct) =>
            {
                this.runs.Add(run);
                await run.Output.WriteLineAsync("Report: /app/state/eval/hybrid/runs/x/report.md");
                return await this.gate.Task.WaitAsync(ct);
            },
        };
        this.service = new EvaluationJobService(commands, () => EvalProtection.Encrypted(EvalTestKeys.Ring()), TimeProvider.System, NullLogger<EvaluationJobService>.Instance);
    }

    public void Dispose() => this.service.Dispose();

    [Fact]
    public async Task Start_RunsInTheBackgroundEncryptedAndKeepsTheOutput()
    {
        var started = this.service.Start(new EvaluationStartRequestDto { Tool = "history-eval", Args = ["ablate", "--split", "dev"], Passphrase = "zqpassphrase-1234" });

        Assert.Equal(EvaluationJobStates.Running, started.State);
        Assert.True(started.Encrypted);
        Assert.Equal("ablate", started.Command);
        this.gate.SetResult(0);
        await this.service.WhenIdleAsync();

        var done = this.service.Get(started.Id)!;
        Assert.Equal(EvaluationJobStates.Succeeded, done.State);
        Assert.Equal(0, done.ExitCode);
        Assert.Contains("report.md", done.Output, StringComparison.Ordinal);
        Assert.True(Assert.Single(this.runs).Protection.IsEncrypted);
        Assert.Equal("zqpassphrase-1234", this.runs[0].Passphrase);
        Assert.DoesNotContain("zqpassphrase", System.Text.Json.JsonSerializer.Serialize(done), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_WhileAJobRuns_RefusedThenCancelEndsTheRunningJob()
    {
        var first = this.service.Start(new EvaluationStartRequestDto { Tool = "history-eval", Args = ["holdout"] });

        var second = this.service.Start(new EvaluationStartRequestDto { Tool = "history-eval", Args = ["ablate"] });
        Assert.Equal(EvaluationJobStates.Refused, second.State);
        Assert.Contains("Another evaluation job", second.Output, StringComparison.Ordinal);

        Assert.True(this.service.Cancel(first.Id));
        await this.service.WhenIdleAsync();
        Assert.Equal(EvaluationJobStates.Cancelled, this.service.Get(first.Id)!.State);
        Assert.False(this.service.Cancel(first.Id));
    }

    [Theory]
    [InlineData("--dir")]
    [InlineData("--source")]
    [InlineData("--embedder")]
    public void Start_PathOrEndpointOption_Refused(string option)
    {
        var job = this.service.Start(new EvaluationStartRequestDto { Tool = "history-eval", Args = ["ablate", option, "/tmp/x"] });

        Assert.Equal(EvaluationJobStates.Refused, job.State);
        Assert.Empty(this.runs);
    }

    [Fact]
    public void Start_UnknownToolOrLockedStorage_Refused()
    {
        Assert.Equal(EvaluationJobStates.Refused, this.service.Start(new EvaluationStartRequestDto { Tool = "history-smoke", Args = ["run"] }).State);

        using var locked = new EvaluationJobService(
            new Dictionary<string, EvaluationCommand> { ["history-eval"] = (_, _) => Task.FromResult(0) },
            () => EvalProtection.Encrypted(new StorageKeyRing()),
            TimeProvider.System,
            NullLogger<EvaluationJobService>.Instance);
        var job = locked.Start(new EvaluationStartRequestDto { Tool = "history-eval", Args = ["ablate"] });

        Assert.Equal(EvaluationJobStates.Refused, job.State);
        Assert.Contains("storage key", job.Output, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~EvaluationJobServiceTests|FullyQualifiedName~EvaluationFilesTests" -v q`
Expected: build FAILS — `The type or namespace name 'EvaluationJobService' could not be found`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/EvaluationFiles.cs` (new):

```csharp
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Evaluation;

/// <summary>
/// The owner-authored files of the evaluation workspaces, read and written through the Bridge (on an encrypted
/// install the files that carry text are sealed, so they cannot be edited in the container). Only allow-listed
/// file names of the configured workspaces are reachable; run outputs are not (they are plaintext numbers and
/// can be read in the container).
/// </summary>
public sealed class EvaluationFiles
{
    // Files that may carry personal text: sealed on an encrypted install.
    private static readonly HashSet<string> SealedNames = new(StringComparer.Ordinal)
    {
        "dataset.jsonl", "seeds.jsonl", "dictionary.jsonl", "dictionary.suggested.jsonl",
    };

    private static readonly Dictionary<string, (string[] Readable, string[] Writable)> Names = new(StringComparer.Ordinal)
    {
        ["history-eval"] = (["dataset.jsonl", "dataset.example.jsonl", "analysis-plan.md", "ablation.json", "preregistration.json", "judge-agreement.csv"],
            ["dataset.jsonl", "analysis-plan.md", "ablation.json", "judge-agreement.csv"]),
        ["memory-eval"] = (["dataset.jsonl", "dataset.example.jsonl", "seeds.jsonl", "pool.jsonl", "query-generation.md", "analysis-plan.md", "ablation.json", "preregistration.json", "judge-agreement.csv"],
            ["dataset.jsonl", "analysis-plan.md", "ablation.json", "judge-agreement.csv"]),
        ["eval-corpus"] = (["dictionary.jsonl", "dictionary.suggested.jsonl"], ["dictionary.jsonl"]),
    };

    private readonly IReadOnlyDictionary<string, string> directories;
    private readonly Func<EvalProtection> protection;

    /// <param name="directories">Workspace directory per tool.</param>
    /// <param name="protection">The protection (throws while the storage key is missing).</param>
    public EvaluationFiles(IReadOnlyDictionary<string, string> directories, Func<EvalProtection> protection)
    {
        this.directories = directories;
        this.protection = protection;
    }

    /// <summary>Reads a file (decrypted when sealed).</summary>
    public EvaluationFileDto Read(EvaluationFileRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (this.Resolve(request.Tool, request.Name, write: false) is not { } path)
        {
            return new EvaluationFileDto { Error = $"'{request.Name}' is not a readable {request.Tool} file." };
        }

        if (!File.Exists(path))
        {
            return new EvaluationFileDto { Exists = false };
        }

        try
        {
            var sealedFile = EvalProtection.IsSealed(path);
            var content = SealedNames.Contains(request.Name) || sealedFile ? this.protection().ReadText(path) : File.ReadAllText(path);
            return new EvaluationFileDto { Exists = true, Sealed = sealedFile, Content = content };
        }
        catch (InvalidOperationException ex)
        {
            return new EvaluationFileDto { Exists = true, Error = ex.Message };
        }
    }

    /// <summary>Writes a file, sealed when it carries text on an encrypted install; identical content is not rewritten.</summary>
    public EvaluationFileWriteResultDto Write(EvaluationFileWriteDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (this.Resolve(request.Tool, request.Name, write: true) is not { } path)
        {
            return new EvaluationFileWriteResultDto { Error = $"'{request.Name}' is not a writable {request.Tool} file." };
        }

        var current = this.Read(new EvaluationFileRequestDto { Tool = request.Tool, Name = request.Name });
        if (current is { Exists: true, Error: null } && string.Equals(current.Content, request.Content, StringComparison.Ordinal))
        {
            return new EvaluationFileWriteResultDto { Written = false, Unchanged = true, Sealed = current.Sealed };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (SealedNames.Contains(request.Name))
        {
            var protection = this.protection();
            protection.WriteText(path, request.Content);
            return new EvaluationFileWriteResultDto { Written = true, Sealed = protection.IsEncrypted };
        }

        File.WriteAllText(path, request.Content);
        return new EvaluationFileWriteResultDto { Written = true };
    }

    private string? Resolve(string tool, string name, bool write) =>
        Names.TryGetValue(tool, out var names)
        && (write ? names.Writable : names.Readable).Contains(name, StringComparer.Ordinal)
        && this.directories.TryGetValue(tool, out var directory)
            ? Path.Combine(directory, name)
            : null;
}
```

`src/Cortex.Contained.Agent.Host/Evaluation/EvaluationJobService.cs` (new):

```csharp
using System.Text;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Evaluation;

/// <summary>What one evaluation command gets: its arguments, where to print, the protection and an optional package passphrase.</summary>
public sealed record EvaluationRun(IReadOnlyList<string> Args, TextWriter Output, EvalProtection Protection, string? Passphrase);

/// <summary>One evaluation command run inside the agent process; returns the exit code.</summary>
public delegate Task<int> EvaluationCommand(EvaluationRun run, CancellationToken cancellationToken);

/// <summary>
/// Runs the evaluation commands inside the agent process, where the storage key lives (the in-container CLI
/// processes never get it): one job at a time, in the background, with the output (ids, numbers and paths only)
/// kept for the Bridge to poll. On an encrypted install every job runs in encrypted-workspace mode
/// (<see cref="EvalProtection.Encrypted"/>). Options that would point a job at other files or endpoints than the
/// configured ones are refused.
/// </summary>
public sealed partial class EvaluationJobService : IDisposable
{
    /// <summary>Most characters of output kept per job.</summary>
    public const int MaxOutputChars = 64 * 1024;

    private const int KeptJobs = 20;

    private static readonly string[] RefusedOptions = ["--dir", "--source", "--labels", "--embedder", "--reranker"];

    private readonly IReadOnlyDictionary<string, EvaluationCommand> commands;
    private readonly Func<EvalProtection> protection;
    private readonly TimeProvider time;
    private readonly ILogger<EvaluationJobService> logger;
    private readonly Lock sync = new();
    private readonly List<Job> jobs = [];

    /// <param name="commands">Command per tool name.</param>
    /// <param name="protection">The protection of the next job; throws <see cref="StorageLockedException"/> while the key is missing.</param>
    public EvaluationJobService(IReadOnlyDictionary<string, EvaluationCommand> commands, Func<EvalProtection> protection, TimeProvider time, ILogger<EvaluationJobService> logger)
    {
        this.commands = commands;
        this.protection = protection;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>The protection for this agent: encrypted when encryption at rest is on.</summary>
    public static Func<EvalProtection> ProtectionFor(StorageEncryptionContext context, StorageKeyRing keys) =>
        () => context.Enabled ? EvalProtection.Encrypted(keys) : EvalProtection.Plaintext;

    /// <summary>Starts <paramref name="request"/> in the background, or returns a refused job.</summary>
    public EvaluationJobDto Start(EvaluationStartRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = this.time.GetUtcNow();
        var command = request.Args.Count > 0 ? request.Args[0] : null;
        if (!this.commands.TryGetValue(request.Tool, out var run))
        {
            return Refused(request.Tool, command, now, $"Unknown tool '{request.Tool}': use {string.Join(", ", this.commands.Keys.Order(StringComparer.Ordinal))}.");
        }

        if (request.Args.FirstOrDefault(a => RefusedOptions.Contains(a, StringComparer.Ordinal)) is { } option)
        {
            return Refused(request.Tool, command, now, $"{option} is not available for jobs: they use the configured workspace, stores and endpoints.");
        }

        EvalProtection jobProtection;
        try
        {
            jobProtection = this.protection();
        }
        catch (StorageLockedException)
        {
            return Refused(request.Tool, command, now, "The storage key has not arrived from the Bridge yet: the encrypted stores cannot be read.");
        }

        lock (this.sync)
        {
            if (this.jobs.Any(j => j.State == EvaluationJobStates.Running))
            {
                return Refused(request.Tool, command, now, "Another evaluation job is running; wait for it or cancel it.");
            }

            var job = new Job(Guid.NewGuid().ToString("N")[..12], request.Tool, command, jobProtection.IsEncrypted, now);
            this.jobs.Add(job);
            if (this.jobs.Count > KeptJobs)
            {
                this.jobs.RemoveAt(this.jobs.FindIndex(j => j.State != EvaluationJobStates.Running));
            }

            this.LogStarted(job.Id, request.Tool, command ?? string.Empty, job.Encrypted);
            job.Task = Task.Run(() => this.RunAsync(job, run, new EvaluationRun(request.Args, job.Output, jobProtection, request.Passphrase)), CancellationToken.None);
            return job.ToDto();
        }
    }

    /// <summary>The job, or null.</summary>
    public EvaluationJobDto? Get(string jobId)
    {
        lock (this.sync)
        {
            return this.jobs.FirstOrDefault(j => string.Equals(j.Id, jobId, StringComparison.Ordinal))?.ToDto();
        }
    }

    /// <summary>Recent jobs, newest first.</summary>
    public IReadOnlyList<EvaluationJobDto> List()
    {
        lock (this.sync)
        {
            return this.jobs.AsEnumerable().Reverse().Select(j => j.ToDto()).ToList();
        }
    }

    /// <summary>Requests cancellation of a running job.</summary>
    public bool Cancel(string jobId)
    {
        lock (this.sync)
        {
            var job = this.jobs.FirstOrDefault(j => string.Equals(j.Id, jobId, StringComparison.Ordinal));
            if (job is not { State: EvaluationJobStates.Running })
            {
                return false;
            }

            job.Cancellation.Cancel();
            return true;
        }
    }

    /// <summary>Waits for the running job, if any (tests and shutdown).</summary>
    public Task WhenIdleAsync()
    {
        lock (this.sync)
        {
            return Task.WhenAll(this.jobs.Select(j => j.Task));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (this.sync)
        {
            foreach (var job in this.jobs)
            {
                job.Cancellation.Cancel();
                job.Cancellation.Dispose();
            }
        }
    }

    private static EvaluationJobDto Refused(string tool, string? command, DateTimeOffset now, string reason) => new()
    {
        Id = string.Empty,
        Tool = tool,
        Command = command,
        State = EvaluationJobStates.Refused,
        StartedAt = now,
        FinishedAt = now,
        Output = reason,
    };

    private async Task RunAsync(Job job, EvaluationCommand run, EvaluationRun arguments)
    {
        string state;
        int? exitCode = null;
        try
        {
            exitCode = await run(arguments, job.Cancellation.Token).ConfigureAwait(false);
            state = exitCode == 0 ? EvaluationJobStates.Succeeded : EvaluationJobStates.Failed;
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            state = EvaluationJobStates.Cancelled;
        }
#pragma warning disable CA1031 // A failing command ends its job, never the agent; only the exception type is reported (messages may hold text).
        catch (Exception ex)
        {
            await job.Output.WriteLineAsync("error: " + ex.GetType().Name).ConfigureAwait(false);
            state = EvaluationJobStates.Failed;
        }
#pragma warning restore CA1031

        lock (this.sync)
        {
            job.State = state;
            job.ExitCode = exitCode;
            job.FinishedAt = this.time.GetUtcNow();
        }

        this.LogFinished(job.Id, state, exitCode ?? -1);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation job {JobId} started: {Tool} {Command} (encrypted workspace: {Encrypted})")]
    private partial void LogStarted(string jobId, string tool, string command, bool encrypted);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation job {JobId} finished: {State} (exit code {ExitCode})")]
    private partial void LogFinished(string jobId, string state, int exitCode);

    private sealed class Job(string id, string tool, string? command, bool encrypted, DateTimeOffset startedAt)
    {
        public string Id { get; } = id;

        public bool Encrypted { get; } = encrypted;

        public JobOutput Output { get; } = new();

        public CancellationTokenSource Cancellation { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;

        public string State { get; set; } = EvaluationJobStates.Running;

        public int? ExitCode { get; set; }

        public DateTimeOffset? FinishedAt { get; set; }

        public EvaluationJobDto ToDto() => new()
        {
            Id = this.Id,
            Tool = tool,
            Command = command,
            State = this.State,
            ExitCode = this.ExitCode,
            Encrypted = this.Encrypted,
            StartedAt = startedAt,
            FinishedAt = this.FinishedAt,
            Output = this.Output.Snapshot(),
        };
    }

    /// <summary>A thread-safe, capped text sink the Bridge can read while the job runs.</summary>
    private sealed class JobOutput : TextWriter
    {
        private readonly StringBuilder text = new();
        private readonly Lock gate = new();
        private bool truncated;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => this.Append(value.ToString());

        public override void Write(char[] buffer, int index, int count) => this.Append(new string(buffer, index, count));

        public override void Write(string? value) => this.Append(value);

        public string Snapshot()
        {
            lock (this.gate)
            {
                return this.truncated ? this.text + "\n[output truncated]" : this.text.ToString();
            }
        }

        private void Append(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            lock (this.gate)
            {
                var room = MaxOutputChars - this.text.Length;
                if (value.Length <= room)
                {
                    this.text.Append(value);
                    return;
                }

                this.text.Append(value.AsSpan(0, Math.Max(0, room)));
                this.truncated = true;
            }
        }
    }
}
```

`src/Cortex.Contained.Contracts/Hub/IEvaluationHub.cs` (new):

```csharp
namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Evaluation commands (<c>history-eval</c>, <c>memory-eval</c>, <c>eval-corpus</c>) run inside the agent process,
/// where the storage key lives, and the owner-authored workspace files. Part of the composed
/// <see cref="IAgentHub"/> surface; the Bridge evaluation endpoints use it.
/// </summary>
public interface IEvaluationHub
{
    /// <summary>Starts a command as a background job (one at a time); a refusal comes back as a job in state <c>refused</c>.</summary>
    Task<EvaluationJobDto> StartEvaluation(EvaluationStartRequestDto request);

    /// <summary>The job (state, exit code, output so far), or null when unknown.</summary>
    Task<EvaluationJobDto?> GetEvaluationJob(string jobId);

    /// <summary>Recent jobs, newest first.</summary>
    Task<IReadOnlyList<EvaluationJobDto>> ListEvaluationJobs();

    /// <summary>Requests cancellation of a running job; false when it is not running.</summary>
    Task<bool> CancelEvaluationJob(string jobId);

    /// <summary>Reads an owner-authored workspace file (decrypted when sealed).</summary>
    Task<EvaluationFileDto> ReadEvaluationFile(EvaluationFileRequestDto request);

    /// <summary>Writes an owner-authored workspace file (sealed when it carries text on an encrypted install).</summary>
    Task<EvaluationFileWriteResultDto> WriteEvaluationFile(EvaluationFileWriteDto request);
}

/// <summary>String values of <see cref="EvaluationJobDto.State"/>.</summary>
public static class EvaluationJobStates
{
    public const string Running = "running";

    public const string Succeeded = "succeeded";

    /// <summary>The command ran and returned a non-zero exit code (or threw).</summary>
    public const string Failed = "failed";

    public const string Cancelled = "cancelled";

    /// <summary>The command was not started (another job runs, storage locked, unknown tool, refused option).</summary>
    public const string Refused = "refused";
}

/// <summary>Body of <see cref="IEvaluationHub.StartEvaluation"/>.</summary>
public sealed record EvaluationStartRequestDto
{
    /// <summary><c>history-eval</c>, <c>memory-eval</c> or <c>eval-corpus</c>.</summary>
    public required string Tool { get; init; }

    /// <summary>The command and its options, as on the command line (e.g. <c>ablate --split dev</c>).</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Package passphrase for <c>eval-corpus export|import</c>; held in memory for the job only, never echoed or logged.</summary>
    public string? Passphrase { get; init; }
}

/// <summary>One evaluation job.</summary>
public sealed record EvaluationJobDto
{
    public required string Id { get; init; }

    public required string Tool { get; init; }

    /// <summary>The command (first argument) only; options are not echoed.</summary>
    public string? Command { get; init; }

    /// <summary>One of <see cref="EvaluationJobStates"/>.</summary>
    public required string State { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>True when the job ran in encrypted-workspace mode.</summary>
    public bool Encrypted { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>What the command printed (ids, numbers and paths only; capped).</summary>
    public string Output { get; init; } = string.Empty;
}

/// <summary>Body of <see cref="IEvaluationHub.ReadEvaluationFile"/>.</summary>
public sealed record EvaluationFileRequestDto
{
    /// <summary><c>history-eval</c>, <c>memory-eval</c> or <c>eval-corpus</c>.</summary>
    public required string Tool { get; init; }

    /// <summary>A workspace file name from the allow-list (e.g. <c>dataset.jsonl</c>).</summary>
    public required string Name { get; init; }
}

/// <summary>A workspace file's content.</summary>
public sealed record EvaluationFileDto
{
    public bool Exists { get; init; }

    /// <summary>True when the file is sealed with the storage key on disk.</summary>
    public bool Sealed { get; init; }

    public string Content { get; init; } = string.Empty;

    public string? Error { get; init; }
}

/// <summary>Body of <see cref="IEvaluationHub.WriteEvaluationFile"/>.</summary>
public sealed record EvaluationFileWriteDto
{
    public required string Tool { get; init; }

    public required string Name { get; init; }

    public required string Content { get; init; }
}

/// <summary>Outcome of <see cref="IEvaluationHub.WriteEvaluationFile"/>.</summary>
public sealed record EvaluationFileWriteResultDto
{
    public bool Written { get; init; }

    /// <summary>True when the content equals what is stored (nothing rewritten, so registration hashes stay valid).</summary>
    public bool Unchanged { get; init; }

    public bool Sealed { get; init; }

    public string? Error { get; init; }
}
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~EvaluationJobServiceTests|FullyQualifiedName~EvaluationFilesTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 12 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Evaluation\EvaluationFiles.cs src\Cortex.Contained.Agent.Host\Evaluation\EvaluationJobService.cs src\Cortex.Contained.Contracts\Hub\IEvaluationHub.cs tests\Cortex.Contained.Agent.Host.Tests\Evaluation\EvaluationFilesTests.cs tests\Cortex.Contained.Agent.Host.Tests\Evaluation\EvaluationJobServiceTests.cs
git commit -m "feat(eval): in-process evaluation jobs and sealed workspace files" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 8: Hub methods, DI, in-agent entry points, the in-container guard

`AgentHub` implements `IEvaluationHub`; `EvaluationRegistration` wires the jobs with the live configuration: `HistoryEvalCli.RunInAgentAsync` (configured embedder, live `messages.db` appended as `--source`) and `MemoryEvalCli.RunInAgentAsync`, the reranker with the Bridge-granted key (`RerankEvalSetup.LiveReranker(options, () => access.ApiKey)`). `EvaluationGuard` now takes the whole argument list: on an encrypted install `docker exec` refuses only the commands that copy stores, pointing to `Invoke-CortexEval.ps1`, and lets `kappa` and the synthetic `history-smoke` checks run.

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs`
- Create: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Evaluation.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Reranking/Evaluation/RerankEvaluation.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Storage/EvaluationGuard.cs`
- Modify: `src/Cortex.Contained.Contracts/Hub/IAgentHub.cs`
- Test (modify): `tests/Cortex.Contained.Agent.Host.Tests/Storage/PlaintextResidueTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Storage/PlaintextResidueTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Agent.Host.Tests/Storage/PlaintextResidueTests.cs
+++ b/tests/Cortex.Contained.Agent.Host.Tests/Storage/PlaintextResidueTests.cs
@@ -33,14 +33,27 @@ public sealed class PlaintextResidueTests : IDisposable
     [Fact]
     public async Task EvaluationCommands_RefuseOnlyWhileEncryptionIsOn()
     {
         using var output = new StringWriter();
-        Assert.False(await EvaluationGuard.RefuseWhenEncryptedAsync(this.Configuration(), "history-eval", output));
+        Assert.False(await EvaluationGuard.RefuseWhenEncryptedAsync(this.Configuration(), ["history-eval", "ablate"], output));
 
         new StorageEncryptionMarker { KeyFingerprint = "fp" }.Write(Path.Combine(this.root, StorageEncryptionMarker.FileName));
 
-        Assert.True(await EvaluationGuard.RefuseWhenEncryptedAsync(this.Configuration(), "history-eval", output));
-        Assert.Contains("history-eval is unavailable while encryption at rest is on", output.ToString(), StringComparison.Ordinal);
+        Assert.True(await EvaluationGuard.RefuseWhenEncryptedAsync(this.Configuration(), ["history-eval", "ablate", "--split", "dev"], output));
+        Assert.Contains("Invoke-CortexEval.ps1 -Tool history-eval -- ablate --split dev", output.ToString(), StringComparison.Ordinal);
+    }
+
+    [Theory]
+    [InlineData("history-eval", "kappa")]
+    [InlineData("memory-eval", "kappa")]
+    [InlineData("history-smoke", "embedder-check")]
+    [InlineData("history-smoke", "reranker-check")]
+    public async Task EvaluationCommands_StoreFreeCommandsRunWhileEncrypted(string tool, string command)
+    {
+        new StorageEncryptionMarker { KeyFingerprint = "fp" }.Write(Path.Combine(this.root, StorageEncryptionMarker.FileName));
+        using var output = new StringWriter();
+
+        Assert.False(await EvaluationGuard.RefuseWhenEncryptedAsync(this.Configuration(), [tool, command], output));
     }
 
     /// <summary>
     /// subagents.db, todos.db and tasks.db rely on the SQLite3 Multiple Ciphers build compiling
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~PlaintextResidueTests|FullyQualifiedName~HistoryEvalCliTests|FullyQualifiedName~MemoryEvalCliTests" -v q`
Expected: build FAILS — `'EvaluationGuard.RefuseWhenEncryptedAsync(IConfiguration, string, TextWriter)': cannot convert from 'string[]' to 'string'`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Evaluation;

/// <summary>DI registration of the in-process evaluation jobs and workspace files (called from <c>Program.cs</c>).</summary>
public static class EvaluationRegistration
{
    /// <summary>Registers <see cref="EvaluationJobService"/> and <see cref="EvaluationFiles"/>.</summary>
    public static void AddEvaluation(IServiceCollection services, StorageEncryptionContext encryption)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(encryption);
        services.AddSingleton(sp =>
        {
            var history = sp.GetRequiredService<IOptionsMonitor<HistoryOptions>>();
            var memory = sp.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>();
            var access = sp.GetRequiredService<RerankerAccess>();
            Func<RerankerOptions, IReranker> reranker = options => RerankEvalSetup.LiveReranker(options, () => access.ApiKey);
            var commands = new Dictionary<string, EvaluationCommand>(StringComparer.Ordinal)
            {
                ["history-eval"] = (run, ct) => HistoryEvalCli.RunInAgentAsync(
                    run.Args, run.Output, run.Protection, history.CurrentValue, memory.CurrentValue, encryption.Layout.MessagesDatabase, reranker, ct),
                ["memory-eval"] = (run, ct) => MemoryEvalCli.RunInAgentAsync(run.Args, run.Output, run.Protection, memory.CurrentValue, reranker, ct),
            };
            return new EvaluationJobService(
                commands,
                EvaluationJobService.ProtectionFor(encryption, sp.GetRequiredService<StorageKeyRing>()),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<EvaluationJobService>>());
        });
        services.AddSingleton(sp => new EvaluationFiles(
            Workspaces(sp.GetRequiredService<IOptionsMonitor<HistoryOptions>>().CurrentValue, encryption.Layout),
            EvaluationJobService.ProtectionFor(encryption, sp.GetRequiredService<StorageKeyRing>())));
    }

    /// <summary>Workspace directory per tool.</summary>
    public static IReadOnlyDictionary<string, string> Workspaces(HistoryOptions history, StorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(layout);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["history-eval"] = history.EvalArtifactsDirectory,
            ["memory-eval"] = MemoryEvalWorkspace.DefaultDirectory,
            ["eval-corpus"] = Path.Combine(layout.EvaluationDirectory, "corpus"),
        };
    }
}
```

`src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs
+++ b/src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs
@@ -62,8 +62,39 @@ public static class HistoryEvalCli
         ArgumentNullException.ThrowIfNull(args);
         return RunAsync(args, output, history => LiveEmbedder(Option(args, "--embedder"), history), cancellationToken);
     }
 
+    /// <summary>
+    /// Entry point inside the agent process (an evaluation job): the configured embedder and history options, the
+    /// live <paramref name="messagesDb"/>, the Bridge-granted reranker and <paramref name="protection"/> (encrypted
+    /// copies and sealed dataset on an encrypted install). The job service has already refused path options.
+    /// </summary>
+    public static Task<int> RunInAgentAsync(
+        IReadOnlyList<string> args,
+        TextWriter output,
+        EvalProtection protection,
+        HistoryOptions configured,
+        MemoryMcpOptions memory,
+        string messagesDb,
+        Func<RerankerOptions, IReranker> rerankerFactory,
+        CancellationToken cancellationToken)
+    {
+        ArgumentNullException.ThrowIfNull(args);
+        ArgumentNullException.ThrowIfNull(memory);
+        return RunAsync(
+            [.. args, "--source", messagesDb],
+            output,
+            history =>
+            {
+                var memoryMonitor = new StaticOptionsMonitor<MemoryMcpOptions>(memory);
+                return new OllamaHistoryEmbedder(new OllamaEmbeddingService(memoryMonitor, NullLogger<OllamaEmbeddingService>.Instance), memoryMonitor, history);
+            },
+            cancellationToken,
+            configured,
+            rerankerFactory,
+            protection);
+    }
+
     /// <summary>Entry point with an injected embedder (tests use a deterministic fake).</summary>
     internal static async Task<int> RunAsync(
         IReadOnlyList<string> args,
         TextWriter output,
```

`src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Evaluation.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary><see cref="IEvaluationHub"/>: evaluation jobs and workspace files for the Bridge evaluation endpoints.</summary>
public sealed partial class AgentHub
{
    /// <inheritdoc />
    public Task<EvaluationJobDto> StartEvaluation(EvaluationStartRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(this.EvaluationJobs.Start(request));
    }

    /// <inheritdoc />
    public Task<EvaluationJobDto?> GetEvaluationJob(string jobId) => Task.FromResult(this.EvaluationJobs.Get(jobId));

    /// <inheritdoc />
    public Task<IReadOnlyList<EvaluationJobDto>> ListEvaluationJobs() => Task.FromResult(this.EvaluationJobs.List());

    /// <inheritdoc />
    public Task<bool> CancelEvaluationJob(string jobId) => Task.FromResult(this.EvaluationJobs.Cancel(jobId));

    /// <inheritdoc />
    public Task<EvaluationFileDto> ReadEvaluationFile(EvaluationFileRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return Task.FromResult(this.EvaluationFiles.Read(request));
        }
        catch (StorageLockedException)
        {
            throw new HubException("The storage key has not arrived from the Bridge yet.");
        }
    }

    /// <inheritdoc />
    public Task<EvaluationFileWriteResultDto> WriteEvaluationFile(EvaluationFileWriteDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return Task.FromResult(this.EvaluationFiles.Write(request));
        }
        catch (StorageLockedException)
        {
            throw new HubException("The storage key has not arrived from the Bridge yet.");
        }
    }

    private Evaluation.EvaluationJobService EvaluationJobs =>
        this.evaluationJobs ?? throw new HubException("Evaluation jobs are not available on this agent.");

    private Evaluation.EvaluationFiles EvaluationFiles =>
        this.evaluationFiles ?? throw new HubException("Evaluation files are not available on this agent.");
}
```

`src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs
+++ b/src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs
@@ -46,8 +46,10 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
     private readonly Storage.StorageKeyRing? storageKeys;
     private readonly Cortex.Contained.Agent.Host.Reranking.RerankerAccess? rerankerAccess;
     private readonly Cortex.Contained.Agent.Host.Recall.RecallControlService? recall;
     private readonly Storage.StorageEncryptionService? storageEncryption;
+    private readonly Cortex.Contained.Agent.Host.Evaluation.EvaluationJobService? evaluationJobs;
+    private readonly Cortex.Contained.Agent.Host.Evaluation.EvaluationFiles? evaluationFiles;
     private readonly Cortex.Contained.Agent.Host.Mcp.McpToolStore mcpToolStore;
     private readonly IHttpClientFactory httpClientFactory;
     private readonly AgentMetrics metrics;
     private readonly SubagentExecutionCoordinator subagentCoordinator;
@@ -84,9 +86,11 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
         Cortex.Contained.Agent.Host.Events.EventBackfillService? eventBackfill = null,
         Storage.StorageKeyRing? storageKeys = null,
         Cortex.Contained.Agent.Host.Reranking.RerankerAccess? rerankerAccess = null,
         Cortex.Contained.Agent.Host.Recall.RecallControlService? recall = null,
-        Storage.StorageEncryptionService? storageEncryption = null)
+        Storage.StorageEncryptionService? storageEncryption = null,
+        Cortex.Contained.Agent.Host.Evaluation.EvaluationJobService? evaluationJobs = null,
+        Cortex.Contained.Agent.Host.Evaluation.EvaluationFiles? evaluationFiles = null)
     {
         this.runtime = runtime;
         this.llmClient = llmClient;
         this.bridgeClientAccessor = bridgeClientAccessor;
@@ -111,8 +115,10 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
         this.storageKeys = storageKeys;
         this.rerankerAccess = rerankerAccess;
         this.recall = recall;
         this.storageEncryption = storageEncryption;
+        this.evaluationJobs = evaluationJobs;
+        this.evaluationFiles = evaluationFiles;
         this.mcpToolStore = mcpToolStore;
         this.httpClientFactory = httpClientFactory;
         this.metrics = metrics;
         this.subagentCoordinator = subagentCoordinator;
```

`src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs
@@ -198,8 +198,28 @@ public static class MemoryEvalCli
             return 130;
         }
     }
 
+    /// <summary>
+    /// Entry point inside the agent process (an evaluation job): the agent's memory options, the Bridge-granted
+    /// reranker and <paramref name="protection"/> (encrypted copies, sealed dataset and seeds on an encrypted install).
+    /// </summary>
+    public static Task<int> RunInAgentAsync(
+        IReadOnlyList<string> args,
+        TextWriter output,
+        EvalProtection protection,
+        MemoryMcpOptions live,
+        Func<RerankerOptions, IReranker> rerankerFactory,
+        CancellationToken cancellationToken) =>
+        RunAsync(
+            args,
+            output,
+            live,
+            options => new OllamaEmbeddingService(new StaticOptionsMonitor(options), NullLogger<OllamaEmbeddingService>.Instance),
+            cancellationToken,
+            rerankerFactory,
+            protection);
+
     /// <summary>
     /// The agent's <c>MemoryMcp</c> configuration, loaded like <c>Program.cs</c> (appsettings, environment,
     /// then <c>cortex.yml</c>), with the CLI overrides.
     /// </summary>
```

`src/Cortex.Contained.Agent.Host/Program.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Program.cs
+++ b/src/Cortex.Contained.Agent.Host/Program.cs
@@ -18,11 +18,12 @@ using MemoryMcp.Core.Storage;
 using Microsoft.Extensions.Options;
 using OllamaSharp;
 using Serilog;
 
-// The evaluation CLIs copy the raw stores into run directories, so they refuse to run while encryption at rest is on.
+// The in-container evaluation CLIs never hold the storage key: on an encrypted install the commands that copy
+// stores run as evaluation jobs inside the agent process instead (scripts/Invoke-CortexEval.ps1).
 if (args.Length > 0 && args[0] is "history-smoke" or "history-eval" or "memory-eval"
-    && await EvaluationGuard.RefuseWhenEncryptedAsync(EvaluationGuard.CliConfiguration(), args[0], Console.Out).ConfigureAwait(false))
+    && await EvaluationGuard.RefuseWhenEncryptedAsync(EvaluationGuard.CliConfiguration(), args, Console.Out).ConfigureAwait(false))
 {
     Environment.ExitCode = EvaluationGuard.RefusedExitCode;
     return;
 }
@@ -294,8 +295,11 @@ builder.Services.AddSingleton(sp => new StorageEncryptionService(
     storageEncryption.Enabled ? sp.GetRequiredService<EncryptedMemoryStore>() : null,
     // Inside the container Docker restarts the agent (restart: unless-stopped) after it stops itself.
     selfRestart: string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)));
 
+// --- Evaluation jobs (history-eval, memory-eval) inside the agent process, where the storage key lives ---
+Cortex.Contained.Agent.Host.Evaluation.EvaluationRegistration.AddEvaluation(builder.Services, storageEncryption);
+
 // --- Memory Settings Store (runtime-mutable overrides for memory options) ---
 builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Memory.MemorySettingsStore>();
 builder.Services.AddSingleton<IPostConfigureOptions<MemoryMcpOptions>,
     Cortex.Contained.Agent.Host.Memory.MemoryMcpPostConfigure>();
```

`src/Cortex.Contained.Agent.Host/Reranking/Evaluation/RerankEvaluation.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Reranking/Evaluation/RerankEvaluation.cs
+++ b/src/Cortex.Contained.Agent.Host/Reranking/Evaluation/RerankEvaluation.cs
@@ -268,8 +268,12 @@ public static class RerankEvalSetup
     /// <summary>The llama-server client over <paramref name="client"/> with the key from <c>Reranker__ApiKey</c>.</summary>
     public static LlamaServerReranker LiveReranker(RerankerOptions options, HttpClient client) =>
         new(new SingleClientFactory(client), new FixedOptions(options), () => Environment.GetEnvironmentVariable("Reranker__ApiKey"));
 
+    /// <summary>The live llama-server client with the key the Bridge granted (evaluation jobs inside the agent process).</summary>
+    public static LlamaServerReranker LiveReranker(RerankerOptions options, Func<string?> apiKey) =>
+        new(new SingleClientFactory(SharedClient), new FixedOptions(options), apiKey);
+
     /// <summary>A rerank stage over <paramref name="reranker"/> for evaluation runs.</summary>
     public static RerankStage Stage(IReranker reranker, RerankerOptions options)
     {
         var access = new RerankerAccess();
```

`src/Cortex.Contained.Agent.Host/Storage/EvaluationGuard.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Storage/EvaluationGuard.cs
+++ b/src/Cortex.Contained.Agent.Host/Storage/EvaluationGuard.cs
@@ -4,22 +4,36 @@ using Microsoft.Extensions.Configuration;
 
 namespace Cortex.Contained.Agent.Host.Storage;
 
 /// <summary>
-/// Keeps the evaluation commands (<c>history-eval</c>, <c>history-smoke</c>, <c>memory-eval</c>) from making
-/// plaintext copies of encrypted stores: their run directories hold raw copies of <c>messages.db</c> and the
-/// memory store, so they refuse to run while encryption at rest is on.
+/// Keeps the in-container evaluation CLIs (<c>docker exec … history-eval|history-smoke|memory-eval</c>) from
+/// copying encrypted stores: such a process never holds the storage key, so on an encrypted install it could
+/// only fail or write plaintext. Those commands run as evaluation jobs inside the agent process instead
+/// (encrypted-workspace mode, <c>scripts/Invoke-CortexEval.ps1</c> through the Bridge). Commands that read
+/// no store stay available: <c>kappa</c> (ids and grades) and the synthetic-text checks of <c>history-smoke</c>.
 /// </summary>
 public static class EvaluationGuard
 {
     /// <summary>Process exit code of a refused command.</summary>
     public const int RefusedExitCode = 3;
 
-    /// <summary>True (and an explanation on <paramref name="output"/>) when <paramref name="command"/> must not run.</summary>
-    public static async Task<bool> RefuseWhenEncryptedAsync(IConfiguration configuration, string command, TextWriter output)
+    private static readonly HashSet<string> StoreFreeCommands = new(StringComparer.Ordinal)
+    {
+        "history-eval kappa", "memory-eval kappa", "history-smoke embedder-check", "history-smoke reranker-check",
+    };
+
+    /// <summary>True (and an explanation on <paramref name="output"/>) when <paramref name="args"/> (tool, command, …) must not run here.</summary>
+    public static async Task<bool> RefuseWhenEncryptedAsync(IConfiguration configuration, IReadOnlyList<string> args, TextWriter output)
     {
         ArgumentNullException.ThrowIfNull(configuration);
+        ArgumentNullException.ThrowIfNull(args);
         ArgumentNullException.ThrowIfNull(output);
+        var tool = args.Count > 0 ? args[0] : string.Empty;
+        if (args.Count > 1 && StoreFreeCommands.Contains(tool + " " + args[1]))
+        {
+            return false;
+        }
+
         var stateRoot = configuration["CORTEX_STATE_PATH"] ?? "/app/state";
         var dataRoot = configuration["CORTEX_DATA_PATH"] ?? "/app/data";
         var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
         var memory = configuration.GetSection(MemoryMcpOptions.SectionName).Get<MemoryMcpOptions>() ?? new MemoryMcpOptions();
@@ -27,10 +41,12 @@ public static class EvaluationGuard
         {
             return false;
         }
 
-        await output.WriteLineAsync(
-            $"{command} is unavailable while encryption at rest is on: its run directories would hold plaintext copies of the encrypted history and memories.")
+        await output.WriteLineAsync(tool == "history-smoke"
+            ? "history-smoke is unavailable while encryption at rest is on (only embedder-check and reranker-check run): use history-eval, which runs encrypted inside the agent."
+            : $"{tool} cannot run in a separate process while encryption at rest is on: it would need plaintext copies of the encrypted stores. " +
+              $"Run it inside the agent instead (encrypted-workspace mode): scripts/Invoke-CortexEval.ps1 -Tool {tool} -- {string.Join(' ', args.Skip(1))}")
             .ConfigureAwait(false);
         return true;
     }
```

`src/Cortex.Contained.Contracts/Hub/IAgentHub.cs` — apply:

```diff
--- a/src/Cortex.Contained.Contracts/Hub/IAgentHub.cs
+++ b/src/Cortex.Contained.Contracts/Hub/IAgentHub.cs
@@ -8,11 +8,11 @@ namespace Cortex.Contained.Contracts.Hub;
 /// This is a composed interface: the method surface is partitioned into feature
 /// interfaces (<see cref="IChatHub"/>, <see cref="IMemoryHub"/>,
 /// <see cref="IHistoryHub"/>, <see cref="IVoiceIdHub"/>, <see cref="ICodingHub"/>,
 /// <see cref="IMcpHub"/>, <see cref="ISystemHub"/>, <see cref="ISubagentHub"/>, <see cref="IRecallHub"/>,
-/// <see cref="IStorageHub"/>) for
+/// <see cref="IStorageHub"/>, <see cref="IEvaluationHub"/>) for
 /// readability and to let consumers depend on the narrow slice they actually use. All
 /// methods still share a single SignalR hub connection and route by method name, so the
 /// wire protocol is unchanged.
 /// </remarks>
-public interface IAgentHub : IChatHub, IMemoryHub, IHistoryHub, IVoiceIdHub, ICodingHub, IMcpHub, ISystemHub, ISubagentHub, IRecallHub, IStorageHub{
+public interface IAgentHub : IChatHub, IMemoryHub, IHistoryHub, IVoiceIdHub, ICodingHub, IMcpHub, ISystemHub, ISubagentHub, IRecallHub, IStorageHub, IEvaluationHub{
 }
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~PlaintextResidueTests|FullyQualifiedName~HistoryEvalCliTests|FullyQualifiedName~MemoryEvalCliTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 23 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Evaluation\EvaluationRegistration.cs src\Cortex.Contained.Agent.Host\History\Evaluation\HistoryEvalCli.cs src\Cortex.Contained.Agent.Host\Hubs\AgentHub.Evaluation.cs src\Cortex.Contained.Agent.Host\Hubs\AgentHub.cs src\Cortex.Contained.Agent.Host\Memory\Evaluation\MemoryEvalCli.cs src\Cortex.Contained.Agent.Host\Program.cs src\Cortex.Contained.Agent.Host\Reranking\Evaluation\RerankEvaluation.cs src\Cortex.Contained.Agent.Host\Storage\EvaluationGuard.cs src\Cortex.Contained.Contracts\Hub\IAgentHub.cs tests\Cortex.Contained.Agent.Host.Tests\Storage\PlaintextResidueTests.cs
git commit -m "feat(eval): evaluation hub, DI and the in-container guard" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 9: Bridge evaluation endpoints and owner scripts

`/api/tenants/{tenantId}/eval/jobs` (start: password again, `reauth` rate limit), job polling and cancel, and `/eval/files/read|write` (password again, rate limited). `scripts/Invoke-CortexEval.ps1` starts a job and prints its output; `scripts/Edit-CortexEvalFile.ps1` edits a sealed file through the Bridge (private temp folder, deleted afterwards).

**Files:**
- Create: `scripts/Edit-CortexEvalFile.ps1`
- Create: `scripts/Invoke-CortexEval.ps1`
- Create: `src/Cortex.Contained.Bridge/Evaluation/EvaluationEndpoints.cs`
- Create: `src/Cortex.Contained.Bridge/Evaluation/IEvaluationAgentClient.cs`
- Create: `src/Cortex.Contained.Bridge/Hub/HubClient.Evaluation.cs`
- Modify: `src/Cortex.Contained.Bridge/Program.cs`
- Test (new): `tests/Cortex.Contained.Bridge.Tests/Evaluation/EvaluationEndpointsTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Bridge.Tests/Evaluation/EvaluationEndpointsTests.cs` (new):

```csharp
using System.Text.Json;
using Cortex.Contained.Bridge.Evaluation;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Cortex.Contained.Bridge.Tests.Evaluation;

/// <summary>The evaluation API: re-authentication for anything that starts a job or carries workspace text.</summary>
public sealed class EvaluationEndpointsTests
{
    private const string Password = "owner-password";

    private readonly IEvaluationAgentClient agent = Substitute.For<IEvaluationAgentClient>();

    public EvaluationEndpointsTests()
    {
        this.agent.StartAsync(Arg.Any<EvaluationStartRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationJobDto { Id = "j1", Tool = "history-eval", Command = "ablate", State = EvaluationJobStates.Running });
    }

    private static bool Verify(string? password) => password == Password;

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static List<RouteEndpoint> Endpoints()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        app.MapEvaluationEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    [Fact]
    public void AllRoutes_RequireAuthorization_AndTheContentRoutesAreRateLimited()
    {
        var endpoints = Endpoints();

        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<IAuthorizeData>()));
        Assert.All(
            endpoints.Where(e => e.RoutePattern.RawText is "/api/tenants/{tenantId}/eval/files/read" or "/api/tenants/{tenantId}/eval/files/write"
                || (e.RoutePattern.RawText == "/api/tenants/{tenantId}/eval/jobs" && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"))),
            e => Assert.Equal("reauth", e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName));
    }

    [Fact]
    public async Task Start_WrongPassword_ForbiddenAndNothingStarted()
    {
        var result = await EvaluationEndpoints.StartAsync(this.agent, Verify, new EvaluationJobRequest("wrong", "history-eval", ["ablate"]), CancellationToken.None);

        Assert.Equal(403, StatusOf(result));
        await this.agent.DidNotReceive().StartAsync(Arg.Any<EvaluationStartRequestDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_Accepted_RelaysToolArgsAndPassphrase()
    {
        var result = await EvaluationEndpoints.StartAsync(
            this.agent, Verify, new EvaluationJobRequest(Password, "eval-corpus", ["export", "--version", "v1"], "correct horse battery staple"), CancellationToken.None);

        Assert.Equal(202, StatusOf(result));
        await this.agent.Received(1).StartAsync(
            Arg.Is<EvaluationStartRequestDto>(r => r.Tool == "eval-corpus" && string.Join(' ', r.Args) == "export --version v1" && r.Passphrase == "correct horse battery staple"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_RefusedByTheAgent_Conflict()
    {
        this.agent.StartAsync(Arg.Any<EvaluationStartRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationJobDto { Id = string.Empty, Tool = "history-eval", State = EvaluationJobStates.Refused, Output = "Another evaluation job is running" });

        Assert.Equal(409, StatusOf(await EvaluationEndpoints.StartAsync(this.agent, Verify, new EvaluationJobRequest(Password, "history-eval", ["ablate"]), CancellationToken.None)));
    }

    [Fact]
    public async Task WriteFile_WrongPasswordOrTooLarge_RefusedBeforeTheAgent()
    {
        Assert.Equal(403, StatusOf(await EvaluationEndpoints.WriteFileAsync(this.agent, Verify, new EvaluationFileWriteRequest("wrong", "history-eval", "dataset.jsonl", "x"), CancellationToken.None)));
        Assert.Equal(400, StatusOf(await EvaluationEndpoints.WriteFileAsync(
            this.agent, Verify, new EvaluationFileWriteRequest(Password, "history-eval", "dataset.jsonl", new string('x', EvaluationEndpoints.MaxFileChars + 1)), CancellationToken.None)));

        await this.agent.DidNotReceive().WriteFileAsync(Arg.Any<EvaluationFileWriteDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadFile_AgentNotConnected_ServiceUnavailable()
    {
        var result = await EvaluationEndpoints.ReadFileAsync(null, Verify, new EvaluationFileReadRequest(Password, "history-eval", "dataset.jsonl"), CancellationToken.None);

        Assert.Equal(503, StatusOf(result));
        Assert.Contains("not connected", JsonSerializer.Serialize(result.GetType().GetProperty("Value")!.GetValue(result)), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~EvaluationEndpointsTests" -v q`
Expected: build FAILS — `The type or namespace name 'Evaluation' does not exist in the namespace 'Cortex.Contained.Bridge'`.

- [ ] **Step 3: Implement**

`scripts/Edit-CortexEvalFile.ps1` (new):

```powershell
<#
.SYNOPSIS
    Reads or writes an owner-authored evaluation file (dataset, analysis plan, sanitiser dictionary, ...).

.DESCRIPTION
    On an encrypted install the files that carry personal text (dataset.jsonl, seeds.jsonl, dictionary.jsonl)
    are sealed with the storage key inside the agent, so they are edited through the Bridge (the password is
    checked again for every read and write). Without -Pull/-Push the file is downloaded to a private folder
    under %LOCALAPPDATA%\Cortex\eval-edit, opened in the editor, uploaded when the editor closes and the local
    copy deleted. -Pull and -Push move the file to and from an explicit path (for labelling with an assistant);
    delete that copy yourself when done. Identical content is not rewritten, so pre-registration hashes stay valid.

.EXAMPLE
    .\scripts\Edit-CortexEvalFile.ps1 -Tool history-eval -Name dataset.jsonl
    .\scripts\Edit-CortexEvalFile.ps1 -Tool memory-eval -Name seeds.jsonl -Pull -Path .\seeds.jsonl
    .\scripts\Edit-CortexEvalFile.ps1 -Tool eval-corpus -Name dictionary.jsonl -Push -Path .\dictionary.jsonl
#>
[CmdletBinding(DefaultParameterSetName = 'Edit')]
param(
    [Parameter(Mandatory)]
    [ValidateSet('history-eval', 'memory-eval', 'eval-corpus')]
    [string]$Tool,

    [Parameter(Mandatory)]
    [string]$Name,

    [Parameter(ParameterSetName = 'Pull', Mandatory)]
    [switch]$Pull,

    [Parameter(ParameterSetName = 'Push', Mandatory)]
    [switch]$Push,

    [Parameter(ParameterSetName = 'Pull', Mandatory)]
    [Parameter(ParameterSetName = 'Push', Mandatory)]
    [string]$Path,

    [string]$TenantId = 'default',
    [string]$BridgeUrl = 'http://localhost:5080',
    [securestring]$Password,
    [string]$Editor = $(if ($env:EDITOR) { $env:EDITOR } else { 'notepad.exe' })
)

$ErrorActionPreference = 'Stop'

if (-not $Password) {
    $Password = Read-Host -AsSecureString -Prompt 'Bridge password'
}

$plain = [System.Net.NetworkCredential]::new('', $Password).Password
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/auth/login" -WebSession $session `
    -ContentType 'application/json' -Body (@{ password = $plain } | ConvertTo-Json) | Out-Null
$tenant = [uri]::EscapeDataString($TenantId)

function Read-EvalFile {
    $file = Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/tenants/$tenant/eval/files/read" -WebSession $session `
        -ContentType 'application/json' -Body (@{ password = $plain; tool = $Tool; name = $Name } | ConvertTo-Json)
    if ($file.exists) { [string]$file.content } else { '' }
}

function Write-EvalFile([string]$content) {
    $result = Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/tenants/$tenant/eval/files/write" -WebSession $session `
        -ContentType 'application/json' -Body (@{ password = $plain; tool = $Tool; name = $Name; content = $content } | ConvertTo-Json)
    if ($result.unchanged) { Write-Host "$Name unchanged." } else { Write-Host "$Name written (sealed: $($result.sealed))." }
}

try {
    switch ($PSCmdlet.ParameterSetName) {
        'Pull' {
            [IO.File]::WriteAllText($Path, (Read-EvalFile))
            Write-Host "Wrote $Path. It holds plaintext: delete it when you are done."
        }
        'Push' {
            Write-EvalFile ([IO.File]::ReadAllText($Path))
        }
        default {
            $folder = Join-Path $env:LOCALAPPDATA 'Cortex\eval-edit'
            New-Item -ItemType Directory -Force -Path $folder | Out-Null
            $local = Join-Path $folder ([guid]::NewGuid().ToString('N') + '-' + $Name)
            try {
                [IO.File]::WriteAllText($local, (Read-EvalFile))
                Start-Process -FilePath $Editor -ArgumentList "`"$local`"" -Wait
                Write-EvalFile ([IO.File]::ReadAllText($local))
            }
            finally {
                Remove-Item -LiteralPath $local -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
finally {
    $plain = $null
}
```

`scripts/Invoke-CortexEval.ps1` (new):

```powershell
<#
.SYNOPSIS
    Runs an evaluation command (history-eval, memory-eval, eval-corpus) inside the agent process.

.DESCRIPTION
    Only the agent process holds the storage key, so on an encrypted install the evaluation commands run
    there as jobs (encrypted-workspace mode): corpus copies are encrypted with the evaluation key, datasets
    and seeds stay sealed, reports hold ids and numbers only. The same path works on an unencrypted install.
    The script logs in to the Bridge, starts the job (the Bridge checks the password again), polls the job
    and prints its output. --dir, --source, --labels, --embedder and --reranker are refused for jobs: they
    always use the configured workspace, stores and endpoints.

.EXAMPLE
    .\scripts\Invoke-CortexEval.ps1 -Tool history-eval -- ablate --split dev
    .\scripts\Invoke-CortexEval.ps1 -Tool memory-eval -- holdout
    .\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -PromptPassphrase -- export --version v1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('history-eval', 'memory-eval', 'eval-corpus')]
    [string]$Tool,

    [Parameter(ValueFromRemainingArguments)]
    [string[]]$Arguments,

    [string]$TenantId = 'default',
    [string]$BridgeUrl = 'http://localhost:5080',
    [securestring]$Password,

    # Asks for the package passphrase (eval-corpus export and import); it is sent once, held by the job only.
    [switch]$PromptPassphrase,

    [int]$PollSeconds = 5
)

$ErrorActionPreference = 'Stop'

if (-not $Arguments -or $Arguments.Count -eq 0) {
    throw 'Pass the command after the tool, e.g. -Tool history-eval -- ablate --split dev'
}

if (-not $Password) {
    $Password = Read-Host -AsSecureString -Prompt 'Bridge password'
}

$plain = [System.Net.NetworkCredential]::new('', $Password).Password
$passphrase = $null
if ($PromptPassphrase) {
    $secure = Read-Host -AsSecureString -Prompt 'Package passphrase (at least 12 characters)'
    $passphrase = [System.Net.NetworkCredential]::new('', $secure).Password
}

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/auth/login" -WebSession $session `
    -ContentType 'application/json' -Body (@{ password = $plain } | ConvertTo-Json) | Out-Null

$tenant = [uri]::EscapeDataString($TenantId)
$body = @{ password = $plain; tool = $Tool; args = @($Arguments); passphrase = $passphrase } | ConvertTo-Json
$plain = $null
$passphrase = $null
try {
    $job = Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/tenants/$tenant/eval/jobs" -WebSession $session `
        -ContentType 'application/json' -Body $body
}
catch {
    throw "The job was not started: $($_.ErrorDetails.Message)"
}
finally {
    $body = $null
}

Write-Host "Job $($job.id): $Tool $($job.command) (encrypted workspace: $($job.encrypted))"
$printed = 0
while ($true) {
    Start-Sleep -Seconds $PollSeconds
    $job = Invoke-RestMethod -Method Get -Uri "$BridgeUrl/api/tenants/$tenant/eval/jobs/$($job.id)" -WebSession $session
    $output = [string]$job.output
    if ($output.Length -gt $printed) {
        Write-Host -NoNewline $output.Substring($printed)
        $printed = $output.Length
    }

    if ($job.state -ne 'running') {
        break
    }
}

Write-Host "State: $($job.state), exit code: $($job.exitCode)"
if ($job.state -ne 'succeeded') {
    exit 1
}
```

`src/Cortex.Contained.Bridge/Evaluation/EvaluationEndpoints.cs` (new):

```csharp
using Cortex.Contained.Bridge.Auth;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Mvc;

namespace Cortex.Contained.Bridge.Evaluation;

/// <summary>Body of <c>POST /api/tenants/{tenantId}/eval/jobs</c>.</summary>
public sealed record EvaluationJobRequest(string? Password, string? Tool, IReadOnlyList<string>? Args, string? Passphrase = null);

/// <summary>Body of <c>POST /api/tenants/{tenantId}/eval/files/read</c>.</summary>
public sealed record EvaluationFileReadRequest(string? Password, string? Tool, string? Name);

/// <summary>Body of <c>POST /api/tenants/{tenantId}/eval/files/write</c>.</summary>
public sealed record EvaluationFileWriteRequest(string? Password, string? Tool, string? Name, string? Content);

/// <summary>
/// Evaluation API (all require authorization): <c>/api/tenants/{tenantId}/eval/jobs</c> (POST start — password
/// again, rate limited; GET list), <c>/eval/jobs/{jobId}</c> (GET), <c>/eval/jobs/{jobId}/cancel</c> (POST),
/// <c>/eval/files/read</c> and <c>/eval/files/write</c> (POST, password again, rate limited: datasets carry
/// personal text). The commands run inside the agent process, which holds the storage key; the Bridge only relays.
/// </summary>
internal static class EvaluationEndpoints
{
    /// <summary>Longest workspace file accepted (datasets are JSONL; 8 Mi characters is far above 500 labelled queries).</summary>
    internal const int MaxFileChars = 8 * 1024 * 1024;

    /// <summary>Maps the evaluation endpoints onto <paramref name="app"/>.</summary>
    public static void MapEvaluationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/tenants/{tenantId}/eval/jobs", (string tenantId, EvaluationJobRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
            StartAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), body, ct)).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);

        app.MapGet("/api/tenants/{tenantId}/eval/jobs", (string tenantId, [FromServices] TenantRouter router, CancellationToken ct) =>
            ListAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), ct)).RequireAuthorization();

        app.MapGet("/api/tenants/{tenantId}/eval/jobs/{jobId}", (string tenantId, string jobId, [FromServices] TenantRouter router, CancellationToken ct) =>
            GetAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), jobId, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/eval/jobs/{jobId}/cancel", (string tenantId, string jobId, [FromServices] TenantRouter router, CancellationToken ct) =>
            CancelAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), jobId, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/eval/files/read", async (string tenantId, EvaluationFileReadRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await ReadFileAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), body, ct).ConfigureAwait(false);
        }).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);

        app.MapPost("/api/tenants/{tenantId}/eval/files/write", (string tenantId, EvaluationFileWriteRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
            WriteFileAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), body, ct)).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);
    }

    internal static async Task<IResult> StartAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, EvaluationJobRequest? body, CancellationToken ct)
    {
        if (body?.Tool is not { Length: > 0 } tool || body.Args is not { Count: > 0 } args)
        {
            return BadRequest("tool and args are required");
        }

        if (!verifyPassword(body.Password))
        {
            return Forbidden();
        }

        if (agent is null)
        {
            return NotConnected();
        }

        return await RecallEndpoints.RelayAsync(
            () => agent.StartAsync(new EvaluationStartRequestDto { Tool = tool, Args = args, Passphrase = body.Passphrase }, ct),
            job => job.State == EvaluationJobStates.Refused ? Results.Json(job, statusCode: 409) : Results.Json(job, statusCode: 202)).ConfigureAwait(false);
    }

    internal static Task<IResult> ListAsync(IEvaluationAgentClient? agent, CancellationToken ct) =>
        agent is null ? Task.FromResult(NotConnected()) : RecallEndpoints.RelayAsync(() => agent.ListAsync(ct), jobs => Results.Ok(new { jobs }));

    internal static Task<IResult> GetAsync(IEvaluationAgentClient? agent, string jobId, CancellationToken ct) =>
        agent is null
            ? Task.FromResult(NotConnected())
            : RecallEndpoints.RelayAsync(() => agent.GetAsync(jobId, ct), job => job is null ? Results.Json(new { error = "Unknown job" }, statusCode: 404) : Results.Ok(job));

    internal static Task<IResult> CancelAsync(IEvaluationAgentClient? agent, string jobId, CancellationToken ct) =>
        agent is null ? Task.FromResult(NotConnected()) : RecallEndpoints.RelayAsync(() => agent.CancelAsync(jobId, ct), cancelled => Results.Ok(new { cancelled }));

    internal static async Task<IResult> ReadFileAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, EvaluationFileReadRequest? body, CancellationToken ct)
    {
        if (body?.Tool is not { Length: > 0 } tool || body.Name is not { Length: > 0 } name)
        {
            return BadRequest("tool and name are required");
        }

        if (!verifyPassword(body.Password))
        {
            return Forbidden();
        }

        return agent is null
            ? NotConnected()
            : await RecallEndpoints.RelayAsync(
                () => agent.ReadFileAsync(new EvaluationFileRequestDto { Tool = tool, Name = name }, ct),
                file => file.Error is null ? Results.Ok(file) : Results.Json(file, statusCode: 400)).ConfigureAwait(false);
    }

    internal static async Task<IResult> WriteFileAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, EvaluationFileWriteRequest? body, CancellationToken ct)
    {
        if (body?.Tool is not { Length: > 0 } tool || body.Name is not { Length: > 0 } name || body.Content is not { } content)
        {
            return BadRequest("tool, name and content are required");
        }

        if (content.Length > MaxFileChars)
        {
            return BadRequest($"The file is larger than {MaxFileChars} characters.");
        }

        if (!verifyPassword(body.Password))
        {
            return Forbidden();
        }

        return agent is null
            ? NotConnected()
            : await RecallEndpoints.RelayAsync(
                () => agent.WriteFileAsync(new EvaluationFileWriteDto { Tool = tool, Name = name, Content = content }, ct),
                result => result.Error is null ? Results.Ok(result) : Results.Json(result, statusCode: 400)).ConfigureAwait(false);
    }

    // Failed re-authentications are counted per session; the third invalidates it (see SessionManager.VerifyPassword).
    private static Func<string?, bool> Reauthenticate(SessionManager sessions, HttpContext http) =>
        password => sessions.VerifyPassword(password, http.Request.Cookies.TryGetValue(CortexSessionAuthHandler.CookieName, out var token) ? token : null);

    private static IResult BadRequest(string error) => Results.Json(new { error }, statusCode: 400);

    private static IResult Forbidden() => Results.Json(new { error = "The password is not correct." }, statusCode: 403);

    private static IResult NotConnected() => Results.Json(new { error = "Agent not connected" }, statusCode: 503);
}
```

`src/Cortex.Contained.Bridge/Evaluation/IEvaluationAgentClient.cs` (new):

```csharp
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Evaluation;

/// <summary>The agent calls the evaluation endpoints make (a seam over <see cref="HubClient"/> for tests).</summary>
public interface IEvaluationAgentClient
{
    Task<EvaluationJobDto> StartAsync(EvaluationStartRequestDto request, CancellationToken cancellationToken);

    Task<EvaluationJobDto?> GetAsync(string jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EvaluationJobDto>> ListAsync(CancellationToken cancellationToken);

    Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken);

    Task<EvaluationFileDto> ReadFileAsync(EvaluationFileRequestDto request, CancellationToken cancellationToken);

    Task<EvaluationFileWriteResultDto> WriteFileAsync(EvaluationFileWriteDto request, CancellationToken cancellationToken);
}

/// <summary>Adapts a connected <see cref="HubClient"/> to <see cref="IEvaluationAgentClient"/>.</summary>
public sealed class HubEvaluationAgentClient(HubClient client) : IEvaluationAgentClient
{
    /// <summary>The adapter for <paramref name="client"/>, or null when it is missing or disconnected.</summary>
    public static IEvaluationAgentClient? For(HubClient? client) => client is { IsConnected: true } ? new HubEvaluationAgentClient(client) : null;

    public Task<EvaluationJobDto> StartAsync(EvaluationStartRequestDto request, CancellationToken cancellationToken) => client.StartEvaluationAsync(request, cancellationToken);

    public Task<EvaluationJobDto?> GetAsync(string jobId, CancellationToken cancellationToken) => client.GetEvaluationJobAsync(jobId, cancellationToken);

    public Task<IReadOnlyList<EvaluationJobDto>> ListAsync(CancellationToken cancellationToken) => client.ListEvaluationJobsAsync(cancellationToken);

    public Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken) => client.CancelEvaluationJobAsync(jobId, cancellationToken);

    public Task<EvaluationFileDto> ReadFileAsync(EvaluationFileRequestDto request, CancellationToken cancellationToken) => client.ReadEvaluationFileAsync(request, cancellationToken);

    public Task<EvaluationFileWriteResultDto> WriteFileAsync(EvaluationFileWriteDto request, CancellationToken cancellationToken) => client.WriteEvaluationFileAsync(request, cancellationToken);
}
```

`src/Cortex.Contained.Bridge/Hub/HubClient.Evaluation.cs` (new):

```csharp
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Bridge.Hub;

/// <summary>Evaluation jobs and workspace files in the agent (<see cref="IEvaluationHub"/>).</summary>
public sealed partial class HubClient
{
    /// <summary>Starts an evaluation command in the agent process.</summary>
    public async Task<EvaluationJobDto> StartEvaluationAsync(EvaluationStartRequestDto request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationJobDto>(nameof(IAgentHub.StartEvaluation), request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One job, or null.</summary>
    public async Task<EvaluationJobDto?> GetEvaluationJobAsync(string jobId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationJobDto?>(nameof(IAgentHub.GetEvaluationJob), jobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Recent jobs, newest first.</summary>
    public async Task<IReadOnlyList<EvaluationJobDto>> ListEvaluationJobsAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<IReadOnlyList<EvaluationJobDto>>(nameof(IAgentHub.ListEvaluationJobs), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Requests cancellation of a running job.</summary>
    public async Task<bool> CancelEvaluationJobAsync(string jobId, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<bool>(nameof(IAgentHub.CancelEvaluationJob), jobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a workspace file.</summary>
    public async Task<EvaluationFileDto> ReadEvaluationFileAsync(EvaluationFileRequestDto request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationFileDto>(nameof(IAgentHub.ReadEvaluationFile), request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a workspace file.</summary>
    public async Task<EvaluationFileWriteResultDto> WriteEvaluationFileAsync(EvaluationFileWriteDto request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<EvaluationFileWriteResultDto>(nameof(IAgentHub.WriteEvaluationFile), request, cancellationToken).ConfigureAwait(false);
    }
}
```

`src/Cortex.Contained.Bridge/Program.cs` — apply:

```diff
--- a/src/Cortex.Contained.Bridge/Program.cs
+++ b/src/Cortex.Contained.Bridge/Program.cs
@@ -1218,8 +1218,11 @@ app.MapRecallEndpoints();
 
 // --- Storage page API (encryption at rest: status, preview, enable, recovery kit export/restore) ---
 Cortex.Contained.Bridge.Storage.StorageEndpoints.MapStorageEndpoints(app, cortexDataDir);
 
+// --- Evaluation API (jobs run inside the agent process; workspace files; re-authentication for content) ---
+Cortex.Contained.Bridge.Evaluation.EvaluationEndpoints.MapEvaluationEndpoints(app);
+
 // --- Connector Plugin System endpoint (loopback-only, no WebUI dependency) ---
 app.MapConnectorEndpoint();
 
 // --- Connector management API (list/approve/deny/enable/revoke/master toggle) ---
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~EvaluationEndpointsTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Bridge.Tests` Passed! — 6 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.
Parse-check the scripts: `[System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path scripts\Invoke-CortexEval.ps1), [ref]$null, [ref]$e) | Out-Null; $e.Count` → `0` (same for `Edit-CortexEvalFile.ps1`).

- [ ] **Step 5: Commit**

```powershell
git add scripts\Edit-CortexEvalFile.ps1 scripts\Invoke-CortexEval.ps1 src\Cortex.Contained.Bridge\Evaluation\EvaluationEndpoints.cs src\Cortex.Contained.Bridge\Evaluation\IEvaluationAgentClient.cs src\Cortex.Contained.Bridge\Hub\HubClient.Evaluation.cs src\Cortex.Contained.Bridge\Program.cs tests\Cortex.Contained.Bridge.Tests\Evaluation\EvaluationEndpointsTests.cs
git commit -m "feat(bridge): evaluation job and workspace file endpoints, owner scripts" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 10: `SensitiveDataRedactor.FindPersonalIdentifiers`

The persistence redactor's personal-identifier detectors (IBAN with checksum, Luhn cards, CPR, email, GPS, Danish and generic phones) exposed as spans, so the sanitiser and its leakage check share ONE implementation with the redactor (E6).

**Files:**
- Modify: `src/Cortex.Contained.Contracts/Security/SensitiveDataRedactor.Persistence.cs`
- Test (modify): `tests/Cortex.Contained.Contracts.Tests/Security/SensitiveDataRedactorPersistenceTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Contracts.Tests/Security/SensitiveDataRedactorPersistenceTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Contracts.Tests/Security/SensitiveDataRedactorPersistenceTests.cs
+++ b/tests/Cortex.Contained.Contracts.Tests/Security/SensitiveDataRedactorPersistenceTests.cs
@@ -51,5 +51,23 @@ public sealed class SensitiveDataRedactorPersistenceTests
     public void RedactForPersistence_StillAppliesLogPatterns()
     {
         Assert.Contains("[REDACTED]", Redact("api_key=abcdefghijklmnop"), StringComparison.Ordinal);
     }
+
+    [Fact]
+    public void FindPersonalIdentifiers_SpansOfTheInputInPriorityOrder()
+    {
+        const string input = "card 4111 1111 1111 1111, cpr 010203-1234, mail a.person@example.com, call +45 12 34 56 78, ref 41234567890";
+
+        var matches = SensitiveDataRedactor.FindPersonalIdentifiers(input);
+
+        Assert.Equal(
+            ["card:4111 1111 1111 1111", "cpr:010203-1234", "email:a.person@example.com", "phone:+45 12 34 56 78", "phone:41234567890"],
+            matches.Select(m => $"{m.Kind}:{input.AsSpan(m.Index, m.Length)}"));
+    }
+
+    [Fact]
+    public void FindPersonalIdentifiers_OrdinaryText_None()
+    {
+        Assert.Empty(SensitiveDataRedactor.FindPersonalIdentifiers("order 42 shipped on monday at 14:30 for 129.95"));
+    }
 }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Contracts.Tests --filter "FullyQualifiedName~SensitiveDataRedactorPersistenceTests" -v q`
Expected: build FAILS — `'SensitiveDataRedactor' does not contain a definition for 'FindPersonalIdentifiers'`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Contracts/Security/SensitiveDataRedactor.Persistence.cs` — apply:

```diff
--- a/src/Cortex.Contained.Contracts/Security/SensitiveDataRedactor.Persistence.cs
+++ b/src/Cortex.Contained.Contracts/Security/SensitiveDataRedactor.Persistence.cs
@@ -2,8 +2,11 @@ using System.Text;
 using System.Text.RegularExpressions;
 
 namespace Cortex.Contained.Contracts.Security;
 
+/// <summary>A personal identifier found by <see cref="SensitiveDataRedactor.FindPersonalIdentifiers"/>: its kind and its span in the input.</summary>
+public readonly record struct IdentifierMatch(string Kind, int Index, int Length);
+
 /// <summary>
 /// Redaction for text that is PERSISTED (event payloads), stricter than log redaction: the input
 /// is unescaped and normalised first so encoded secrets cannot slip past the patterns, known
 /// secret values are replaced exactly, and personal identifiers (Danish CPR numbers, IBANs,
@@ -42,8 +45,57 @@ public static partial class SensitiveDataRedactor
         result = DanishPhonePattern().Replace(result, "[REDACTED:phone]");
         return Redact(result);
     }
 
+    /// <summary>Kinds reported by <see cref="FindPersonalIdentifiers"/>.</summary>
+    public static class IdentifierKinds
+    {
+        public const string Iban = "iban";
+
+        public const string Card = "card";
+
+        public const string Cpr = "cpr";
+
+        public const string Email = "email";
+
+        public const string Gps = "gps";
+
+        public const string Phone = "phone";
+    }
+
+    /// <summary>
+    /// The personal identifiers in <paramref name="input"/> as spans of the input itself (no normalisation), with the
+    /// patterns and validations of <see cref="RedactForPersistence"/> (IBAN checksum, Luhn) plus the generic phone
+    /// pattern of <see cref="Redact"/>. Overlaps are resolved in that priority order; the result is sorted by index.
+    /// The evaluation corpus sanitiser and its leakage check share these detectors with the persistence redactor.
+    /// </summary>
+    public static IReadOnlyList<IdentifierMatch> FindPersonalIdentifiers(string input)
+    {
+        ArgumentNullException.ThrowIfNull(input);
+        var accepted = new List<IdentifierMatch>();
+        void Take(Regex pattern, string kind, Func<string, bool>? valid = null)
+        {
+            foreach (Match match in pattern.Matches(input))
+            {
+                if ((valid is null || valid(match.Value))
+                    && !accepted.Exists(a => a.Index < match.Index + match.Length && match.Index < a.Index + a.Length))
+                {
+                    accepted.Add(new IdentifierMatch(kind, match.Index, match.Length));
+                }
+            }
+        }
+
+        Take(IbanPattern(), IdentifierKinds.Iban, IsValidIban);
+        Take(CardPattern(), IdentifierKinds.Card, IsLuhnValid);
+        Take(CprPattern(), IdentifierKinds.Cpr);
+        Take(EmailPattern(), IdentifierKinds.Email);
+        Take(GpsPattern(), IdentifierKinds.Gps);
+        Take(DanishPhonePattern(), IdentifierKinds.Phone);
+        Take(PhoneNumberPattern(), IdentifierKinds.Phone);
+        accepted.Sort((a, b) => a.Index.CompareTo(b.Index));
+        return accepted;
+    }
+
     /// <summary>JSON/regex-style unescape, NFKC, and removal of zero-width characters.</summary>
     internal static string Normalize(string input)
     {
         string unescaped;
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Contracts.Tests --filter "FullyQualifiedName~SensitiveDataRedactorPersistenceTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Contracts.Tests` Passed! — 13 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Contracts\Security\SensitiveDataRedactor.Persistence.cs tests\Cortex.Contained.Contracts.Tests\Security\SensitiveDataRedactorPersistenceTests.cs
git commit -m "feat(security): personal-identifier spans for the corpus sanitiser" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 11: Sanitiser core: pseudonyms, owner lexicon, text sanitiser

`PseudonymMap` (HMAC-SHA256 under the per-version secret, single-token class-prefixed pseudonyms, same-length digits for numbers, collisions fail, E7), `SanitiserLexicon` (the owner's `dictionary.jsonl`: case-insensitive whole words, up to three inflection letters for terms of four or more letters, folded spellings, `entity` groups variants, `public-host` keeps URL hosts) and `TextSanitiser` (URL → identifiers → handles → lexicon terms → 9+ digit numbers; dates, prices and short numbers kept, E8).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Sanitising/PseudonymMap.cs`
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Sanitising/SanitiserLexicon.cs`
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Sanitising/TextSanitiser.cs`
- Test (new): `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Sanitising/TextSanitiserTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Sanitising/TextSanitiserTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation.Sanitising;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Sanitising;

public sealed class TextSanitiserTests
{
    private static readonly byte[] Secret = Enumerable.Range(1, PseudonymMap.SecretBytes).Select(i => (byte)i).ToArray();

    private static SanitiserLexicon Dictionary(params string[] lines) => SanitiserLexicon.Parse(lines);

    private static TextSanitiser Sanitiser(SanitiserLexicon? dictionary = null, byte[]? secret = null) =>
        new(dictionary ?? Dictionary(
            """{"term":"Alvenia","kind":"person"}""",
            """{"term":"Vena","kind":"person","entity":"alvenia"}""",
            """{"term":"Krøllvik","kind":"place"}""",
            """{"term":"example.org","kind":"public-host"}"""),
            new PseudonymMap(secret ?? Secret));

    [Fact]
    public void Sanitise_NamesAndVariants_OneConsistentPseudonymWithInflectionKept()
    {
        var sanitiser = Sanitiser();

        var first = sanitiser.Sanitise("Alvenia and Vena met in Krøllvik; Alvenias bike");
        var second = sanitiser.Sanitise("ALVENIA lives in Krollvik");

        var person = sanitiser.Sanitise("Alvenia");
        Assert.StartsWith("Person", person, StringComparison.Ordinal);
        Assert.Equal($"{person} and {person} met in {sanitiser.Sanitise("Krøllvik")}; {person}s bike", first);
        Assert.Equal($"{person} lives in {sanitiser.Sanitise("Krøllvik")}", second);
        Assert.DoesNotContain("lvenia", first + second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitise_Identifiers_ReplacedByStablePseudonymsDatesAndPricesKept()
    {
        var sanitiser = Sanitiser();
        const string text = "On 2026-05-04 at 14:30 I paid 129.95 to DK5000400440116243, mail a.person@example.com, call +45 12 34 56 78, cpr 010203-1234, <@123456789012345678>";

        var output = sanitiser.Sanitise(text);

        Assert.Contains("On 2026-05-04 at 14:30 I paid 129.95 to iban", output, StringComparison.Ordinal);
        Assert.Contains("@example.invalid", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a.person", output, StringComparison.Ordinal);
        Assert.DoesNotContain("12 34 56 78", output, StringComparison.Ordinal);
        Assert.DoesNotContain("010203", output, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789012345678", output, StringComparison.Ordinal);
        Assert.Matches(@"<@[1-9]\d{17}>", output);
        Assert.Equal(output, sanitiser.Sanitise(text));
    }

    [Fact]
    public void Sanitise_Urls_PublicHostKeptPrivateHostAndPathsPseudonymised()
    {
        var sanitiser = Sanitiser();

        var output = sanitiser.Sanitise("see https://example.org/users/alvenia?id=7 and http://family-blog.test/2026/trip");

        Assert.StartsWith("see https://example.org/p", output, StringComparison.Ordinal);
        Assert.Contains("http://host", output, StringComparison.Ordinal);
        Assert.Contains(".example/p", output, StringComparison.Ordinal);
        Assert.DoesNotContain("alvenia", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("family-blog", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_HandlesAndZeroWidthObfuscation_Caught()
    {
        var sanitiser = Sanitiser();

        var output = sanitiser.Sanitise("ping @night_owl and Alve\u200Bnia");

        Assert.DoesNotContain("night_owl", output, StringComparison.Ordinal);
        Assert.Contains("@user", output, StringComparison.Ordinal);
        Assert.Equal(sanitiser.Sanitise("Alvenia"), output[(output.LastIndexOf(' ') + 1)..]);
    }

    [Fact]
    public void Sanitise_AnotherSecret_UnlinkablePseudonyms()
    {
        var other = Enumerable.Range(2, PseudonymMap.SecretBytes).Select(i => (byte)i).ToArray();

        Assert.NotEqual(Sanitiser().Sanitise("Alvenia"), Sanitiser(secret: other).Sanitise("Alvenia"));
    }

    [Fact]
    public void Parse_BadLines_NamedInTheError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Dictionary(
            """{"term":"Alvenia","kind":"person"}""", """{"term":"A","kind":"person"}""", """{"term":"Bo","kind":"pet"}""", "not json"));

        Assert.Contains("lines 2, 3, 4", error.Message, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~TextSanitiserTests" -v q`
Expected: build FAILS — `The type or namespace name 'Sanitising' does not exist`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Sanitising/PseudonymMap.cs` (new):

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Cortex.Contained.Agent.Host.Evaluation.Sanitising;

/// <summary>
/// Deterministic pseudonyms for one frozen corpus version: HMAC-SHA256 under the version's secret, so the same
/// person, place or identifier gets the same pseudonym in the corpus, the memories and the query datasets
/// (relevance labels stay valid), different versions are unlinkable, and nobody without the local secret can
/// reverse or confirm a guess. The secret never leaves the machine (it is not part of the export package).
/// Two different values that would share a pseudonym fail the freeze (fail closed).
/// </summary>
public sealed class PseudonymMap
{
    /// <summary>Length of a corpus secret.</summary>
    public const int SecretBytes = 32;

    private const int TokenChars = 8;

    // 32 symbols without l, o, 0 and 1.
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    private readonly byte[] secret;
    private readonly Dictionary<string, string> byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> owners = new(StringComparer.Ordinal);

    public PseudonymMap(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != SecretBytes)
        {
            throw new ArgumentException($"The corpus secret must have {SecretBytes} bytes.", nameof(secret));
        }

        this.secret = (byte[])secret.Clone();
    }

    /// <summary>Every pseudonym handed out so far (the leakage check never flags them).</summary>
    public IReadOnlyCollection<string> Pseudonyms => this.owners.Keys;

    /// <summary>A fresh random corpus secret.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>
    /// The pseudonym of <paramref name="key"/> (a canonical value) of <paramref name="kind"/>: <c>Person…</c>,
    /// <c>Place…</c>, <c>Org…</c>, <c>user…</c>, <c>Term…</c>, <c>mail…@example.invalid</c>, <c>phone…</c>,
    /// <c>cpr…</c>, <c>iban…</c>, <c>card…</c>, <c>gps…</c>, <c>host….example</c>, <c>p…</c>; <c>number</c> keeps the
    /// digit count. Each is a single token for the keyword index.
    /// </summary>
    public string For(string kind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(key);
        var id = kind + "\n" + key;
        if (this.byKey.TryGetValue(id, out var known))
        {
            return known;
        }

        var token = this.Token(id);
        var pseudonym = kind switch
        {
            "person" => "Person" + token,
            "place" => "Place" + token,
            "org" => "Org" + token,
            "handle" => "user" + token,
            "email" => "mail" + token + "@example.invalid",
            "host" => "host" + token + ".example",
            "path" => "p" + token,
            "number" => this.Digits(id, key.Length),
            _ => (kind == "other" ? "Term" : kind) + token,
        };
        if (this.owners.TryGetValue(pseudonym, out var owner) && !string.Equals(owner, id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Two values of the corpus received the same pseudonym; freeze again (a new version gets a new secret).");
        }

        this.owners[pseudonym] = id;
        this.byKey[id] = pseudonym;
        return pseudonym;
    }

    private string Token(string id)
    {
        var hash = HMACSHA256.HashData(this.secret, Encoding.UTF8.GetBytes(id));
        var chars = new char[TokenChars];
        for (var i = 0; i < TokenChars; i++)
        {
            chars[i] = Alphabet[hash[i] % Alphabet.Length];
        }

        return new string(chars);
    }

    // Same length, first digit never 0; derived from the HMAC so equal numbers stay equal.
    private string Digits(string id, int length)
    {
        var builder = new StringBuilder(length);
        var counter = 0;
        while (builder.Length < length)
        {
            var hash = HMACSHA256.HashData(this.secret, Encoding.UTF8.GetBytes(id + "\n" + counter++));
            foreach (var b in hash)
            {
                if (builder.Length == length)
                {
                    break;
                }

                builder.Append(builder.Length == 0 ? (char)('1' + (b % 9)) : (char)('0' + (b % 10)));
            }
        }

        return builder.ToString();
    }
}
```

`src/Cortex.Contained.Agent.Host/Evaluation/Sanitising/SanitiserLexicon.cs` (new):

```csharp
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MemoryMcp.Core.Retrieval;

namespace Cortex.Contained.Agent.Host.Evaluation.Sanitising;

/// <summary>One owner-maintained dictionary line (<c>dictionary.jsonl</c>, sealed on an encrypted install).</summary>
public sealed record LexiconEntry
{
    /// <summary>The term as it appears in text (a name, a place, an organisation, a handle).</summary>
    public required string Term { get; init; }

    /// <summary>One of <see cref="SanitiserLexicon.Kinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Groups variants (nicknames, spellings, scripts) under one pseudonym; defaults to the term.</summary>
    public string? Entity { get; init; }
}

/// <summary>
/// The owner's dictionary of personal terms. Matching is case-insensitive, on whole words, with up to three
/// trailing letters for inflection (Danish genitive, Russian cases) for terms of four or more letters; the
/// folded form of every term (no diacritics) is matched as well. <c>public-host</c> entries are URL hosts kept
/// as they are (public sites); every other kind is pseudonymised.
/// </summary>
public sealed class SanitiserLexicon
{
    /// <summary>Known kinds.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["person", "place", "org", "handle", "other", "public-host"];

    private const int MinTermLength = 2;
    private const int InflectedTermLength = 4;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, LexiconEntry> byCanonical;

    private SanitiserLexicon(IReadOnlyList<LexiconEntry> entries)
    {
        this.Entries = entries;
        this.PublicHosts = entries.Where(e => e.Kind == "public-host").Select(e => e.Term.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var names = entries.Where(e => e.Kind != "public-host").ToList();
        this.byCanonical = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal);
        foreach (var entry in names)
        {
            this.byCanonical.TryAdd(Canonical(entry.Term), entry);
        }

        var variants = names.SelectMany(e => new[] { e.Term, TextFolder.Fold(e.Term) }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        this.Matcher = variants.Count == 0 ? null : Build(variants);
    }

    /// <summary>All entries.</summary>
    public IReadOnlyList<LexiconEntry> Entries { get; }

    /// <summary>URL hosts kept as they are (lower case).</summary>
    public IReadOnlySet<string> PublicHosts { get; }

    /// <summary>Whole-word matcher of every term (group <c>term</c>, inflection in group <c>suffix</c>), or null when empty.</summary>
    public Regex? Matcher { get; }

    /// <summary>An empty dictionary (patterns only).</summary>
    public static SanitiserLexicon Empty { get; } = new([]);

    /// <summary>The canonical form a term and its matches share: folded (NFKC, lower case, no diacritics), trimmed.</summary>
    public static string Canonical(string term) => TextFolder.Fold(term).Trim();

    /// <summary>Parses <c>dictionary.jsonl</c>; throws <see cref="InvalidOperationException"/> naming the bad line numbers.</summary>
    public static SanitiserLexicon Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var entries = new List<LexiconEntry>();
        var bad = new List<int>();
        var number = 0;
        foreach (var line in lines)
        {
            number++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            LexiconEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<LexiconEntry>(line, Json);
            }
            catch (JsonException)
            {
                entry = null;
            }

            if (entry is null || entry.Term.Trim().Length < MinTermLength || !Kinds.Contains(entry.Kind, StringComparer.Ordinal))
            {
                bad.Add(number);
                continue;
            }

            entries.Add(entry with { Term = entry.Term.Trim() });
        }

        return bad.Count == 0
            ? new SanitiserLexicon(entries)
            : throw new InvalidOperationException($"dictionary.jsonl lines {string.Join(", ", bad)} are not {{\"term\",\"kind\"[,\"entity\"]}} with a term of at least {MinTermLength} characters and a kind of {string.Join("|", Kinds)}.");
    }

    /// <summary>The entry a matched term belongs to, or null.</summary>
    public LexiconEntry? Lookup(string matched) => this.byCanonical.GetValueOrDefault(Canonical(matched));

    private static Regex Build(IReadOnlyList<string> variants)
    {
        static string Alternation(IEnumerable<string> terms) =>
            string.Join("|", terms.OrderByDescending(t => t.Length).ThenBy(t => t, StringComparer.Ordinal).Select(Regex.Escape));

        var inflected = variants.Where(v => v.Length >= InflectedTermLength).ToList();
        var exact = variants.Where(v => v.Length < InflectedTermLength).ToList();
        var pattern = new StringBuilder(@"(?<![\p{L}\p{N}_])(?:");
        if (inflected.Count > 0)
        {
            pattern.Append("(?<term>").Append(Alternation(inflected)).Append(@")(?<suffix>\p{L}{0,3})");
        }

        if (exact.Count > 0)
        {
            pattern.Append(inflected.Count > 0 ? "|" : string.Empty).Append("(?<term>").Append(Alternation(exact)).Append(")(?<suffix>)");
        }

        pattern.Append(@")(?![\p{L}\p{N}_])");
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
    }
}
```

`src/Cortex.Contained.Agent.Host/Evaluation/Sanitising/TextSanitiser.cs` (new):

```csharp
using System.Text;
using System.Text.RegularExpressions;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Evaluation.Sanitising;

/// <summary>
/// Deterministic pseudonymisation of corpus and query text for the off-machine copy (D6). Detectors, in
/// priority order on the same text (overlaps go to the earlier detector):
/// <list type="number">
/// <item>URLs: the scheme stays, the host is kept only when the dictionary lists it as <c>public-host</c>
/// (otherwise <c>host….example</c>), path, query and fragment become one pseudonym;</item>
/// <item>personal identifiers of <see cref="SensitiveDataRedactor.FindPersonalIdentifiers"/> (IBAN, card, CPR,
/// email, GPS, phone) — one implementation with the persistence redactor;</item>
/// <item><c>@handles</c>;</item>
/// <item>owner dictionary terms (names, places, organisations), inflection suffix kept;</item>
/// <item>long numbers (9 digits or more: chat ids, account and order numbers), replaced by digits of the same length.</item>
/// </list>
/// Dates, times, prices and short numbers are kept (temporal and identifier queries depend on them). The text is
/// NFKC-normalised and stripped of zero-width characters first, so obfuscated spellings cannot slip through.
/// </summary>
public sealed partial class TextSanitiser
{
    private readonly SanitiserLexicon dictionary;
    private readonly PseudonymMap map;

    public TextSanitiser(SanitiserLexicon dictionary, PseudonymMap map)
    {
        this.dictionary = dictionary;
        this.map = map;
    }

    /// <summary>Every pseudonym produced (the leakage check skips them).</summary>
    public IReadOnlyCollection<string> Pseudonyms => this.map.Pseudonyms;

    /// <summary>NFKC plus removal of zero-width characters (the sanitiser and the leakage check see the same text).</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Normalize(NormalizationForm.FormKC);
        return normalized.AsSpan().IndexOfAny("\u200B\u200C\u200D\u2060\uFEFF") < 0
            ? normalized
            : string.Concat(normalized.Where(c => c is not ('\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF')));
    }

    /// <summary>The sanitised form of <paramref name="text"/>.</summary>
    public string Sanitise(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var input = Normalize(text);
        var spans = new List<(int Index, int Length, string Replacement)>();
        bool Free(int index, int length) => !spans.Exists(s => s.Index < index + length && index < s.Index + s.Length);

        foreach (Match url in UrlPattern().Matches(input))
        {
            spans.Add((url.Index, url.Length, this.Url(url)));
        }

        foreach (var match in SensitiveDataRedactor.FindPersonalIdentifiers(input))
        {
            if (Free(match.Index, match.Length))
            {
                var value = input.Substring(match.Index, match.Length);
                spans.Add((match.Index, match.Length, this.map.For(match.Kind, Key(value, match.Kind))));
            }
        }

        foreach (Match handle in HandlePattern().Matches(input))
        {
            if (Free(handle.Index, handle.Length))
            {
                spans.Add((handle.Index, handle.Length, "@" + this.map.For("handle", handle.Groups["name"].Value.ToLowerInvariant())));
            }
        }

        if (this.dictionary.Matcher is { } matcher)
        {
            foreach (Match term in matcher.Matches(input))
            {
                if (Free(term.Index, term.Length) && this.dictionary.Lookup(term.Groups["term"].Value) is { } entry)
                {
                    var pseudonym = this.map.For(entry.Kind, entry.Entity ?? SanitiserLexicon.Canonical(entry.Term));
                    spans.Add((term.Index, term.Length, pseudonym + term.Groups["suffix"].Value));
                }
            }
        }

        foreach (Match number in LongNumberPattern().Matches(input))
        {
            if (Free(number.Index, number.Length))
            {
                spans.Add((number.Index, number.Length, this.map.For("number", number.Value)));
            }
        }

        if (spans.Count == 0)
        {
            return input;
        }

        spans.Sort((a, b) => a.Index.CompareTo(b.Index));
        var output = new StringBuilder(input.Length);
        var position = 0;
        foreach (var (index, length, replacement) in spans)
        {
            output.Append(input, position, index - position).Append(replacement);
            position = index + length;
        }

        return output.Append(input, position, input.Length - position).ToString();
    }

    /// <summary>A channel or user id: the letter prefix stays (<c>discord-dm-</c>, <c>webchat</c>), numbers and dictionary terms are pseudonymised.</summary>
    public string SanitiseIdentifier(string? id) => this.Sanitise(id);

    private static string Key(string value, string kind) => kind switch
    {
        SensitiveDataRedactor.IdentifierKinds.Email => value.ToLowerInvariant(),
        SensitiveDataRedactor.IdentifierKinds.Gps => value,
        _ => new string(value.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant(),
    };

    private string Url(Match url)
    {
        var host = url.Groups["host"].Value.ToLowerInvariant();
        var rest = url.Groups["rest"].Value;
        var keptHost = this.dictionary.PublicHosts.Contains(host) ? host : this.map.For("host", host);
        return url.Groups["scheme"].Value + "://" + keptHost + (rest.Length > 1 ? "/" + this.map.For("path", rest) : string.Empty);
    }

    [GeneratedRegex(@"(?<scheme>https?)://(?<host>[A-Za-z0-9.\-]+)(?::\d+)?(?<rest>[/?#][^\s<>""'\)\]]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    // A handle has at least one letter; an all-digit mention (<@123…>) is a chat id and goes to the number rule.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_@.])@(?<name>(?=[A-Za-z0-9_.]*[A-Za-z_])[A-Za-z0-9_.]{2,32})(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant)]
    private static partial Regex HandlePattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d{9,}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumberPattern();
}
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~TextSanitiserTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 6 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Evaluation\Sanitising\PseudonymMap.cs src\Cortex.Contained.Agent.Host\Evaluation\Sanitising\SanitiserLexicon.cs src\Cortex.Contained.Agent.Host\Evaluation\Sanitising\TextSanitiser.cs tests\Cortex.Contained.Agent.Host.Tests\Evaluation\Sanitising\TextSanitiserTests.cs
git commit -m "feat(eval): deterministic corpus sanitiser" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 12: Fail-closed leakage check

`LeakageScanner` normalises every sanitised text and reports, by location and kind only, dictionary terms on folded text, identifier patterns, 9+ digit runs, handles and non-public URL hosts that are not produced pseudonyms (E9). `termsOnly` re-checks an existing version after the dictionary grew.

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Sanitising/LeakageScanner.cs`
- Test (new): `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Sanitising/LeakageScannerTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Sanitising/LeakageScannerTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation.Sanitising;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Sanitising;

public sealed class LeakageScannerTests
{
    private static readonly byte[] Secret = Enumerable.Range(1, PseudonymMap.SecretBytes).Select(i => (byte)i).ToArray();

    private static readonly SanitiserLexicon Lexicon = SanitiserLexicon.Parse(
    [
        """{"term":"Krøllvik","kind":"place"}""",
        """{"term":"Alvenia","kind":"person"}""",
        """{"term":"example.org","kind":"public-host"}""",
    ]);

    [Fact]
    public void Scan_SanitisedText_Passes()
    {
        var sanitiser = new TextSanitiser(Lexicon, new PseudonymMap(Secret));
        var output = sanitiser.Sanitise(
            "Alvenia (a.person@example.com, +45 12 34 56 78, cpr 010203-1234) posted https://example.org/x and http://blog.test/y from Krøllvik, id 123456789012345678, @night_owl");
        var scanner = new LeakageScanner(Lexicon, sanitiser.Pseudonyms);

        scanner.Scan("messages.Content", output);

        Assert.True(scanner.Passed, string.Join("; ", scanner.Findings));
    }

    [Theory]
    [InlineData("we drove to Krollvik", "dictionary-term")]
    [InlineData("ALVENIAS bike", "dictionary-term")]
    [InlineData("mail a.person@example.com", "email")]
    [InlineData("cpr 010203-1234", "cpr")]
    [InlineData("id 123456789012345678", "number")]
    [InlineData("ping @night_owl", "handle")]
    [InlineData("see http://blog.test/y", "url-host")]
    [InlineData("we drove to Krø\u200Bllvik", "dictionary-term")]
    public void Scan_ResidualPersonalValue_FoundByKindWithoutText(string text, string kind)
    {
        var scanner = new LeakageScanner(Lexicon, []);

        scanner.Scan("history.dataset.text", text);

        Assert.False(scanner.Passed);
        var finding = Assert.Single(scanner.Findings);
        Assert.Equal(new LeakageFinding("history.dataset.text", kind, 1), finding);
    }

    [Fact]
    public void Scan_WordThatOnlyStartsLikeATerm_NotFlagged()
    {
        var scanner = new LeakageScanner(Lexicon, []);

        scanner.Scan("messages.Content", "alvenianisation of the krollvikskyline");

        Assert.True(scanner.Passed);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~LeakageScannerTests" -v q`
Expected: build FAILS — `The type or namespace name 'LeakageScanner' could not be found`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Sanitising/LeakageScanner.cs` (new):

```csharp
using System.Text.RegularExpressions;
using Cortex.Contained.Contracts.Security;
using MemoryMcp.Core.Retrieval;

namespace Cortex.Contained.Agent.Host.Evaluation.Sanitising;

/// <summary>A residual personal value in sanitised output: where and what kind, never the text.</summary>
public sealed record LeakageFinding(string Location, string Kind, int Count);

/// <summary>
/// The fail-closed residual-leakage check of a sanitised corpus. Every sanitised text (messages, channel and user
/// ids, tool-call summaries, memories, tags, query datasets, plans) is normalised (NFKC, zero-width removed) and
/// scanned for: owner dictionary terms on the folded text (no case, no diacritics, so spelling variants the
/// sanitiser missed are caught); the personal-identifier patterns of <see cref="SensitiveDataRedactor"/>; digit
/// runs of 9 or more; <c>@handles</c>; URL hosts that are neither public nor pseudonyms. Produced pseudonyms are
/// exempt. A single finding fails the freeze: the version is deleted, the report lists locations and kinds only.
/// </summary>
public sealed partial class LeakageScanner
{
    private readonly HashSet<string> pseudonyms;
    private readonly IReadOnlySet<string> publicHosts;
    private readonly Regex? terms;
    private readonly Dictionary<(string Location, string Kind), int> findings = [];
    private readonly bool termsOnly;

    /// <param name="lexicon">The owner dictionary.</param>
    /// <param name="pseudonyms">Every pseudonym the sanitiser produced (exempt).</param>
    /// <param name="termsOnly">Only dictionary terms (re-checking a version after the dictionary grew; its pseudonyms are unknown then).</param>
    public LeakageScanner(SanitiserLexicon lexicon, IEnumerable<string> pseudonyms, bool termsOnly = false)
    {
        ArgumentNullException.ThrowIfNull(lexicon);
        ArgumentNullException.ThrowIfNull(pseudonyms);
        this.termsOnly = termsOnly;
        this.pseudonyms = pseudonyms.ToHashSet(StringComparer.Ordinal);
        this.publicHosts = lexicon.PublicHosts;
        var folded = lexicon.Entries.Where(e => e.Kind != "public-host").Select(e => SanitiserLexicon.Canonical(e.Term)).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        this.terms = folded.Count == 0 ? null : FoldedTermPattern(folded);
    }

    /// <summary>Texts scanned so far.</summary>
    public int Scanned { get; private set; }

    /// <summary>True while nothing was found.</summary>
    public bool Passed => this.findings.Count == 0;

    /// <summary>Findings by location and kind (counts only).</summary>
    public IReadOnlyList<LeakageFinding> Findings =>
        this.findings.Select(f => new LeakageFinding(f.Key.Location, f.Key.Kind, f.Value)).OrderBy(f => f.Location, StringComparer.Ordinal).ThenBy(f => f.Kind, StringComparer.Ordinal).ToList();

    /// <summary>Scans one sanitised text found at <paramref name="location"/> (e.g. <c>messages.Content</c>).</summary>
    public void Scan(string location, string? text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        this.Scanned++;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var normalized = TextSanitiser.Normalize(text);
        if (this.terms is not null)
        {
            this.Add(location, "dictionary-term", this.terms.Count(TextFolder.Fold(normalized)));
        }

        if (this.termsOnly)
        {
            return;
        }

        foreach (var match in SensitiveDataRedactor.FindPersonalIdentifiers(normalized))
        {
            var value = normalized.Substring(match.Index, match.Length);
            if (!this.IsPseudonym(value) && !this.InsidePseudonym(normalized, match.Index, match.Length))
            {
                this.Add(location, match.Kind);
            }
        }

        foreach (Match number in LongNumberPattern().Matches(normalized))
        {
            if (!this.pseudonyms.Contains(number.Value))
            {
                this.Add(location, "number");
            }
        }

        foreach (Match handle in HandlePattern().Matches(normalized))
        {
            if (!this.pseudonyms.Contains(handle.Groups["name"].Value))
            {
                this.Add(location, "handle");
            }
        }

        foreach (Match host in HostPattern().Matches(normalized))
        {
            var name = host.Groups["host"].Value.ToLowerInvariant();
            if (!this.publicHosts.Contains(name) && !this.pseudonyms.Contains(name))
            {
                this.Add(location, "url-host");
            }
        }
    }

    // The sanitiser's word rule on folded text: whole words, up to three inflection letters after terms of four or more letters.
    private static Regex FoldedTermPattern(IReadOnlyList<string> folded)
    {
        static string Alternation(IEnumerable<string> terms) => string.Join("|", terms.OrderByDescending(t => t.Length).Select(Regex.Escape));

        var inflected = folded.Where(t => t.Length >= 4).ToList();
        var exact = folded.Where(t => t.Length < 4).ToList();
        var alternatives = new List<string>();
        if (inflected.Count > 0)
        {
            alternatives.Add("(?:" + Alternation(inflected) + @")\p{L}{0,3}");
        }

        if (exact.Count > 0)
        {
            alternatives.Add("(?:" + Alternation(exact) + ")");
        }

        return new Regex(@"(?<![\p{L}\p{N}_])(?:" + string.Join("|", alternatives) + @")(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
    }

    private bool IsPseudonym(string value) => this.pseudonyms.Contains(value);

    // A pattern can match inside a pseudonym (e.g. the e-mail pattern inside mail…@example.invalid is the pseudonym itself).
    private bool InsidePseudonym(string text, int index, int length)
    {
        var start = index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        var end = index + length;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        var word = text[start..end].Trim('<', '>', '(', ')', '[', ']', ',', ';', '.', '"', '\'', ':');
        return this.pseudonyms.Contains(word);
    }

    private void Add(string location, string kind, int count = 1)
    {
        if (count > 0)
        {
            this.findings[(location, kind)] = this.findings.GetValueOrDefault((location, kind)) + count;
        }
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d{9,}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumberPattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_@.])@(?<name>(?=[A-Za-z0-9_.]*[A-Za-z_])[A-Za-z0-9_.]{2,32})(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant)]
    private static partial Regex HandlePattern();

    [GeneratedRegex(@"https?://(?<host>[A-Za-z0-9.\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();
}
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~LeakageScannerTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 10 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Evaluation\Sanitising\LeakageScanner.cs tests\Cortex.Contained.Agent.Host.Tests\Evaluation\Sanitising\LeakageScannerTests.cs
git commit -m "feat(eval): fail-closed leakage check for sanitised corpora" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 13: Frozen corpus versions: manifest, store, freeze

`FrozenCorpusBuilder.FreezeAsync` builds `/app/state/eval/frozen/vN.partial`: the sanitised `Messages` rows (ids and timestamps kept, encrypted with the evaluation ring), sanitised memories, both registered datasets sanitised with the same pseudonyms, the unchanged plans, then the leakage check; only a clean version is registered and renamed (E10). `FrozenCorpusStore` reserves never-reused version names, destroys versions and keeps the off-machine deletion ledger. The frozen-corpus DTOs are added to Contracts.

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusBuilder.cs`
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusManifest.cs`
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs`
- Create: `src/Cortex.Contained.Contracts/Hub/FrozenCorpusTypes.cs`
- Test (new): `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Frozen/FrozenCorpusBuilderTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Frozen/FrozenCorpusBuilderTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Frozen;

/// <summary>Freezing a sanitised, encrypted corpus version with consistent pseudonyms, fail-closed on leakage.</summary>
public sealed class FrozenCorpusBuilderTests : IAsyncDisposable
{
    internal static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"frozen-{Guid.NewGuid():N}");
    private readonly StorageKeyRing keys = EvalTestKeys.Ring();
    private readonly EvalProtection protection;

    public FrozenCorpusBuilderTests()
    {
        this.protection = EvalProtection.Encrypted(this.keys);
    }

    private string State => Path.Combine(this.root, "state");

    private FrozenCorpusStore Store => new(Path.Combine(this.State, "eval"));

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    /// <summary>An encrypted live install with a registered history workspace and a sealed dictionary.</summary>
    internal static async Task<FreezeSources> SeedAsync(string state, StorageKeyRing keys, EvalProtection protection, string extraText = "")
    {
        Directory.CreateDirectory(state);
        var messagesDb = Path.Combine(state, "messages.db");
        long train, invoice;
        await using (var store = new MessageStore(messagesDb, NullLogger<MessageStore>.Instance, new MessageStoreEncryption(keys, TimeSpan.FromSeconds(5))))
        {
            train = await store.SaveMessageAsync("user-123456789012345678", "discord-dm-123456789012345678", "user", "Alvenia booked the railway journey to Krøllvik, mail a.person@example.com " + extraText, T0);
            invoice = await store.SaveMessageAsync("user-123456789012345678", "discord-dm-123456789012345678", "user", "invoice INV2041 is paid", T0.AddHours(5));
        }

        var history = new EvalWorkspace(Path.Combine(state, "eval", "hybrid"), protection);
        Directory.CreateDirectory(history.Directory);
        protection.WriteLines(history.DatasetPath,
        [
            EvalDataset.ToLine(new EvalQuery
            {
                Id = "e001", Text = "when did Alvenia go to Krøllvik", Category = "lookup", Judgments = [new EvalJudgment(train, 2)],
                GoldFilters = new EvalFilters { Channel = "discord-dm-123456789012345678" },
            }),
            EvalDataset.ToLine(new EvalQuery { Id = "e002", Text = "invoice INV2041", Category = "identifier", Judgments = [new EvalJudgment(invoice, 2)] }),
        ]);
        File.WriteAllText(history.AnalysisPlanPath, "# plan");
        history.WritePlan(new AblationPlan
        {
            Configs = [new AblationConfig { Name = AblationGrid.Baseline, Mode = HistorySearchMode.Keyword }, new AblationConfig { Name = "hybrid-rrf60" }],
            Holdout = new HoldoutPlan { Config = "hybrid-rrf60" },
        });
        history.Register();

        var lexicon = Path.Combine(state, "eval", "corpus", "dictionary.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(lexicon)!);
        protection.WriteLines(lexicon, ["""{"term":"Alvenia","kind":"person"}""", """{"term":"Krøllvik","kind":"place"}"""]);
        var memory = new MemoryMcpOptions { DataDirectory = Path.Combine(state, "memory") };
        return new FreezeSources(messagesDb, memory, history, new MemoryEvalWorkspace(Path.Combine(state, "eval", "memory"), protection), lexicon);
    }

    [Fact]
    public async Task FreezeAsync_EncryptedInstall_SanitisedEncryptedVersionWithConsistentLabels()
    {
        var sources = await SeedAsync(this.State, this.keys, this.protection);

        var result = await FrozenCorpusBuilder.FreezeAsync(sources, this.Store, this.protection, TimeProvider.System, CancellationToken.None);

        SqliteConnection.ClearAllPools();
        var manifest = Assert.IsType<FrozenCorpusManifest>(result.Manifest);
        Assert.Equal("v1", manifest.Version);
        Assert.Equal((2, 2, T0, T0.AddHours(5)), (manifest.Messages, manifest.HistoryQueries, manifest.CorpusFrom, manifest.CorpusTo));
        var directory = this.Store.DirectoryOf("v1");
        Assert.Equal(Cortex.Contained.Contracts.Security.StoredFileState.Encrypted, Cortex.Contained.Contracts.Security.SqliteFileHeader.Classify(Path.Combine(directory, "messages.db")));
        Assert.All(
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories),
            f => Assert.DoesNotContain("Alvenia", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(f)), StringComparison.Ordinal));

        var frozen = new EvalWorkspace(Path.Combine(directory, "history"), this.protection);
        frozen.VerifyRegistration();
        var query = frozen.ReadDataset()[0];
        Assert.StartsWith("when did Person", query.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789012345678", query.GoldFilters!.Channel, StringComparison.Ordinal);
        Assert.Equal(sources.History.ReadDataset()[0].Judgments, query.Judgments);

        await using var copy = new MessageStore(Path.Combine(directory, "messages.db"), NullLogger<MessageStore>.Instance, this.protection.CopyMessageEncryption);
        await using var snapshot = await copy.OpenSnapshotAsync(CancellationToken.None);
        var row = (await snapshot.ReadByIdsAsync([query.Judgments[0].MessageId], CancellationToken.None)).Single();
        Assert.Equal(query.GoldFilters.Channel, row.ChannelId);
        Assert.Contains(query.Text.Split(' ')[2], row.Content, StringComparison.Ordinal);
        Assert.Contains("@example.invalid", row.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FreezeAsync_ResidualVariantTheSanitiserMissed_FailsClosedAndKeepsNothing()
    {
        var sources = await SeedAsync(this.State, this.keys, this.protection, extraText: "and later Krôllvik again");

        var result = await FrozenCorpusBuilder.FreezeAsync(sources, this.Store, this.protection, TimeProvider.System, CancellationToken.None);

        Assert.Null(result.Manifest);
        Assert.Equal(new Cortex.Contained.Agent.Host.Evaluation.Sanitising.LeakageFinding("messages.Content", "dictionary-term", 1), Assert.Single(result.Findings));
        Assert.Empty(Directory.EnumerateDirectories(this.Store.Root));
    }

    [Fact]
    public async Task FreezeAsync_DatasetChangedSinceRegistration_Refused()
    {
        var sources = await SeedAsync(this.State, this.keys, this.protection);
        this.protection.WriteLines(sources.History.DatasetPath, [.. sources.History.ReadDataset().Take(1).Select(EvalDataset.ToLine)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => FrozenCorpusBuilder.FreezeAsync(sources, this.Store, this.protection, TimeProvider.System, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~FrozenCorpusBuilderTests" -v q`
Expected: build FAILS — `The type or namespace name 'Frozen' does not exist in the namespace 'Cortex.Contained.Agent.Host.Evaluation'`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusBuilder.cs` (new):

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Evaluation.Sanitising;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>What a freeze reads: the live stores, the two registered workspaces and the owner dictionary.</summary>
public sealed record FreezeSources(string MessagesDb, MemoryMcpOptions Memory, EvalWorkspace History, MemoryEvalWorkspace MemoryWorkspace, string LexiconPath);

/// <summary>Outcome of a freeze: the new version, or the leakage findings that stopped it.</summary>
public sealed record FreezeResult(FrozenCorpusManifest? Manifest, IReadOnlyList<LeakageFinding> Findings);

/// <summary>
/// Freezes a version (D6, §4.2): a sanitised copy of the message history (only the <c>Messages</c> rows, ids and
/// timestamps unchanged, text, channel and user ids pseudonymised, tool-call arguments sanitised, external message
/// ids dropped), the sanitised curated memories, both registered datasets sanitised with the same pseudonyms (labels
/// stay valid), the unchanged analysis and ablation plans, a fresh registration of the sanitised files and a manifest
/// with their hashes. The version is built in <c>vN.partial</c>, scanned by <see cref="LeakageScanner"/> and only then
/// renamed; a finding deletes it (fail closed). Nothing is ever written in plaintext on an encrypted install.
/// </summary>
public static class FrozenCorpusBuilder
{
    /// <summary>Sealed file of the version's pseudonym secret (never exported).</summary>
    public const string SecretFileName = "secret.key";

    /// <summary>Sanitised memories (one <see cref="MemorySeedSource"/> per line, sealed).</summary>
    public const string MemoriesFileName = "memories.jsonl";

    /// <summary>Key of the messages digest in <see cref="FrozenCorpusManifest.Files"/>.</summary>
    public const string MessagesDigestKey = "messages.db#rows";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] Workspaces = ["history", "memory"];

    private static readonly string[] PlanFiles = ["analysis-plan.md", "ablation.json"];

    /// <summary>Freezes the next version; throws <see cref="InvalidOperationException"/> when the inputs are not ready.</summary>
    public static async Task<FreezeResult> FreezeAsync(FreezeSources sources, FrozenCorpusStore store, EvalProtection protection, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(time);
        var lexicon = File.Exists(sources.LexiconPath) ? SanitiserLexicon.Parse(protection.ReadLines(sources.LexiconPath)) : SanitiserLexicon.Empty;
        var history = ReadRegistered(sources.History.DatasetPath, sources.History.VerifyRegistration, sources.History.ReadDataset);
        var memoryQueries = ReadRegistered(sources.MemoryWorkspace.DatasetPath, sources.MemoryWorkspace.VerifyRegistration, sources.MemoryWorkspace.ReadDataset);
        if (history is null && memoryQueries is null)
        {
            throw new InvalidOperationException("Nothing to freeze: neither history-eval nor memory-eval has a dataset.");
        }

        var version = store.ReserveNextVersion();
        var final = store.DirectoryOf(version);
        var partial = final + ".partial";
        Directory.CreateDirectory(partial);
        try
        {
            var secret = PseudonymMap.NewSecret();
            protection.WriteText(Path.Combine(partial, SecretFileName), Convert.ToBase64String(secret));
            var sanitiser = new TextSanitiser(lexicon, new PseudonymMap(secret));
            CryptographicOperations.ZeroMemory(secret);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);

            var dbPath = Path.Combine(partial, "messages.db");
            var copyPassword = protection.CopyPassword(MessageStore.PasswordPurpose);
            var corpus = await WriteMessagesAsync(sources.MessagesDb, protection.SourcePassword(MessageStore.PasswordPurpose), dbPath, protection, sanitiser, cancellationToken).ConfigureAwait(false);
            files[MessagesDigestKey] = MessagesDigest(dbPath, copyPassword);

            var memories = (await ReadMemoriesAsync(sources.Memory, Path.Combine(partial, "tmp-memory"), protection, cancellationToken).ConfigureAwait(false))
                .Select(m => new MemorySeedSource(m.MemoryId, m.Title is null ? null : sanitiser.Sanitise(m.Title), m.Tags.Select(sanitiser.Sanitise).ToList(), sanitiser.Sanitise(m.Content)))
                .ToList();
            WriteSealed(protection, Path.Combine(partial, MemoriesFileName), memories.Select(m => JsonSerializer.Serialize(m, Json)), files, MemoriesFileName);

            if (history is not null)
            {
                var missing = history.SelectMany(q => q.Judgments).Count(j => !corpus.Ids.Contains(j.MessageId));
                if (missing > 0)
                {
                    throw new InvalidOperationException($"{missing} history judgments refer to messages that no longer exist (deleted): update dataset.jsonl and register again.");
                }

                var sanitised = history.Select(q => Sanitise(q, sanitiser)).ToList();
                WriteWorkspace(protection, sources.History.Directory, Path.Combine(partial, "history"), sanitised.Select(EvalDataset.ToLine), files, "history");
            }

            if (memoryQueries is not null)
            {
                var memoryIds = memories.Select(m => m.MemoryId).ToHashSet(StringComparer.Ordinal);
                var missing = memoryQueries.SelectMany(q => q.Judgments).Count(j => !memoryIds.Contains(j.MemoryId));
                if (missing > 0)
                {
                    throw new InvalidOperationException($"{missing} memory judgments refer to memories that no longer exist: update the memory dataset.jsonl and register again.");
                }

                var sanitised = memoryQueries.Select(q => Sanitise(q, sanitiser)).ToList();
                WriteWorkspace(protection, sources.MemoryWorkspace.Directory, Path.Combine(partial, "memory"), sanitised.Select(MemoryEvalDataset.ToLine), files, "memory");
            }

            var scanner = new LeakageScanner(lexicon, sanitiser.Pseudonyms);
            ScanVersion(partial, dbPath, copyPassword, protection, scanner);
            if (!scanner.Passed)
            {
                DeleteDirectory(partial);
                return new FreezeResult(null, scanner.Findings);
            }

            if (history is not null)
            {
                new EvalWorkspace(Path.Combine(partial, "history"), protection).Register();
            }

            if (memoryQueries is not null)
            {
                new MemoryEvalWorkspace(Path.Combine(partial, "memory"), protection).Register();
            }

            var manifest = new FrozenCorpusManifest
            {
                Version = version,
                CreatedAt = time.GetUtcNow(),
                CorpusFrom = corpus.From,
                CorpusTo = corpus.To,
                Messages = corpus.Ids.Count,
                Memories = memories.Count,
                HistoryQueries = history?.Count ?? 0,
                MemoryQueries = memoryQueries?.Count ?? 0,
                LexiconEntries = lexicon.Entries.Count,
                LeakageTextsScanned = scanner.Scanned,
                Encrypted = protection.IsEncrypted,
                Files = files,
                RawRegistration = RawRegistration(sources),
            };
            manifest.Write(Path.Combine(partial, FrozenCorpusManifest.FileName));
            SqliteConnection.ClearAllPools();
            Directory.Move(partial, final);
            return new FreezeResult(manifest, []);
        }
        catch
        {
            DeleteDirectory(partial);
            throw;
        }
    }

    /// <summary>SHA-256 over the rows of <c>Messages</c> (ids, time, role, category, channel, user, text, tool calls), the version's integrity hash of the corpus.</summary>
    public static string MessagesDigest(string dbPath, string? password)
    {
        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(dbPath, password, SqliteOpenMode.ReadOnly, pooling: false));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Timestamp, Role, Category, ChannelId, UserId, Content, ToolCalls FROM Messages ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? "\0" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
                var bytes = Encoding.UTF8.GetBytes(value);
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>SHA-256 (hex) of a text's UTF-8 bytes.</summary>
    public static string TextDigest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static IReadOnlyList<T>? ReadRegistered<T>(string datasetPath, Action verifyRegistration, Func<IReadOnlyList<T>> read)
    {
        if (!File.Exists(datasetPath))
        {
            return null;
        }

        verifyRegistration();
        return read();
    }

    private static EvalQuery Sanitise(EvalQuery query, TextSanitiser sanitiser)
    {
        EvalFilters? Filters(EvalFilters? filters) =>
            filters is null ? null : filters with { Channel = filters.Channel is null ? null : sanitiser.SanitiseIdentifier(filters.Channel) };

        return query with
        {
            Text = sanitiser.Sanitise(query.Text),
            GoldFilters = Filters(query.GoldFilters),
            ModelQuery = query.ModelQuery is null ? null : query.ModelQuery with { Text = sanitiser.Sanitise(query.ModelQuery.Text), Filters = Filters(query.ModelQuery.Filters) },
        };
    }

    private static MemoryEvalQuery Sanitise(MemoryEvalQuery query, TextSanitiser sanitiser) => query with
    {
        Text = sanitiser.Sanitise(query.Text),
        Tags = query.Tags?.Select(sanitiser.Sanitise).ToList(),
        ModelQuery = query.ModelQuery is null ? null : query.ModelQuery with { Text = sanitiser.Sanitise(query.ModelQuery.Text), Tags = query.ModelQuery.Tags?.Select(sanitiser.Sanitise).ToList() },
    };

    private static async Task<(HashSet<long> Ids, DateTimeOffset From, DateTimeOffset To)> WriteMessagesAsync(
        string sourceDb, string? sourcePassword, string targetDb, EvalProtection protection, TextSanitiser sanitiser, CancellationToken cancellationToken)
    {
        // The store creates the schema (encrypted with the evaluation ring when the install is encrypted); only Messages rows are written.
        await using (var schema = new MessageStore(targetDb, NullLogger<MessageStore>.Instance, protection.CopyMessageEncryption))
        {
            await schema.WhenOpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var ids = new HashSet<long>();
        DateTimeOffset? from = null, to = null;
        using var source = new SqliteConnection(EncryptedSqlite.ConnectionString(sourceDb, sourcePassword, SqliteOpenMode.ReadOnly, pooling: false));
        using var target = new SqliteConnection(EncryptedSqlite.ConnectionString(targetDb, protection.CopyPassword(MessageStore.PasswordPurpose), SqliteOpenMode.ReadWrite, pooling: false));
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await target.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = target.BeginTransaction();
        using var insert = target.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO Messages (Id, UserId, ChannelId, Role, Content, Timestamp, MessageId, Category, ToolCalls)
            VALUES (@id, @user, @channel, @role, @content, @timestamp, NULL, @category, @tools);
            """;
        var parameters = new[]
        {
            insert.Parameters.Add("@id", SqliteType.Integer),
            insert.Parameters.Add("@user", SqliteType.Text),
            insert.Parameters.Add("@channel", SqliteType.Text),
            insert.Parameters.Add("@role", SqliteType.Text),
            insert.Parameters.Add("@content", SqliteType.Text),
            insert.Parameters.Add("@timestamp", SqliteType.Text),
            insert.Parameters.Add("@category", SqliteType.Integer),
            insert.Parameters.Add("@tools", SqliteType.Text),
        };
        using var read = source.CreateCommand();
        read.CommandText = "SELECT Id, UserId, ChannelId, Role, Content, Timestamp, Category, ToolCalls FROM Messages ORDER BY Id;";
        using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var timestamp = reader.GetString(5);
            var at = SqliteDateTimeText.Parse(timestamp);
            from = from is null || at < from ? at : from;
            to = to is null || at > to ? at : to;
            parameters[0].Value = id;
            parameters[1].Value = sanitiser.SanitiseIdentifier(reader.GetString(1));
            parameters[2].Value = sanitiser.SanitiseIdentifier(reader.GetString(2));
            parameters[3].Value = reader.GetString(3);
            parameters[4].Value = sanitiser.Sanitise(reader.GetString(4));
            parameters[5].Value = timestamp;
            parameters[6].Value = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
            parameters[7].Value = reader.IsDBNull(7) ? DBNull.Value : (object?)SanitiseToolCalls(reader.GetString(7), sanitiser) ?? DBNull.Value;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            ids.Add(id);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (ids, from ?? DateTimeOffset.UnixEpoch, to ?? DateTimeOffset.UnixEpoch);
    }

    // Tool names stay (history labels depend on them); the 32-character argument previews are sanitised like text.
    private static string? SanitiseToolCalls(string json, TextSanitiser sanitiser)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<ToolCallSummaryEntry>>(json, Json);
            return entries is null ? null : JsonSerializer.Serialize(entries.Select(e => e with { Args = sanitiser.Sanitise(e.Args) }), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<MemorySeedSource>> ReadMemoriesAsync(MemoryMcpOptions live, string runDirectory, EvalProtection protection, CancellationToken cancellationToken)
    {
        if (!File.Exists(live.DatabasePath))
        {
            return [];
        }

        try
        {
            await using var corpus = await MemoryEvalCorpus.CreateAsync(live.DataDirectory, runDirectory, live, new NoEmbedding(), cancellationToken, protection: protection).ConfigureAwait(false);
            return await corpus.ListMemoriesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteDirectory(runDirectory);
        }
    }

    private static void WriteWorkspace(EvalProtection protection, string rawWorkspace, string target, IEnumerable<string> datasetLines, Dictionary<string, string> files, string prefix)
    {
        Directory.CreateDirectory(target);
        WriteSealed(protection, Path.Combine(target, "dataset.jsonl"), datasetLines, files, prefix + "/dataset.jsonl");
        foreach (var name in PlanFiles)
        {
            var text = File.ReadAllText(Path.Combine(rawWorkspace, name));
            File.WriteAllText(Path.Combine(target, name), text);
            files[prefix + "/" + name] = TextDigest(text);
        }
    }

    private static void WriteSealed(EvalProtection protection, string path, IEnumerable<string> lines, Dictionary<string, string> files, string key)
    {
        var text = string.Concat(lines.Select(l => l + "\n"));
        protection.WriteText(path, text);
        files[key] = TextDigest(text);
    }

    private static void ScanVersion(string partial, string dbPath, string? password, EvalProtection protection, LeakageScanner scanner)
    {
        using (var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(dbPath, password, SqliteOpenMode.ReadOnly, pooling: false)))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT UserId, ChannelId, Content, ToolCalls FROM Messages;";
            using var reader = cmd.ExecuteReader();
            string[] columns = ["UserId", "ChannelId", "Content", "ToolCalls"];
            while (reader.Read())
            {
                for (var i = 0; i < columns.Length; i++)
                {
                    scanner.Scan("messages." + columns[i], reader.IsDBNull(i) ? null : reader.GetString(i));
                }
            }
        }

        foreach (var line in protection.ReadLines(Path.Combine(partial, MemoriesFileName)))
        {
            scanner.Scan(MemoriesFileName, line);
        }

        foreach (var workspace in Workspaces.Select(w => Path.Combine(partial, w)).Where(Directory.Exists))
        {
            var name = Path.GetFileName(workspace);
            foreach (var line in protection.ReadLines(Path.Combine(workspace, "dataset.jsonl")))
            {
                scanner.Scan(name + "/dataset.jsonl", line);
            }

            scanner.Scan(name + "/analysis-plan.md", File.ReadAllText(Path.Combine(workspace, "analysis-plan.md")));
            scanner.Scan(name + "/ablation.json", File.ReadAllText(Path.Combine(workspace, "ablation.json")));
        }
    }

    private static Dictionary<string, string> RawRegistration(FreezeSources sources)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (prefix, path) in new[] { ("history", sources.History.PreregistrationPath), ("memory", sources.MemoryWorkspace.PreregistrationPath) })
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json) is { } registered)
            {
                foreach (var (name, hash) in registered)
                {
                    hashes[prefix + "/" + name] = hash;
                }
            }
        }

        return hashes;
    }

    private static void DeleteDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left for D3 and the next freeze; a .partial directory is never served
        }
    }

    // Listing memories never embeds; a call means a bug, not a degraded run.
    internal sealed class NoEmbedding : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Freezing does not embed.");

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Freezing does not embed.");
    }
}
```

`src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusManifest.cs` (new):

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>Where the off-machine copy of a version lives (D6): a file attached to the one SafeExchange secret the assistant may use.</summary>
public sealed record OffsiteCopy
{
    /// <summary>The only SafeExchange secret evaluation packages are attached to (it also holds the storage recovery kit).</summary>
    public const string SecretName = "cortex-recovery-kit";

    /// <summary>The secret (always <see cref="SecretName"/>).</summary>
    public string Secret { get; init; } = SecretName;

    /// <summary>The attachment's contentId, as reported by SafeExchange after the upload.</summary>
    public string? ContentId { get; init; }

    /// <summary>SHA-256 of the uploaded package (must equal the exported one).</summary>
    public string? Sha256 { get; init; }

    public DateTimeOffset UploadedAt { get; init; }
}

/// <summary>
/// <c>manifest.json</c> of a frozen corpus version (plaintext: ids, numbers and hashes only). The corpus range is
/// what D3 compares a purge with; <see cref="Files"/> holds the SHA-256 of every sanitised file's plaintext (the
/// version's registration hashes, verified again on import).
/// </summary>
public sealed record FrozenCorpusManifest
{
    /// <summary>File name inside the version directory.</summary>
    public const string FileName = "manifest.json";

    /// <summary>Format of the sanitiser that produced the version (bump when its rules change).</summary>
    public const int CurrentSanitiserVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary><c>v1</c>, <c>v2</c>, …</summary>
    public required string Version { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Oldest message in the corpus (timestamps are kept unchanged by the sanitiser).</summary>
    public DateTimeOffset CorpusFrom { get; init; }

    /// <summary>Newest message in the corpus.</summary>
    public DateTimeOffset CorpusTo { get; init; }

    public int Messages { get; init; }

    public int Memories { get; init; }

    public int HistoryQueries { get; init; }

    public int MemoryQueries { get; init; }

    public int SanitiserVersion { get; init; } = CurrentSanitiserVersion;

    /// <summary>Entries in the owner dictionary when the version was frozen.</summary>
    public int LexiconEntries { get; init; }

    /// <summary>Texts the leakage check scanned (it passed, or the version would not exist).</summary>
    public int LeakageTextsScanned { get; init; }

    /// <summary>True when the local copy is encrypted with the evaluation key (an encrypted install).</summary>
    public bool Encrypted { get; init; }

    /// <summary>True when the version came from an imported package (no local secret; it is not re-exported).</summary>
    public bool Imported { get; init; }

    /// <summary>Relative path → SHA-256 of the plaintext of every sanitised file.</summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } = new Dictionary<string, string>();

    /// <summary>The raw workspaces' registration hashes (<c>history/…</c>, <c>memory/…</c>) the sanitised datasets derive from.</summary>
    public IReadOnlyDictionary<string, string> RawRegistration { get; init; } = new Dictionary<string, string>();

    /// <summary>When the export package was last written, if ever.</summary>
    public DateTimeOffset? ExportedAt { get; init; }

    /// <summary>SHA-256 of the export package, if exported.</summary>
    public string? PackageSha256 { get; init; }

    /// <summary>The recorded off-machine copy, if the owner reported the upload.</summary>
    public OffsiteCopy? Offsite { get; init; }

    /// <summary>The package's file name, also its attachment name in SafeExchange.</summary>
    [JsonIgnore]
    public string PackageFileName => PackageFileNameOf(this.Version);

    /// <summary><c>cortex-eval-corpus-&lt;version&gt;.cxpkg</c>.</summary>
    public static string PackageFileNameOf(string version) => $"cortex-eval-corpus-{version}.cxpkg";

    /// <summary>The number of a version name (<c>v7</c> → 7), or null.</summary>
    public static int? NumberOf(string version) =>
        version.Length > 1 && version[0] == 'v' && int.TryParse(version.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    /// <summary>Reads a manifest, or null when absent or unreadable.</summary>
    public static FrozenCorpusManifest? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<FrozenCorpusManifest>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the manifest atomically.</summary>
    public void Write(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}
```

`src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs` (new):

```csharp
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
```

`src/Cortex.Contained.Contracts/Hub/FrozenCorpusTypes.cs` (new):

```csharp
namespace Cortex.Contained.Contracts.Hub;

/// <summary>A frozen evaluation corpus version (sanitised corpus + datasets + plans + registration hashes).</summary>
public sealed record FrozenCorpusVersionDto
{
    /// <summary><c>v1</c>, <c>v2</c>, …</summary>
    public required string Version { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Oldest message of the corpus (D3 compares purges with this range).</summary>
    public DateTimeOffset CorpusFrom { get; init; }

    public DateTimeOffset CorpusTo { get; init; }

    public int Messages { get; init; }

    public int Memories { get; init; }

    public int HistoryQueries { get; init; }

    public int MemoryQueries { get; init; }

    /// <summary>File name of the export package and of its attachment in SafeExchange (<c>cortex-eval-corpus-v1.cxpkg</c>).</summary>
    public required string PackageFileName { get; init; }

    /// <summary>When the package was last exported, if ever.</summary>
    public DateTimeOffset? ExportedAt { get; init; }

    /// <summary>True when the owner recorded the upload to SafeExchange.</summary>
    public bool Uploaded { get; init; }

    /// <summary>True for a version imported from a package (evaluation elsewhere).</summary>
    public bool Imported { get; init; }
}

/// <summary>An off-machine package that must be deleted because a purge destroyed its version (D3).</summary>
public sealed record OffsiteDeletionDto
{
    public required string Version { get; init; }

    /// <summary>The SafeExchange secret the package is attached to (always <c>cortex-recovery-kit</c>).</summary>
    public required string Secret { get; init; }

    /// <summary>The attachment's file name in that secret.</summary>
    public required string PackageFileName { get; init; }

    /// <summary>The attachment's contentId when the owner recorded it after the upload.</summary>
    public string? ContentId { get; init; }

    public DateTimeOffset DestroyedAt { get; init; }
}

/// <summary>Frozen corpora, export packages and pending off-machine deletions.</summary>
public sealed record FrozenCorpusStatusDto
{
    public IReadOnlyList<FrozenCorpusVersionDto> Versions { get; init; } = [];

    /// <summary>Export packages present in the agent (passphrase-encrypted), by file name.</summary>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>Off-machine copies the owner still has to delete from SafeExchange.</summary>
    public IReadOnlyList<OffsiteDeletionDto> PendingOffsiteDeletions { get; init; } = [];
}

/// <summary>A chunk of an export package (passphrase-encrypted bytes).</summary>
public sealed record EvaluationPackageChunkDto
{
    /// <summary>Total package length.</summary>
    public long Length { get; init; }

    public byte[] Data { get; init; } = [];

    public string? Error { get; init; }
}
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~FrozenCorpusBuilderTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 3 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Evaluation\Frozen\FrozenCorpusBuilder.cs src\Cortex.Contained.Agent.Host\Evaluation\Frozen\FrozenCorpusManifest.cs src\Cortex.Contained.Agent.Host\Evaluation\Frozen\FrozenCorpusStore.cs src\Cortex.Contained.Contracts\Hub\FrozenCorpusTypes.cs tests\Cortex.Contained.Agent.Host.Tests\Evaluation\Frozen\FrozenCorpusBuilderTests.cs
git commit -m "feat(eval): frozen sanitised corpus versions" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 14: Dictionary suggestions (heuristics and optional LLM)

`LexiconSuggester` proposes dictionary terms: capitalised words that are not sentence-initial and never appear in lower case, and — on request — named entities from the configured LLM provider (D5). Nothing is applied automatically (E6).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Sanitising/LexiconSuggester.cs`
- Test (new): `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Sanitising/LexiconSuggesterTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Sanitising/LexiconSuggesterTests.cs` (new):

````csharp
using Cortex.Contained.Agent.Host.Evaluation.Sanitising;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Sanitising;

public sealed class LexiconSuggesterTests
{
    private static readonly SanitiserLexicon Known = SanitiserLexicon.Parse(["""{"term":"Alvenia","kind":"person"}"""]);

    [Fact]
    public void Capitalised_ProperNounsNotSentenceInitialAndNeverLowercase_Suggested()
    {
        string[] texts =
        [
            "We met Dorvin at the market. Then Dorvin left.",
            "Ask Dorvin and Alvenia about Morvenby tomorrow",
            "the train goes to Morvenby. Train tickets are cheap, the train is late",
        ];

        var suggestions = LexiconSuggester.Capitalised(texts, Known);

        Assert.Equal(["Dorvin", "Morvenby"], suggestions.Select(s => s.Term));
        Assert.All(suggestions, s => Assert.Equal("capitalised", s.Source));
    }

    [Fact]
    public async Task FromLlmAsync_ParsesEntitiesSkipsKnownTermsAndUnknownKinds()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>()).Returns(new LlmCompletionResult
        {
            Success = true,
            Content = "```json\n" + """[{"term":"Dorvin","kind":"person"},{"term":"Alvenia","kind":"person"},{"term":"Morvenby","kind":"place"},{"term":"Rex","kind":"pet"}]""" + "\n```",
        });

        var suggestions = await LexiconSuggester.FromLlmAsync(["Dorvin and Alvenia went to Morvenby with Rex"], Known, llm, "model-a", maxBatches: 5, CancellationToken.None);

        Assert.Equal(["Dorvin:person", "Morvenby:place"], suggestions.Select(s => s.Term + ":" + s.Kind));
        await llm.Received(1).CompleteAsync(Arg.Is<LlmCompletionRequest>(r => r.Model == "model-a" && r.Temperature == 0), Arg.Any<CancellationToken>());
    }
}
````

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~LexiconSuggesterTests" -v q`
Expected: build FAILS — `The type or namespace name 'LexiconSuggester' could not be found`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Sanitising/LexiconSuggester.cs` (new):

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Evaluation.Sanitising;

/// <summary>A candidate dictionary term for the owner to review (<c>dictionary.suggested.jsonl</c>, sealed).</summary>
public sealed record LexiconSuggestion(string Term, string Kind, int Count, string Source);

/// <summary>
/// Candidates for the owner dictionary; never applied automatically (the owner moves accepted lines into
/// <c>dictionary.jsonl</c>). Two sources: proper-noun heuristics over the corpus (capitalised words that are not
/// sentence-initial and never appear in lower case), and optionally the configured LLM provider (D5 allows cloud
/// models to process history; no other destination ever sees the text). Terms the dictionary already covers are skipped.
/// </summary>
public static partial class LexiconSuggester
{
    /// <summary>Most characters sent to the LLM per batch.</summary>
    public const int BatchChars = 8000;

    private const string Prompt = """
        You find personal named entities in chat messages so they can be pseudonymised. Return ONLY a JSON array of
        objects {"term": "...", "kind": "person|place|org|handle"} with every name of a real person, every private or
        small place (street, village, venue), every organisation and every user handle that appears in the text, written
        exactly as in the text (one entry per spelling). Leave out common words, product names, programming terms and
        famous public places. Return [] when there is none.
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "person", "place", "org", "handle" };

    /// <summary>Proper-noun candidates seen at least <paramref name="minCount"/> times, most frequent first.</summary>
    public static IReadOnlyList<LexiconSuggestion> Capitalised(IEnumerable<string> texts, SanitiserLexicon lexicon, int minCount = 2)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(lexicon);
        var capitalised = new Dictionary<string, int>(StringComparer.Ordinal);
        var lower = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            var normalized = TextSanitiser.Normalize(text);
            foreach (Match word in WordPattern().Matches(normalized))
            {
                var value = word.Value;
                if (char.IsLower(value[0]))
                {
                    lower.Add(value);
                }
                else if (!IsSentenceStart(normalized, word.Index))
                {
                    capitalised[value] = capitalised.GetValueOrDefault(value) + 1;
                }
            }
        }

        return capitalised
            .Where(c => c.Value >= minCount && !lower.Contains(c.Key.ToLowerInvariant()) && lexicon.Lookup(c.Key) is null && !c.Key.All(char.IsUpper))
            .OrderByDescending(c => c.Value)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => new LexiconSuggestion(c.Key, "unknown", c.Value, "capitalised"))
            .ToList();
    }

    /// <summary>Entities the LLM finds in up to <paramref name="maxBatches"/> batches of <paramref name="texts"/>.</summary>
    public static async Task<IReadOnlyList<LexiconSuggestion>> FromLlmAsync(
        IEnumerable<string> texts, SanitiserLexicon lexicon, ILlmClient llm, string model, int maxBatches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(lexicon);
        ArgumentNullException.ThrowIfNull(llm);
        var found = new Dictionary<(string Term, string Kind), int>();
        foreach (var batch in Batches(texts).Take(maxBatches))
        {
            var result = await llm.CompleteAsync(
                new LlmCompletionRequest
                {
                    Model = model,
                    Messages = [new LlmMessage { Role = "system", Content = Prompt }, new LlmMessage { Role = "user", Content = batch }],
                    Temperature = 0,
                    MaxTokens = 2000,
                    RequestId = Guid.NewGuid().ToString("N"),
                    ConversationId = "eval-corpus-suggest",
                },
                cancellationToken).ConfigureAwait(false);
            foreach (var entity in Parse(result.Success ? result.Content : null))
            {
                found[(entity.Term, entity.Kind)] = found.GetValueOrDefault((entity.Term, entity.Kind)) + 1;
            }
        }

        return found
            .Where(f => lexicon.Lookup(f.Key.Term) is null)
            .OrderByDescending(f => f.Value)
            .ThenBy(f => f.Key.Term, StringComparer.Ordinal)
            .Select(f => new LexiconSuggestion(f.Key.Term, f.Key.Kind, f.Value, "llm"))
            .ToList();
    }

    private static IEnumerable<string> Batches(IEnumerable<string> texts)
    {
        var batch = new System.Text.StringBuilder();
        foreach (var text in texts.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (batch.Length > 0 && batch.Length + text.Length > BatchChars)
            {
                yield return batch.ToString();
                batch.Clear();
            }

            batch.AppendLine(text.Length > BatchChars ? text[..BatchChars] : text);
        }

        if (batch.Length > 0)
        {
            yield return batch.ToString();
        }
    }

    private static List<(string Term, string Kind)> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<Entity>>(MemoryConsolidationService.StripToJson(content), Json) ?? [])
                .Where(e => e.Term is { Length: >= 2 } && e.Kind is not null && Kinds.Contains(e.Kind))
                .Select(e => (e.Term!.Trim(), e.Kind!))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsSentenceStart(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        return i < 0 || text[i] is '.' or '!' or '?' or ':' or '"' or '\n';
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])\p{L}[\p{L}'\-]{2,}(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    private sealed record Entity(string? Term, string? Kind);
}
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~LexiconSuggesterTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 2 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Evaluation\Sanitising\LexiconSuggester.cs tests\Cortex.Contained.Agent.Host.Tests\Evaluation\Sanitising\LexiconSuggesterTests.cs
git commit -m "feat(eval): dictionary suggestions for the sanitiser" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 15: Passphrase KDF shared with the recovery kit; chunked AEAD package cipher

`PassphraseKdf` (Contracts) holds the recovery kit's PBKDF2-HMAC-SHA256 parameters; `StorageRecoveryKit` uses it unchanged in behaviour. `PackageCipher` is the package stream: header (KDF, salt, chunk size, nonce prefix), then AES-256-GCM chunks with nonce = prefix ‖ counter ‖ last-flag and the header hash as associated data, so truncation, reordering, appending and any modified byte fail (E11).

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Frozen/PackageCipher.cs`
- Modify: `src/Cortex.Contained.Bridge/Security/StorageRecoveryKit.cs`
- Create: `src/Cortex.Contained.Contracts/Security/PassphraseKdf.cs`
- Test (new): `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Frozen/PackageCipherTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Frozen/PackageCipherTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Frozen;

public sealed class PackageCipherTests
{
    private const string Passphrase = "correct horse battery staple";
    private const int Chunk = 64;

    private static byte[] Seal(byte[] plaintext)
    {
        using var output = new MemoryStream();
        using (var writer = PackageCipher.CreateWriter(output, Passphrase, PassphraseKdf.MinimumIterations, Chunk))
        {
            writer.Write(plaintext);
        }

        return output.ToArray();
    }

    private static byte[] Open(byte[] package, string passphrase = Passphrase)
    {
        using var reader = PackageCipher.OpenReader(new MemoryStream(package), passphrase);
        using var plaintext = new MemoryStream();
        reader.CopyTo(plaintext);
        return plaintext.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData((3 * Chunk) + 5)]
    public void RoundTrip_AnyLength_ReturnsThePlaintext(int length)
    {
        var plaintext = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

        Assert.Equal(plaintext, Open(Seal(plaintext)));
    }

    [Fact]
    public void Open_WrongPassphrase_Fails()
    {
        Assert.Throws<InvalidDataException>(() => Open(Seal([1, 2, 3]), "another passphrase"));
    }

    [Theory]
    [InlineData("truncate-chunk")]
    [InlineData("drop-last-chunk")]
    [InlineData("append")]
    [InlineData("flip-header")]
    [InlineData("flip-body")]
    public void Open_TamperedPackage_Fails(string tamper)
    {
        var package = Seal(Enumerable.Range(0, 3 * Chunk).Select(i => (byte)i).ToArray());
        var sealedChunk = Chunk + 16;
        byte[] damaged = tamper switch
        {
            "truncate-chunk" => package[..^5],
            "drop-last-chunk" => package[..^16],
            "append" => [.. package, 0],
            "flip-header" => Flip(package, 20),
            _ => Flip(package, package.Length - sealedChunk - 3),
        };

        Assert.ThrowsAny<Exception>(() => Open(damaged));
    }

    [Fact]
    public void CreateWriter_ShortPassphrase_Refused()
    {
        Assert.Throws<ArgumentException>(() => PackageCipher.CreateWriter(new MemoryStream(), "short"));
    }

    private static byte[] Flip(byte[] bytes, int index)
    {
        var copy = (byte[])bytes.Clone();
        copy[index] ^= 0x01;
        return copy;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~PackageCipherTests" -v q` and `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~StorageRecoveryKitTests|FullyQualifiedName~StorageEndpointsTests" -v q`
Expected: build FAILS — `The type or namespace name 'PackageCipher' could not be found`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Frozen/PackageCipher.cs` (new):

```csharp
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>
/// The passphrase encryption of an evaluation package (<c>.cxpkg</c>): <see cref="PassphraseKdf"/> (PBKDF2-HMAC-SHA256,
/// 600,000 iterations, the recovery kit's derivation) into an AES-256-GCM key, then a chunked AEAD stream (the STREAM
/// construction): the plaintext is cut into chunks of <see cref="DefaultChunkSize"/>, each sealed with the nonce
/// <c>prefix(7) ‖ counter(4, big-endian) ‖ last(1)</c> and the SHA-256 of the header as associated data. Reordered,
/// dropped, truncated or appended chunks and any modified byte (header included) fail authentication; a wrong
/// passphrase fails on the first chunk. Layout: <c>CXPKG1\n</c>, header length (4 bytes, big-endian), header JSON, chunks.
/// </summary>
public static class PackageCipher
{
    /// <summary>Plaintext bytes per chunk.</summary>
    public const int DefaultChunkSize = 1 << 20;

    /// <summary>Format identifier in the header.</summary>
    public const string Format = "cortex-eval-package/1";

    internal const int TagBytes = 16;
    internal const int PrefixBytes = 7;
    private const int MaxHeaderBytes = 4096;

    private static readonly byte[] Magic = "CXPKG1\n"u8.ToArray();

    /// <summary>Starts an encrypted package on <paramref name="inner"/>; dispose the result to write the final chunk.</summary>
    public static Stream CreateWriter(Stream inner, string passphrase, int iterations = PassphraseKdf.DefaultIterations, int chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(passphrase);
        if (passphrase.Length < PassphraseKdf.MinimumPassphraseLength)
        {
            throw new ArgumentException($"The passphrase needs at least {PassphraseKdf.MinimumPassphraseLength} characters.", nameof(passphrase));
        }

        var header = new Header
        {
            Format = Format,
            Kdf = PassphraseKdf.Name,
            Iterations = iterations,
            Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(PassphraseKdf.SaltBytes)),
            Cipher = "AES-256-GCM-STREAM",
            ChunkSize = chunkSize,
            NoncePrefix = Convert.ToBase64String(RandomNumberGenerator.GetBytes(PrefixBytes)),
        };
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, headerBytes.Length);
        inner.Write(Magic);
        inner.Write(length);
        inner.Write(headerBytes);
        var key = PassphraseKdf.DeriveKey(passphrase, Convert.FromBase64String(header.Salt), iterations);
        return new PackageWriteStream(inner, key, Convert.FromBase64String(header.NoncePrefix), AssociatedData(length, headerBytes), chunkSize);
    }

    /// <summary>Opens an encrypted package; throws <see cref="InvalidDataException"/> for a file that is not a package.</summary>
    public static Stream OpenReader(Stream inner, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(passphrase);
        Span<byte> magic = stackalloc byte[7];
        Span<byte> length = stackalloc byte[4];
        if (inner.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) != magic.Length || !magic.SequenceEqual(Magic)
            || inner.ReadAtLeast(length, 4, throwOnEndOfStream: false) != 4)
        {
            throw new InvalidDataException("The file is not an evaluation package.");
        }

        var headerLength = BinaryPrimitives.ReadInt32BigEndian(length);
        if (headerLength is <= 0 or > MaxHeaderBytes)
        {
            throw new InvalidDataException("The evaluation package header is damaged.");
        }

        var headerBytes = new byte[headerLength];
        inner.ReadExactly(headerBytes);
        Header? header;
        try
        {
            header = JsonSerializer.Deserialize<Header>(headerBytes);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The evaluation package header is damaged.", ex);
        }

        if (header is not { Format: Format, Kdf: PassphraseKdf.Name, Cipher: "AES-256-GCM-STREAM" }
            || header.Iterations is < PassphraseKdf.MinimumIterations or > PassphraseKdf.MaximumIterations
            || header.ChunkSize is < 16 or > 16 << 20)
        {
            throw new InvalidDataException("The file is not a supported evaluation package.");
        }

        byte[] salt, prefix;
        try
        {
            salt = Convert.FromBase64String(header.Salt);
            prefix = Convert.FromBase64String(header.NoncePrefix);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The evaluation package header is damaged.", ex);
        }

        if (prefix.Length != PrefixBytes)
        {
            throw new InvalidDataException("The evaluation package header is damaged.");
        }

        var key = PassphraseKdf.DeriveKey(passphrase, salt, header.Iterations);
        return new PackageReadStream(inner, key, prefix, AssociatedData(length.ToArray(), headerBytes), header.ChunkSize);
    }

    internal static byte[] Nonce(byte[] prefix, uint counter, bool last)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(PrefixBytes), counter);
        nonce[11] = last ? (byte)1 : (byte)0;
        return nonce;
    }

    private static byte[] AssociatedData(byte[] length, byte[] header) => SHA256.HashData([.. Magic, .. length, .. header]);

    private sealed record Header
    {
        public string Format { get; init; } = string.Empty;

        public string Kdf { get; init; } = string.Empty;

        public int Iterations { get; init; }

        public string Salt { get; init; } = string.Empty;

        public string Cipher { get; init; } = string.Empty;

        public int ChunkSize { get; init; }

        public string NoncePrefix { get; init; } = string.Empty;
    }

    private sealed class PackageWriteStream(Stream inner, byte[] key, byte[] prefix, byte[] associatedData, int chunkSize) : Stream
    {
        private readonly AesGcm aes = new(key, TagBytes);
        private readonly byte[] buffer = new byte[chunkSize];
        private int filled;
        private uint counter;
        private bool finished;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !this.finished;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(this.finished, this);
            while (data.Length > 0)
            {
                if (this.filled == this.buffer.Length)
                {
                    // Only now is it known that this full chunk is not the last one.
                    this.Seal(last: false);
                }

                var take = Math.Min(data.Length, this.buffer.Length - this.filled);
                data[..take].CopyTo(this.buffer.AsSpan(this.filled));
                this.filled += take;
                data = data[take..];
            }
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !this.finished)
            {
                this.Seal(last: true);
                this.finished = true;
                inner.Flush();
                this.aes.Dispose();
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(this.buffer);
            }

            base.Dispose(disposing);
        }

        private void Seal(bool last)
        {
            var output = new byte[this.filled + TagBytes];
            this.aes.Encrypt(Nonce(prefix, this.counter++, last), this.buffer.AsSpan(0, this.filled), output.AsSpan(0, this.filled), output.AsSpan(this.filled), associatedData);
            inner.Write(output);
            this.filled = 0;
        }
    }

    private sealed class PackageReadStream(Stream inner, byte[] key, byte[] prefix, byte[] associatedData, int chunkSize) : Stream
    {
        private readonly AesGcm aes = new(key, TagBytes);
        private readonly byte[] sealedChunk = new byte[chunkSize + TagBytes];
        private byte[] plain = [];
        private int offset;
        private uint counter;
        private bool lastSeen;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            while (this.offset == this.plain.Length)
            {
                if (this.lastSeen)
                {
                    return 0;
                }

                this.Open();
            }

            var take = Math.Min(destination.Length, this.plain.Length - this.offset);
            this.plain.AsSpan(this.offset, take).CopyTo(destination);
            this.offset += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.aes.Dispose();
                CryptographicOperations.ZeroMemory(key);
            }

            base.Dispose(disposing);
        }

        private void Open()
        {
            var read = inner.ReadAtLeast(this.sealedChunk, this.sealedChunk.Length, throwOnEndOfStream: false);
            if (read < TagBytes)
            {
                throw new InvalidDataException("The evaluation package is truncated.");
            }

            var length = read - TagBytes;
            var plaintext = new byte[length];
            var sealedBytes = this.sealedChunk.AsSpan(0, length);
            var tag = this.sealedChunk.AsSpan(length, TagBytes);

            // A short chunk can only be the last; a full one is the last only when the "last" nonce authenticates it.
            var candidates = read < this.sealedChunk.Length ? new[] { true } : [false, true];
            foreach (var last in candidates)
            {
                try
                {
                    this.aes.Decrypt(Nonce(prefix, this.counter, last), sealedBytes, tag, plaintext, associatedData);
                    this.counter++;
                    this.lastSeen = last;
                    this.plain = plaintext;
                    this.offset = 0;
                    if (last && inner.ReadByte() >= 0)
                    {
                        throw new InvalidDataException("The evaluation package has data after its last chunk.");
                    }

                    return;
                }
                catch (AuthenticationTagMismatchException)
                {
                    // try the other position, then fail
                }
            }

            throw new InvalidDataException("Wrong passphrase, or the evaluation package was modified or truncated.");
        }
    }
}
```

`src/Cortex.Contained.Bridge/Security/StorageRecoveryKit.cs` — apply:

```diff
--- a/src/Cortex.Contained.Bridge/Security/StorageRecoveryKit.cs
+++ b/src/Cortex.Contained.Bridge/Security/StorageRecoveryKit.cs
@@ -30,9 +30,9 @@ public sealed class StorageRecoveryKitException : Exception
 }
 
 /// <summary>
 /// The owner's offline backup of a tenant storage key: the key encrypted with AES-256-GCM under a key
-/// derived from a passphrase with PBKDF2-HMAC-SHA256 (600,000 iterations, 128-bit salt). Format, tenant and
+/// derived from a passphrase with <see cref="PassphraseKdf"/> (PBKDF2-HMAC-SHA256, 600,000 iterations, 128-bit salt). Format, tenant and
 /// key fingerprint are authenticated as associated data, so a kit cannot be relabelled for another tenant
 /// or key. Without the passphrase the file is useless; the key is never logged.
 /// </summary>
 public static class StorageRecoveryKit
@@ -40,17 +40,17 @@ public static class StorageRecoveryKit
     /// <summary>Format identifier written into every kit.</summary>
     public const string Format = "cortex-storage-recovery-kit/1";
 
     /// <summary>PBKDF2 iterations of a new kit (OWASP 2023 guidance for PBKDF2-HMAC-SHA256).</summary>
-    public const int DefaultIterations = 600_000;
+    public const int DefaultIterations = PassphraseKdf.DefaultIterations;
 
     /// <summary>Fewest PBKDF2 iterations a kit may declare.</summary>
-    public const int MinimumIterations = 100_000;
+    public const int MinimumIterations = PassphraseKdf.MinimumIterations;
 
     /// <summary>Shortest accepted passphrase.</summary>
-    public const int MinimumPassphraseLength = 12;
+    public const int MinimumPassphraseLength = PassphraseKdf.MinimumPassphraseLength;
 
-    private const int SaltBytes = 16;
+    private const int SaltBytes = PassphraseKdf.SaltBytes;
     private const int NonceBytes = 12;
     private const int TagBytes = 16;
 
     private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
@@ -72,9 +72,9 @@ public static class StorageRecoveryKit
             var salt = RandomNumberGenerator.GetBytes(SaltBytes);
             var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
             var ciphertext = new byte[key.Length];
             var tag = new byte[TagBytes];
-            var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
+            var wrappingKey = PassphraseKdf.DeriveKey(passphrase, salt, iterations);
             try
             {
                 using var aes = new AesGcm(wrappingKey, TagBytes);
                 aes.Encrypt(nonce, key, ciphertext, tag, AssociatedData(tenantId, fingerprint));
@@ -122,9 +122,9 @@ public static class StorageRecoveryKit
         }
 
         if (kit is not { Format: Format, TenantId.Length: > 0, KeyFingerprint.Length: > 0, Kdf: { } kdf, Cipher: { } cipher }
             || kdf.Iterations < minimumIterations
-            || kdf.Iterations > 10_000_000)
+            || kdf.Iterations > PassphraseKdf.MaximumIterations)
         {
             throw new StorageRecoveryKitException("The file is not a supported recovery kit.");
         }
 
@@ -146,9 +146,9 @@ public static class StorageRecoveryKit
             throw new StorageRecoveryKitException("The recovery kit is damaged.");
         }
 
         var key = new byte[ciphertext.Length];
-        var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, kdf.Iterations, HashAlgorithmName.SHA256, 32);
+        var wrappingKey = PassphraseKdf.DeriveKey(passphrase, salt, kdf.Iterations);
         try
         {
             using var aes = new AesGcm(wrappingKey, TagBytes);
             aes.Decrypt(nonce, ciphertext, tag, key, AssociatedData(kit.TenantId, kit.KeyFingerprint));
```

`src/Cortex.Contained.Contracts/Security/PassphraseKdf.cs` (new):

```csharp
using System.Security.Cryptography;

namespace Cortex.Contained.Contracts.Security;

/// <summary>
/// The passphrase key derivation shared by every passphrase-encrypted file the owner keeps off the machine (the
/// storage recovery kit, evaluation corpus packages): PBKDF2-HMAC-SHA256 with a 128-bit random salt and 600,000
/// iterations (OWASP 2023 guidance) into a 256-bit AES key.
/// </summary>
public static class PassphraseKdf
{
    /// <summary>Name written into file headers.</summary>
    public const string Name = "PBKDF2-HMAC-SHA256";

    /// <summary>Iterations of a new file.</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Fewest iterations a file may declare.</summary>
    public const int MinimumIterations = 100_000;

    /// <summary>Most iterations a file may declare (a hostile header cannot pin the CPU).</summary>
    public const int MaximumIterations = 10_000_000;

    /// <summary>Shortest accepted passphrase.</summary>
    public const int MinimumPassphraseLength = 12;

    /// <summary>Salt length.</summary>
    public const int SaltBytes = 16;

    /// <summary>The 256-bit key for <paramref name="passphrase"/>; the caller zeroes it after use.</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentNullException.ThrowIfNull(salt);
        return Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
    }
}
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~PackageCipherTests" -v q` and `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~StorageRecoveryKitTests|FullyQualifiedName~StorageEndpointsTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 12 tests, 0 failed; `Cortex.Contained.Bridge.Tests` Passed! — 28 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Evaluation\Frozen\PackageCipher.cs src\Cortex.Contained.Bridge\Security\StorageRecoveryKit.cs src\Cortex.Contained.Contracts\Security\PassphraseKdf.cs tests\Cortex.Contained.Agent.Host.Tests\Evaluation\Frozen\PackageCipherTests.cs
git commit -m "feat(eval): passphrase-encrypted package stream reusing the recovery kit KDF" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 16: Package export and import

`FrozenCorpusPackage.Export` writes `cortex-eval-corpus-vN.cxpkg` (tar in the package stream; `messages.db` encrypted with a random key carried in the package manifest; the version secret excluded) and records the export and its SHA-256. `Import` verifies every file against the manifest hashes, re-seals for the local install and registers the sanitised workspaces again.

**Files:**
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusPackage.cs`
- Test (new): `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Frozen/FrozenCorpusPackageTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Frozen/FrozenCorpusPackageTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Frozen;

/// <summary>Export from an encrypted install, import elsewhere (here: a plaintext install), hashes verified.</summary>
public sealed class FrozenCorpusPackageTests : IAsyncDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"package-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    private async Task<(FrozenCorpusStore Store, FrozenCorpusManifest Manifest)> FreezeAsync()
    {
        var state = Path.Combine(this.root, "state");
        var sources = await FrozenCorpusBuilderTests.SeedAsync(state, EvalTestKeys.Ring(), this.protection);
        var store = new FrozenCorpusStore(Path.Combine(state, "eval"));
        var result = await FrozenCorpusBuilder.FreezeAsync(sources, store, this.protection, TimeProvider.System, CancellationToken.None);
        return (store, result.Manifest!);
    }

    [Fact]
    public async Task ExportThenImportElsewhere_SameSanitisedCorpusDatasetsAndRegistration()
    {
        var (store, manifest) = await this.FreezeAsync();

        var package = FrozenCorpusPackage.Export(store, manifest.Version, this.protection, Passphrase, TimeProvider.System);

        Assert.Equal("cortex-eval-corpus-v1.cxpkg", package.FileName);
        Assert.Equal(package.Sha256, store.Read("v1")!.PackageSha256);
        Assert.DoesNotContain("Person", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(package.Path)), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(store.PackagesDirectory, "*.tmp"));

        var elsewhere = new FrozenCorpusStore(Path.Combine(this.root, "elsewhere", "eval"));
        var imported = FrozenCorpusPackage.Import(elsewhere, package.Path, EvalProtection.Plaintext, Passphrase);

        Assert.True(imported.Imported);
        var directory = elsewhere.DirectoryOf("v1");
        Assert.Equal(manifest.Files[FrozenCorpusBuilder.MessagesDigestKey], FrozenCorpusBuilder.MessagesDigest(Path.Combine(directory, "messages.db"), null));
        var history = new EvalWorkspace(Path.Combine(directory, "history"));
        history.VerifyRegistration();
        var original = new EvalWorkspace(Path.Combine(store.DirectoryOf("v1"), "history"), this.protection).ReadDataset();
        Assert.Equal(original.Select(EvalDataset.ToLine), history.ReadDataset().Select(EvalDataset.ToLine));
        Assert.False(File.Exists(Path.Combine(directory, FrozenCorpusBuilder.SecretFileName)));
    }

    [Fact]
    public async Task Import_WrongPassphraseOrExistingVersion_RefusedAndNothingLeftBehind()
    {
        var (store, manifest) = await this.FreezeAsync();
        var package = FrozenCorpusPackage.Export(store, manifest.Version, this.protection, Passphrase, TimeProvider.System);
        var elsewhere = new FrozenCorpusStore(Path.Combine(this.root, "elsewhere", "eval"));

        Assert.Throws<InvalidDataException>(() => FrozenCorpusPackage.Import(elsewhere, package.Path, EvalProtection.Plaintext, "another passphrase"));
        Assert.Empty(Directory.EnumerateDirectories(elsewhere.Root));
        Assert.Throws<InvalidOperationException>(() => FrozenCorpusPackage.Import(store, package.Path, this.protection, Passphrase));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~FrozenCorpusPackageTests" -v q`
Expected: build FAILS — `The name 'FrozenCorpusPackage' does not exist in the current context`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusPackage.cs` (new):

```csharp
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>Where an export package was written.</summary>
public sealed record ExportedPackage(string Path, string FileName, long Length, string Sha256);

/// <summary>
/// The off-machine form of a frozen version (D6): one passphrase-encrypted file (<see cref="PackageCipher"/>) holding
/// a tar archive of the sanitised files and the manifest. The sanitised <c>messages.db</c> travels encrypted with a
/// random key of its own (in the package manifest), so neither export nor import ever writes it in plaintext; the
/// version's pseudonym secret never leaves the machine. Import verifies every file against the manifest hashes,
/// re-seals the files for the local install and registers the sanitised datasets again.
/// </summary>
public static class FrozenCorpusPackage
{
    private const string PackageManifestEntry = "package.json";
    private const string DatabaseEntry = "messages.db";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Text files of a version, relative to its directory; sealed ones carry personal (sanitised) text.
    private static readonly (string Path, bool Sealed)[] TextFiles =
    [
        (FrozenCorpusBuilder.MemoriesFileName, true),
        ("history/dataset.jsonl", true), ("history/analysis-plan.md", false), ("history/ablation.json", false),
        ("memory/dataset.jsonl", true), ("memory/analysis-plan.md", false), ("memory/ablation.json", false),
    ];

    /// <summary>Writes <c>cortex-eval-corpus-&lt;version&gt;.cxpkg</c> into the packages directory and records the export in the manifest.</summary>
    public static ExportedPackage Export(FrozenCorpusStore store, string version, EvalProtection protection, string passphrase, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(time);
        var manifest = store.Read(version) ?? throw new InvalidOperationException($"No frozen corpus {version}.");
        if (manifest.Imported)
        {
            throw new InvalidOperationException($"{version} was imported from a package; export only versions frozen on this install.");
        }

        var directory = store.DirectoryOf(version);
        Directory.CreateDirectory(store.PackagesDirectory);
        var path = store.PackageOf(version);
        var temp = path + ".tmp";
        var databaseCopy = Path.Combine(store.PackagesDirectory, version + ".db.tmp");
        var databasePassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        try
        {
            DeleteFiles(databaseCopy, temp);
            EncryptedSqlite.CopyDatabase(Path.Combine(directory, "messages.db"), protection.CopyPassword(MessageStore.PasswordPurpose), databaseCopy, databasePassword);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var encrypted = PackageCipher.CreateWriter(file, passphrase))
            using (var tar = new TarWriter(encrypted, TarEntryFormat.Pax, leaveOpen: true))
            {
                var exported = manifest with { ExportedAt = null, PackageSha256 = null, Offsite = null, Encrypted = false };
                AddText(tar, PackageManifestEntry, JsonSerializer.Serialize(new PackageManifest(exported, databasePassword), Json));
                tar.WriteEntry(databaseCopy, DatabaseEntry);
                foreach (var (relative, sealedFile) in TextFiles)
                {
                    var source = Path.Combine(directory, relative);
                    if (File.Exists(source))
                    {
                        AddText(tar, relative, sealedFile ? protection.ReadText(source) : File.ReadAllText(source));
                    }
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFiles(databaseCopy, temp);
        }

        var info = new FileInfo(path);
        string sha256;
        using (var stream = info.OpenRead())
        {
            sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        (manifest with { ExportedAt = time.GetUtcNow(), PackageSha256 = sha256 }).Write(Path.Combine(directory, FrozenCorpusManifest.FileName));
        return new ExportedPackage(path, info.Name, info.Length, sha256);
    }

    /// <summary>
    /// Imports a package as a local version (same name), verifying every file against the manifest hashes.
    /// Throws <see cref="InvalidDataException"/> for a wrong passphrase or a damaged package and
    /// <see cref="InvalidOperationException"/> when the version already exists.
    /// </summary>
    public static FrozenCorpusManifest Import(FrozenCorpusStore store, string packagePath, EvalProtection protection, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protection);
        var staging = Path.Combine(store.Root, "import-" + Guid.NewGuid().ToString("N")[..8] + ".partial");
        Directory.CreateDirectory(staging);
        try
        {
            PackageManifest? package = null;
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            var databaseCopy = Path.Combine(staging, "messages.db.package");
            using (var file = File.OpenRead(packagePath))
            using (var decrypted = PackageCipher.OpenReader(file, passphrase))
            using (var tar = new TarReader(decrypted))
            {
                while (tar.GetNextEntry() is { } entry)
                {
                    if (entry.Name == DatabaseEntry && entry.DataStream is not null)
                    {
                        entry.ExtractToFile(databaseCopy, overwrite: false);
                    }
                    else if (entry.Name == PackageManifestEntry && entry.DataStream is not null)
                    {
                        package = JsonSerializer.Deserialize<PackageManifest>(entry.DataStream, Json);
                    }
                    else if (TextFiles.Any(t => t.Path == entry.Name))
                    {
                        // An empty file has no data stream.
                        using var reader = new StreamReader(entry.DataStream ?? Stream.Null, Encoding.UTF8);
                        texts[entry.Name] = reader.ReadToEnd();
                    }
                }
            }

            if (package?.Manifest is not { } manifest || !File.Exists(databaseCopy))
            {
                throw new InvalidDataException("The evaluation package is incomplete.");
            }

            var target = store.DirectoryOf(manifest.Version);
            if (Directory.Exists(target))
            {
                throw new InvalidOperationException($"{manifest.Version} already exists on this install.");
            }

            var databasePath = Path.Combine(staging, "messages.db");
            var copyPassword = protection.CopyPassword(MessageStore.PasswordPurpose);
            EncryptedSqlite.CopyDatabase(databaseCopy, package.DatabasePassword, databasePath, copyPassword);
            File.Delete(databaseCopy);
            Verify(manifest, FrozenCorpusBuilder.MessagesDigestKey, FrozenCorpusBuilder.MessagesDigest(databasePath, copyPassword));
            foreach (var (relative, sealedFile) in TextFiles)
            {
                if (!texts.TryGetValue(relative, out var text))
                {
                    continue;
                }

                Verify(manifest, relative, FrozenCorpusBuilder.TextDigest(text));
                var path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (sealedFile)
                {
                    protection.WriteText(path, text);
                }
                else
                {
                    File.WriteAllText(path, text);
                }
            }

            if (manifest.Files.Keys.FirstOrDefault(k => k != FrozenCorpusBuilder.MessagesDigestKey && !texts.ContainsKey(k)) is { } missing)
            {
                throw new InvalidDataException($"The evaluation package lacks {missing}.");
            }

            if (Directory.Exists(Path.Combine(staging, "history")))
            {
                new EvalWorkspace(Path.Combine(staging, "history"), protection).Register();
            }

            if (Directory.Exists(Path.Combine(staging, "memory")))
            {
                new MemoryEvalWorkspace(Path.Combine(staging, "memory"), protection).Register();
            }

            var imported = manifest with { Imported = true, Encrypted = protection.IsEncrypted, ExportedAt = null, PackageSha256 = null, Offsite = null };
            imported.Write(Path.Combine(staging, FrozenCorpusManifest.FileName));
            SqliteConnection.ClearAllPools();
            Directory.Move(staging, target);
            return imported;
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }
    }

    private static void Verify(FrozenCorpusManifest manifest, string key, string digest)
    {
        if (!manifest.Files.TryGetValue(key, out var expected) || !string.Equals(expected, digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{key} does not match the package manifest.");
        }
    }

    private static void AddText(TarWriter tar, string name, string text)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) };
        tar.WriteEntry(entry);
    }

    private static void DeleteFiles(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed record PackageManifest(FrozenCorpusManifest Manifest, string DatabasePassword);
}
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~FrozenCorpusPackageTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 2 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Evaluation\Frozen\FrozenCorpusPackage.cs tests\Cortex.Contained.Agent.Host.Tests\Evaluation\Frozen\FrozenCorpusPackageTests.cs
git commit -m "feat(eval): export and import of frozen corpus packages" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 17: Evaluating a frozen corpus (`--corpus frozen:vN`)

`history-eval` and `memory-eval` accept `--corpus frozen:vN`: the version's own registered workspace and corpus, read with the evaluation ring (`ForFrozenSource`). Memory builds a private store from the sanitised memories (chunked and embedded like the live store, ids unchanged) via `MemoryEvalCorpus.CreateFromSnapshotAsync`; `OpenForAsync` picks the right source for every memory command, including `rerank-curve` and `gate-r`.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalWorkspace.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryRerankEval.cs`
- Test (new): `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Frozen/FrozenCorpusRunTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Frozen/FrozenCorpusRunTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Tests.History;
using Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Frozen;

/// <summary>history-eval and memory-eval on a frozen sanitised copy (<c>--corpus frozen:vN</c>).</summary>
public sealed class FrozenCorpusRunTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"frozen-run-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    internal static Task<int> RunHistoryAsync(FreezeSources sources, EvalProtection protection, StringWriter output, params string[] args) =>
        HistoryEvalCli.RunAsync(
            [.. args, "--dir", sources.History.Directory, "--source", sources.MessagesDb, "--tz", "UTC"],
            output,
            _ => new FakeHistoryEmbedder(new Dictionary<string, string> { ["train"] = "railway", ["trip"] = "journey" }),
            CancellationToken.None,
            protection: protection);

    [Fact]
    public async Task HistoryHoldout_FrozenCorpus_RunsOnTheSanitisedCopyEncrypted()
    {
        var state = Path.Combine(this.root, "state");
        var sources = await FrozenCorpusBuilderTests.SeedAsync(state, EvalTestKeys.Ring(), this.protection);
        var store = new FrozenCorpusStore(Path.Combine(state, "eval"));
        await FrozenCorpusBuilder.FreezeAsync(sources, store, this.protection, TimeProvider.System, CancellationToken.None);
        using var output = new StringWriter();

        Assert.Equal(0, await RunHistoryAsync(sources, this.protection, output, "holdout", "--corpus", "frozen:v1"));

        SqliteConnection.ClearAllPools();
        var run = Directory.GetDirectories(Path.Combine(store.DirectoryOf("v1"), "history", "runs")).Single();
        Assert.True(File.Exists(Path.Combine(run, "verdict.json")));
        Assert.Equal(Cortex.Contained.Contracts.Security.StoredFileState.Encrypted, Cortex.Contained.Contracts.Security.SqliteFileHeader.Classify(Path.Combine(run, "messages.db")));
        Assert.False(Directory.Exists(Path.Combine(sources.History.Directory, "runs")));
    }

    [Theory]
    [InlineData("frozen:v9")]
    [InlineData("v1")]
    public async Task HistoryValidate_UnknownOrMalformedCorpus_Refused(string corpus)
    {
        var sources = await FrozenCorpusBuilderTests.SeedAsync(Path.Combine(this.root, "state"), EvalTestKeys.Ring(), this.protection);
        using var output = new StringWriter();

        Assert.Equal(2, await RunHistoryAsync(sources, this.protection, output, "validate", "--corpus", corpus));
    }

    [Fact]
    public async Task MemoryCorpus_FromSanitisedSnapshot_SearchesByTheOriginalIds()
    {
        var snapshot = Path.Combine(this.root, "memories.jsonl");
        Directory.CreateDirectory(this.root);
        this.protection.WriteLines(snapshot,
        [
            System.Text.Json.JsonSerializer.Serialize(new MemorySeedSource("mem-invoice", null, ["finance"], "Invoice INV-2041 was paid by Personab3k9x2m"), System.Text.Json.JsonSerializerOptions.Web),
            System.Text.Json.JsonSerializer.Serialize(new MemorySeedSource("mem-train", "Trip", ["travel"], "Train trip to Placeq7w2e9rt"), System.Text.Json.JsonSerializerOptions.Web),
        ]);
        var live = new MemoryMcpOptions { DataDirectory = Path.Combine(this.root, "live"), Ollama = new OllamaOptions { Dimensions = FakeMemoryEmbedder.Dimensions } };
        var workspace = new MemoryEvalWorkspace(Path.Combine(this.root, "memory"), this.protection.ForFrozenSource(), snapshot);

        await using var corpus = await MemoryEvalCorpus.OpenForAsync(workspace, live, new FakeMemoryEmbedder(), Path.Combine(this.root, "run"), CancellationToken.None);
        var run = await corpus.SearchAsync(
            new MemoryAblationConfig { Name = "keyword", Mode = MemorySearchMode.Keyword }, new MemoryEvalQuery { Id = "m1", Text = "INV-2041", Category = "identifier" }, CancellationToken.None);

        Assert.Equal(["mem-invoice"], run.Memories.Select(m => m.MemoryId));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~FrozenCorpusRunTests|FullyQualifiedName~MemoryEvalCorpusTests|FullyQualifiedName~MemoryEvalCliTests|FullyQualifiedName~HistoryEvalCliTests|FullyQualifiedName~MemoryRerankEvalTests|FullyQualifiedName~HistoryRerankEvalTests" -v q`
Expected: build FAILS — `'MemoryEvalCorpus' does not contain a definition for 'OpenForAsync'` (and `MemoryEvalWorkspace` has no `snapshot` parameter).

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs
+++ b/src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs
@@ -60,8 +60,28 @@ public sealed class FrozenCorpusStore
         Directory.Exists(this.PackagesDirectory)
             ? Directory.EnumerateFiles(this.PackagesDirectory, "*.cxpkg").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
             : [];
 
+    /// <summary>
+    /// The directory of the version named by a <c>--corpus frozen:&lt;version&gt;</c> option, looked up in the evaluation
+    /// directory that holds <paramref name="workspaceDirectory"/>; throws <see cref="InvalidOperationException"/> otherwise.
+    /// </summary>
+    public static string ResolveCorpusOption(string corpus, string workspaceDirectory)
+    {
+        ArgumentNullException.ThrowIfNull(corpus);
+        const string Prefix = "frozen:";
+        if (!corpus.StartsWith(Prefix, StringComparison.Ordinal))
+        {
+            throw new InvalidOperationException("--corpus takes frozen:<version>, e.g. frozen:v1.");
+        }
+
+        var store = new FrozenCorpusStore(Path.GetDirectoryName(Path.GetFullPath(workspaceDirectory.TrimEnd('/', '\\')))!);
+        var version = corpus[Prefix.Length..];
+        return store.Read(version) is not null
+            ? store.DirectoryOf(version)
+            : throw new InvalidOperationException($"No frozen corpus {version}: list them with 'eval-corpus list'.");
+    }
+
     /// <summary>Reserves the next version name (never reused, even after a version was destroyed).</summary>
     public string ReserveNextVersion()
     {
         lock (this.sync)
```

`src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs
+++ b/src/Cortex.Contained.Agent.Host/History/Evaluation/HistoryEvalCli.cs
@@ -1,6 +1,7 @@
 using System.Globalization;
 using Cortex.Contained.Agent.Host.Evaluation;
+using Cortex.Contained.Agent.Host.Evaluation.Frozen;
 using Cortex.Contained.Agent.Host.Reranking;
 using Cortex.Contained.Agent.Host.Reranking.Evaluation;
 using MemoryMcp.Core.Configuration;
 using MemoryMcp.Core.Services;
@@ -18,9 +19,9 @@ namespace Cortex.Contained.Agent.Host.History.Evaluation;
 public static class HistoryEvalCli
 {
     private const string Usage =
         "usage: history-eval <init|validate|guard|register|ablate|holdout|kappa|rerank-curve|gate-r> [--dir /app/state/eval/hybrid] [--source /app/state/messages.db] " +
-        "[--tz <IANA zone, default: local>] [--split dev|test|all] [--embedder http://embeddings:11434] [--labels judge-agreement.csv]\n" +
+        "[--tz <IANA zone, default: local>] [--split dev|test|all] [--embedder http://embeddings:11434] [--labels judge-agreement.csv] [--corpus frozen:v1]\n" +
         "rerank-curve [--condition normal|embedder-busy|cold] [--base CONFIG] [--reranker http://reranker:8080] [--timeout-ms 30000]; " +
         "gate-r --config CONFIG [--timeout-ms 2000]; both read the reranker key from Reranker__ApiKey.";
 
     /// <summary>Minimum LLM-vs-human agreement for LLM relevance labels (§4.2).</summary>
@@ -123,8 +124,21 @@ public static class HistoryEvalCli
             await output.WriteLineAsync(warning).ConfigureAwait(false);
         }
         try
         {
+            if (Option(args, "--corpus") is { } corpus)
+            {
+                // The sanitised frozen copy (§4.2): its own registered workspace and corpus, read with the evaluation key.
+                if (args[0] is "init" or "register")
+                {
+                    throw new InvalidOperationException($"'{args[0]}' does not apply to a frozen corpus: it was registered when it was frozen.");
+                }
+
+                var version = FrozenCorpusStore.ResolveCorpusOption(corpus, workspace.Directory);
+                workspace = new EvalWorkspace(Path.Combine(version, "history"), (protection ?? EvalProtection.Plaintext).ForFrozenSource());
+                source = Path.Combine(version, "messages.db");
+            }
+
             switch (args[0])
             {
                 case "init":
                     Init(workspace);
```

`src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCli.cs
@@ -1,8 +1,9 @@
 using System.Globalization;
 using System.Runtime.InteropServices;
 using System.Text.Json;
 using Cortex.Contained.Agent.Host.Evaluation;
+using Cortex.Contained.Agent.Host.Evaluation.Frozen;
 using Cortex.Contained.Agent.Host.Reranking;
 using Cortex.Contained.Agent.Host.Reranking.Evaluation;
 using Cortex.Contained.Contracts.Config.Yaml;
 using MemoryMcp.Core.Configuration;
@@ -23,9 +24,9 @@ namespace Cortex.Contained.Agent.Host.Memory.Evaluation;
 public static class MemoryEvalCli
 {
     private const string Usage =
         "usage: memory-eval <init|sample|validate|guard|pool|register|ablate|holdout|kappa|rerank-curve|gate-r> [--dir /app/state/eval/memory] " +
-        "[--source /app/state/memory] [--count 300] [--depth 10] [--split dev|test|all] [--embedder http://embeddings:11434] [--labels judge-agreement.csv]\n" +
+        "[--source /app/state/memory] [--count 300] [--depth 10] [--split dev|test|all] [--embedder http://embeddings:11434] [--labels judge-agreement.csv] [--corpus frozen:v1]\n" +
         "rerank-curve [--condition normal|embedder-busy|cold] [--base CONFIG] [--reranker http://reranker:8080] [--timeout-ms 30000]; " +
         "gate-r --config CONFIG [--timeout-ms 2000]; both read the reranker key from Reranker__ApiKey.";
 
     /// <summary>Minimum LLM-vs-human agreement for LLM relevance labels.</summary>
@@ -141,8 +142,29 @@ public static class MemoryEvalCli
         }
 
         var workspace = new MemoryEvalWorkspace(Option(args, "--dir") ?? MemoryEvalWorkspace.DefaultDirectory, protection);
         Directory.CreateDirectory(workspace.Directory);
+        if (Option(args, "--corpus") is { } corpus)
+        {
+            // The sanitised frozen copy (§4.2): its own registered workspace, a store rebuilt from its sanitised memories.
+            try
+            {
+                if (args[0] is "init" or "register" or "sample" or "pool")
+                {
+                    throw new InvalidOperationException($"'{args[0]}' does not apply to a frozen corpus: its dataset was labelled and registered before it was frozen.");
+                }
+
+                var version = FrozenCorpusStore.ResolveCorpusOption(corpus, workspace.Directory);
+                workspace = new MemoryEvalWorkspace(
+                    Path.Combine(version, "memory"), (protection ?? EvalProtection.Plaintext).ForFrozenSource(), Path.Combine(version, FrozenCorpusBuilder.MemoriesFileName));
+            }
+            catch (InvalidOperationException ex)
+            {
+                await output.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
+                return 2;
+            }
+        }
+
         if (MemoryEvalCorpus.SweepAbandoned(workspace.RunsDirectory) is var swept and > 0)
         {
             await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                 $"Removed {swept} leftover raw evaluation copies from interrupted runs.")).ConfigureAwait(false);
@@ -434,13 +456,13 @@ public static class MemoryEvalCli
 
     private sealed record CommandContext(
         MemoryEvalWorkspace Workspace, MemoryMcpOptions Live, Func<MemoryMcpOptions, IEmbeddingService> EmbedderFactory, TextWriter Output)
     {
-        /// <summary>Opens a copy of the live store under <paramref name="runDirectory"/> (a name is placed under <c>runs/</c>).</summary>
+        /// <summary>Opens the corpus (live copy or frozen snapshot) under <paramref name="runDirectory"/> (a name is placed under <c>runs/</c>).</summary>
         public Task<MemoryEvalCorpus> OpenCorpusAsync(string runDirectory, CancellationToken cancellationToken)
         {
             var directory = Path.IsPathRooted(runDirectory) ? runDirectory : NewRunDirectory(this.Workspace, runDirectory);
-            return MemoryEvalCorpus.CreateAsync(this.Live.DataDirectory, directory, this.Live, this.EmbedderFactory(this.Live), cancellationToken, protection: this.Workspace.Protection);
+            return MemoryEvalCorpus.OpenForAsync(this.Workspace, this.Live, this.EmbedderFactory(this.Live), directory, cancellationToken);
         }
     }
 
     private sealed class StaticOptionsMonitor(MemoryMcpOptions value) : IOptionsMonitor<MemoryMcpOptions>
```

`src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalCorpus.cs
@@ -1,9 +1,11 @@
 using System.Diagnostics;
+using System.Text.Json;
 using Cortex.Contained.Agent.Host.Evaluation;
 using Cortex.Contained.Agent.Host.Reranking;
 using Cortex.Contained.Agent.Host.Storage;
 using MemoryMcp.Core.Configuration;
+using MemoryMcp.Core.Models;
 using MemoryMcp.Core.Services;
 using MemoryMcp.Core.Storage;
 using Microsoft.Data.Sqlite;
 using Microsoft.Extensions.Logging.Abstractions;
@@ -25,8 +27,10 @@ public sealed class MemoryEvalCorpus : IAsyncDisposable
     public const int ResultLimit = 10;
 
     private const string LockFileName = "corpus.lock";
 
+    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
+
     private readonly string corpusDirectory;
     private readonly FileStream runLock;
     private readonly MemoryMcpOptions options;
     private readonly SqliteVecMemoryStore store;
@@ -118,12 +122,87 @@ public sealed class MemoryEvalCorpus : IAsyncDisposable
         DataDirectory = corpusDirectory,
         DatabaseFileName = live.DatabaseFileName,
         MemoriesSubdirectory = live.MemoriesSubdirectory,
         SearchMaxContentLength = live.SearchMaxContentLength,
+        ChunkSizeWords = live.ChunkSizeWords,
+        ChunkOverlapWords = live.ChunkOverlapWords,
         Ollama = live.Ollama,
         Search = live.Search,
     };
 
+    /// <summary>
+    /// The corpus of <paramref name="workspace"/>: a copy of the live store, or — for a frozen corpus workspace
+    /// (<see cref="MemoryEvalWorkspace.Snapshot"/>) — a store built from its sanitised memories.
+    /// </summary>
+    public static Task<MemoryEvalCorpus> OpenForAsync(
+        MemoryEvalWorkspace workspace, MemoryMcpOptions live, IEmbeddingService embedding, string runDirectory, CancellationToken cancellationToken, IRerankStage? reranker = null)
+    {
+        ArgumentNullException.ThrowIfNull(workspace);
+        ArgumentNullException.ThrowIfNull(live);
+        return workspace.Snapshot is { } snapshot
+            ? CreateFromSnapshotAsync(snapshot, runDirectory, live, embedding, cancellationToken, reranker, workspace.Protection)
+            : CreateAsync(live.DataDirectory, runDirectory, live, embedding, cancellationToken, reranker, workspace.Protection);
+    }
+
+    /// <summary>
+    /// Builds a private store in <paramref name="runDirectory"/><c>/corpus</c> from sanitised memories (one
+    /// <see cref="MemorySeedSource"/> per line of <paramref name="snapshotPath"/>, sealed on an encrypted install):
+    /// chunked like the live store and embedded with <paramref name="embedding"/>, ids unchanged so labels apply.
+    /// </summary>
+    public static async Task<MemoryEvalCorpus> CreateFromSnapshotAsync(
+        string snapshotPath,
+        string runDirectory,
+        MemoryMcpOptions live,
+        IEmbeddingService embedding,
+        CancellationToken cancellationToken,
+        IRerankStage? reranker = null,
+        EvalProtection? protection = null)
+    {
+        ArgumentNullException.ThrowIfNull(live);
+        ArgumentNullException.ThrowIfNull(embedding);
+        protection ??= EvalProtection.Plaintext;
+        var memories = protection.ReadLines(snapshotPath)
+            .Select(line => JsonSerializer.Deserialize<MemorySeedSource>(line, SnapshotJson) ?? throw new InvalidDataException($"Bad line in {snapshotPath}"))
+            .ToList();
+        var corpusDirectory = Path.Combine(runDirectory, "corpus");
+        var options = CopyOptions(corpusDirectory, live);
+        Directory.CreateDirectory(runDirectory);
+        var runLock = new FileStream(Path.Combine(runDirectory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
+        try
+        {
+            Directory.CreateDirectory(options.MemoriesDirectory);
+            using var store = new SqliteVecMemoryStore(
+                Options.Create(options), protection.CopyContent(), NullLogger<SqliteVecMemoryStore>.Instance, protection.CopyPassword(EncryptedMemoryStore.DatabasePurpose));
+            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
+            var chunker = new WordChunkingService(Options.Create(options));
+            foreach (var memory in memories)
+            {
+                var chunks = chunker.Chunk(memory.Content);
+                var vectors = await embedding.EmbedBatchAsync(chunks.Select(c => c.Text), cancellationToken).ConfigureAwait(false);
+                var records = chunks.Select(c => new ChunkRecord
+                {
+                    MemoryId = memory.MemoryId,
+                    ChunkIndex = c.ChunkIndex,
+                    StartOffset = c.StartOffset,
+                    Length = c.Length,
+                    Title = memory.Title,
+                    Tags = [.. memory.Tags],
+                    CreatedAt = DateTimeOffset.UnixEpoch,
+                    UpdatedAt = DateTimeOffset.UnixEpoch,
+                }).ToList();
+                await store.StoreMemoryAsync(memory.MemoryId, memory.Content, records, [.. vectors], cancellationToken).ConfigureAwait(false);
+            }
+        }
+        catch
+        {
+            DeleteCopy(corpusDirectory);
+            runLock.Dispose();
+            throw;
+        }
+
+        return await OpenAsync(corpusDirectory, runLock, options, embedding, reranker, protection, cancellationToken).ConfigureAwait(false);
+    }
+
     private static async Task<MemoryEvalCorpus> OpenAsync(
         string corpusDirectory, FileStream runLock, MemoryMcpOptions options, IEmbeddingService embedding, IRerankStage? reranker, EvalProtection protection, CancellationToken cancellationToken)
     {
         var password = protection.CopyPassword(EncryptedMemoryStore.DatabasePurpose);
```

`src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalWorkspace.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalWorkspace.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryEvalWorkspace.cs
@@ -43,21 +43,25 @@ public sealed class MemoryEvalWorkspace
         DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
         Converters = { new JsonStringEnumConverter() },
     };
 
-    public MemoryEvalWorkspace(string directory, EvalProtection? protection = null)
+    public MemoryEvalWorkspace(string directory, EvalProtection? protection = null, string? snapshot = null)
     {
         ArgumentException.ThrowIfNullOrWhiteSpace(directory);
         this.Directory = directory;
         this.Protection = protection ?? EvalProtection.Plaintext;
+        this.Snapshot = snapshot;
     }
 
     /// <summary>Workspace root.</summary>
     public string Directory { get; }
 
     /// <summary>How the dataset and seeds are sealed and the corpus copies are encrypted.</summary>
     public EvalProtection Protection { get; }
 
+    /// <summary>The sanitised memories of a frozen corpus this workspace evaluates, or null for the live store.</summary>
+    public string? Snapshot { get; }
+
     /// <summary>Labelled queries (<see cref="MemoryEvalQuery"/> per line; sealed on an encrypted install).</summary>
     public string DatasetPath => Path.Combine(this.Directory, "dataset.jsonl");
 
     /// <summary>Query-authoring seeds (raw memory text, sealed on an encrypted install; delete after authoring).</summary>
```

`src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryRerankEval.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryRerankEval.cs
+++ b/src/Cortex.Contained.Agent.Host/Memory/Evaluation/MemoryRerankEval.cs
@@ -50,9 +50,9 @@ internal static class MemoryRerankEval
         IReadOnlyList<RerankCurveRow> rows;
         AblationReport report;
         int? loadCompleted = null;
         int? loadFailed = null;
-        await using (var corpus = await MemoryEvalCorpus.CreateAsync(live.DataDirectory, runDir, live, embedderFactory(live), cancellationToken, stage, workspace.Protection).ConfigureAwait(false))
+        await using (var corpus = await MemoryEvalCorpus.OpenForAsync(workspace, live, embedderFactory(live), runDir, cancellationToken, stage).ConfigureAwait(false))
         {
             if (condition == RerankConditions.Cold)
             {
                 readyMs = await WaitReadyAsync(reranker, cancellationToken).ConfigureAwait(false);
@@ -109,9 +109,9 @@ internal static class MemoryRerankEval
 
         var runDir = NewRunDirectory(workspace, "gate-r");
         AblationReport report;
         var busy = new List<double>();
-        await using (var corpus = await MemoryEvalCorpus.CreateAsync(live.DataDirectory, runDir, live, embedderFactory(live), cancellationToken, stage, workspace.Protection).ConfigureAwait(false))
+        await using (var corpus = await MemoryEvalCorpus.OpenForAsync(workspace, live, embedderFactory(live), runDir, cancellationToken, stage).ConfigureAwait(false))
         {
             report = await MemoryAblationRunner.RunAsync([unreranked, candidate], queries, corpus.SearchAsync, unreranked.Name, cancellationToken).ConfigureAwait(false);
             await using (StartEmbedderLoad(embedderFactory, live))
             {
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~FrozenCorpusRunTests|FullyQualifiedName~MemoryEvalCorpusTests|FullyQualifiedName~MemoryEvalCliTests|FullyQualifiedName~HistoryEvalCliTests|FullyQualifiedName~MemoryRerankEvalTests|FullyQualifiedName~HistoryRerankEvalTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 36 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Evaluation\Frozen\FrozenCorpusStore.cs src\Cortex.Contained.Agent.Host\History\Evaluation\HistoryEvalCli.cs src\Cortex.Contained.Agent.Host\Memory\Evaluation\MemoryEvalCli.cs src\Cortex.Contained.Agent.Host\Memory\Evaluation\MemoryEvalCorpus.cs src\Cortex.Contained.Agent.Host\Memory\Evaluation\MemoryEvalWorkspace.cs src\Cortex.Contained.Agent.Host\Memory\Evaluation\MemoryRerankEval.cs tests\Cortex.Contained.Agent.Host.Tests\Evaluation\Frozen\FrozenCorpusRunTests.cs
git commit -m "feat(eval): evaluate frozen sanitised corpora" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 18: `eval-corpus` command and the raw-vs-sanitised report

`eval-corpus suggest|check|freeze|list|export|uploaded|import|compare|delete` as an evaluation job. `compare` pairs the newest raw holdout with the newest sanitised one and writes `/app/state/eval/compare/*.md|json` (E14). `uploaded` records the SafeExchange `contentId` after checking the SHA-256 (E12).

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs`
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Frozen/EvalCorpusCli.cs`
- Create: `src/Cortex.Contained.Agent.Host/Evaluation/Frozen/SanitisedComparison.cs`
- Test (new): `tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Frozen/EvalCorpusCliTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Evaluation/Frozen/EvalCorpusCliTests.cs` (new):

```csharp
using Cortex.Contained.Agent.Host.Evaluation;
using Cortex.Contained.Agent.Host.Evaluation.Frozen;
using MemoryMcp.Core.Configuration;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Frozen;

/// <summary>The eval-corpus job command end to end: freeze, export, record the upload, compare, delete.</summary>
public sealed class EvalCorpusCliTests : IAsyncDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"eval-corpus-{Guid.NewGuid():N}");
    private readonly EvalProtection protection = EvalProtection.Encrypted(EvalTestKeys.Ring());

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(this.root, recursive: true); } catch (IOException) { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    private string State => Path.Combine(this.root, "state");

    private EvalCorpusContext Context(FreezeSources sources) => new(
        Path.Combine(this.State, "eval"), sources.MessagesDb, new MemoryMcpOptions { DataDirectory = Path.Combine(this.State, "memory") },
        sources.History.Directory, sources.MemoryWorkspace.Directory, TimeProvider.System);

    private async Task<(int Code, string Output)> RunAsync(FreezeSources sources, string? passphrase, params string[] args)
    {
        using var output = new StringWriter();
        var code = await EvalCorpusCli.RunAsync(new EvaluationRun(args, output, this.protection, passphrase), this.Context(sources), CancellationToken.None);
        return (code, output.ToString());
    }

    [Fact]
    public async Task Workflow_FreezeExportUploadCompareDelete()
    {
        var sources = await FrozenCorpusBuilderTests.SeedAsync(this.State, EvalTestKeys.Ring(), this.protection);

        Assert.Equal(0, (await this.RunAsync(sources, null, "freeze")).Code);
        Assert.Equal(2, (await this.RunAsync(sources, null, "export", "--version", "v1")).Code);
        var export = await this.RunAsync(sources, Passphrase, "export", "--version", "v1");
        Assert.Equal(0, export.Code);
        var sha = new FrozenCorpusStore(Path.Combine(this.State, "eval")).Read("v1")!.PackageSha256!;
        Assert.Contains(sha, export.Output, StringComparison.Ordinal);
        Assert.Equal(2, (await this.RunAsync(sources, null, "uploaded", "--version", "v1", "--content-id", "c1", "--sha256", new string('0', 64))).Code);
        Assert.Equal(0, (await this.RunAsync(sources, null, "uploaded", "--version", "v1", "--content-id", "c1", "--sha256", sha)).Code);

        using (var output = new StringWriter())
        {
            Assert.Equal(0, await FrozenCorpusRunTests.RunHistoryAsync(sources, this.protection, output, "holdout"));
            Assert.Equal(0, await FrozenCorpusRunTests.RunHistoryAsync(sources, this.protection, output, "holdout", "--corpus", "frozen:v1"));
        }

        var compare = await this.RunAsync(sources, null, "compare", "--version", "v1");
        Assert.Equal(0, compare.Code);
        Assert.Contains("| hybrid-rrf60 |", compare.Output, StringComparison.Ordinal);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(this.State, "eval", "compare"), "*.md"));

        var delete = await this.RunAsync(sources, null, "delete", "--version", "v1");
        Assert.Equal(0, delete.Code);
        Assert.Contains("cortex-eval-corpus-v1.cxpkg", delete.Output, StringComparison.Ordinal);
        var list = await this.RunAsync(sources, null, "list");
        Assert.Contains("PENDING off-machine deletion: v1", list.Output, StringComparison.Ordinal);
        Assert.Contains("contentId c1", list.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Freeze_LeakFound_RefusedWithLocationsAndKindsOnly()
    {
        var sources = await FrozenCorpusBuilderTests.SeedAsync(this.State, EvalTestKeys.Ring(), this.protection, extraText: "and Krôllvik");

        var freeze = await this.RunAsync(sources, null, "freeze");

        Assert.Equal(1, freeze.Code);
        Assert.Contains("leak: messages.Content dictionary-term ×1", freeze.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Krôllvik", freeze.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_TermAddedAfterFreeze_FoundInTheVersion()
    {
        var sources = await FrozenCorpusBuilderTests.SeedAsync(this.State, EvalTestKeys.Ring(), this.protection, extraText: "with Dorvin");
        Assert.Equal(0, (await this.RunAsync(sources, null, "freeze")).Code);
        this.protection.WriteLines(sources.LexiconPath, [.. this.protection.ReadLines(sources.LexiconPath), """{"term":"Dorvin","kind":"person"}"""]);

        var check = await this.RunAsync(sources, null, "check", "--version", "v1");

        Assert.Equal(1, check.Code);
        Assert.Contains("finding: messages.Content dictionary-term ×1", check.Output, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~EvalCorpusCliTests" -v q`
Expected: build FAILS — `The type or namespace name 'EvalCorpusContext' could not be found` (and `EvalCorpusCli`).

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs
+++ b/src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs
@@ -1,10 +1,12 @@
+using Cortex.Contained.Agent.Host.Evaluation.Frozen;
 using Cortex.Contained.Agent.Host.History;
 using Cortex.Contained.Agent.Host.History.Evaluation;
 using Cortex.Contained.Agent.Host.Memory.Evaluation;
 using Cortex.Contained.Agent.Host.Reranking;
 using Cortex.Contained.Agent.Host.Reranking.Evaluation;
 using Cortex.Contained.Agent.Host.Storage;
+using Cortex.Contained.Contracts.Llm;
 using MemoryMcp.Core.Configuration;
 using Microsoft.Extensions.Options;
 
 namespace Cortex.Contained.Agent.Host.Evaluation;
@@ -27,8 +29,20 @@ public static class EvaluationRegistration
             {
                 ["history-eval"] = (run, ct) => HistoryEvalCli.RunInAgentAsync(
                     run.Args, run.Output, run.Protection, history.CurrentValue, memory.CurrentValue, encryption.Layout.MessagesDatabase, reranker, ct),
                 ["memory-eval"] = (run, ct) => MemoryEvalCli.RunInAgentAsync(run.Args, run.Output, run.Protection, memory.CurrentValue, reranker, ct),
+                ["eval-corpus"] = (run, ct) => EvalCorpusCli.RunAsync(
+                    run,
+                    new EvalCorpusContext(
+                        encryption.Layout.EvaluationDirectory,
+                        encryption.Layout.MessagesDatabase,
+                        memory.CurrentValue,
+                        history.CurrentValue.EvalArtifactsDirectory,
+                        MemoryEvalWorkspace.DefaultDirectory,
+                        sp.GetRequiredService<TimeProvider>(),
+                        sp.GetService<ILlmClient>(),
+                        sp.GetService<Agent.IModelProvider>()?.MemoryModel),
+                    ct),
             };
             return new EvaluationJobService(
                 commands,
                 EvaluationJobService.ProtectionFor(encryption, sp.GetRequiredService<StorageKeyRing>()),
```

`src/Cortex.Contained.Agent.Host/Evaluation/Frozen/EvalCorpusCli.cs` (new):

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cortex.Contained.Agent.Host.Evaluation.Sanitising;
using Cortex.Contained.Agent.Host.History.Evaluation;
using Cortex.Contained.Agent.Host.Memory.Evaluation;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Llm;
using Cortex.Contained.Contracts.Security;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Evaluation;
using Microsoft.Data.Sqlite;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>What <c>eval-corpus</c> works on: the live stores, the two workspaces and the optional LLM for suggestions.</summary>
public sealed record EvalCorpusContext(
    string EvaluationDirectory,
    string MessagesDb,
    MemoryMcpOptions Memory,
    string HistoryWorkspace,
    string MemoryWorkspace,
    TimeProvider Time,
    ILlmClient? Llm = null,
    string? LlmModel = null);

/// <summary>
/// The frozen evaluation corpus command (run as an evaluation job inside the agent):
/// <c>eval-corpus &lt;suggest|check|freeze|list|export|uploaded|import|compare|delete&gt;</c>. Output holds
/// versions, counts, hashes and paths only.
/// </summary>
public static class EvalCorpusCli
{
    /// <summary>Usage text.</summary>
    public const string Usage =
        "usage: eval-corpus <suggest [--llm] [--max-batches 50] | check --version vN | freeze | list | export --version vN | " +
        "uploaded --version vN --content-id ID --sha256 HEX | import --package FILE.cxpkg | compare --version vN [--tool history-eval|memory-eval] [--raw RUN] [--sanitised RUN] | delete --version vN>\n" +
        "export and import need the package passphrase (Invoke-CortexEval.ps1 -PromptPassphrase).";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Runs one command; returns the exit code.</summary>
    public static async Task<int> RunAsync(EvaluationRun run, EvalCorpusContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(context);
        var output = run.Output;
        if (run.Args.Count == 0)
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        var store = new FrozenCorpusStore(context.EvaluationDirectory);
        var corpusDirectory = Path.Combine(context.EvaluationDirectory, "corpus");
        var lexiconPath = Path.Combine(corpusDirectory, "dictionary.jsonl");
        try
        {
            switch (run.Args[0])
            {
                case "suggest":
                    return await SuggestAsync(run, context, lexiconPath, Path.Combine(corpusDirectory, "dictionary.suggested.jsonl"), cancellationToken).ConfigureAwait(false);
                case "check":
                    return await CheckAsync(run, store, lexiconPath).ConfigureAwait(false);
                case "freeze":
                    return await FreezeAsync(run, context, store, lexiconPath, cancellationToken).ConfigureAwait(false);
                case "list":
                    foreach (var m in store.List())
                    {
                        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"{m.Version}  {m.CreatedAt:yyyy-MM-dd}  range {m.CorpusFrom:yyyy-MM-dd}..{m.CorpusTo:yyyy-MM-dd}  messages {m.Messages}  memories {m.Memories}  queries {m.HistoryQueries}/{m.MemoryQueries}  exported {(m.ExportedAt is { } e ? e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "no")}  uploaded {(m.Offsite is null ? "no" : "yes")}{(m.Imported ? "  imported" : string.Empty)}")).ConfigureAwait(false);
                    }

                    foreach (var pending in store.PendingDeletions())
                    {
                        await output.WriteLineAsync($"PENDING off-machine deletion: {pending.Version} — delete file {pending.PackageFileName} of SafeExchange secret {pending.Secret}{(pending.ContentId is null ? string.Empty : " (contentId " + pending.ContentId + ")")}.").ConfigureAwait(false);
                    }

                    return 0;
                case "export":
                    return await ExportAsync(run, store, context).ConfigureAwait(false);
                case "uploaded":
                    return await UploadedAsync(run, store, context).ConfigureAwait(false);
                case "import":
                    return await ImportAsync(run, store).ConfigureAwait(false);
                case "compare":
                    return await CompareAsync(run, store, context).ConfigureAwait(false);
                case "delete":
                    var version = Required(run.Args, "--version");
                    _ = store.Read(version) ?? throw new InvalidOperationException($"No frozen corpus {version}.");
                    var gone = store.Destroy(version, context.Time.GetUtcNow());
                    await output.WriteLineAsync($"{version} deleted{(gone ? string.Empty : " (some files could not be removed yet)")}. " + OffsiteReminder(store, version)).ConfigureAwait(false);
                    return gone ? 0 : 1;
                default:
                    await output.WriteLineAsync(Usage).ConfigureAwait(false);
                    return 2;
            }
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
            return 2;
        }
        catch (InvalidDataException ex)
        {
            await output.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
            return 2;
        }
    }

    private static async Task<int> SuggestAsync(EvaluationRun run, EvalCorpusContext context, string lexiconPath, string suggestedPath, CancellationToken cancellationToken)
    {
        var lexicon = File.Exists(lexiconPath) ? SanitiserLexicon.Parse(run.Protection.ReadLines(lexiconPath)) : SanitiserLexicon.Empty;
        var texts = ReadMessageTexts(context.MessagesDb, run.Protection.SourcePassword(MessageStore.PasswordPurpose));
        var memoryRun = Path.Combine(context.EvaluationDirectory, "corpus", "tmp-memory");
        if (File.Exists(context.Memory.DatabasePath))
        {
            await using var memories = await MemoryEvalCorpus.CreateAsync(context.Memory.DataDirectory, memoryRun, context.Memory, new FrozenCorpusBuilder.NoEmbedding(), cancellationToken, protection: run.Protection).ConfigureAwait(false);
            texts.AddRange((await memories.ListMemoriesAsync(cancellationToken).ConfigureAwait(false)).Select(m => m.Title + "\n" + string.Join(' ', m.Tags) + "\n" + m.Content));
        }

        var suggestions = LexiconSuggester.Capitalised(texts, lexicon).ToList();
        if (run.Args.Contains("--llm", StringComparer.Ordinal))
        {
            if (context.Llm is null || string.IsNullOrWhiteSpace(context.LlmModel))
            {
                throw new InvalidOperationException("No LLM provider is configured for this agent.");
            }

            var batches = Option(run.Args, "--max-batches") is { } text && int.TryParse(text, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 50;
            suggestions.AddRange(await LexiconSuggester.FromLlmAsync(texts, lexicon, context.Llm, context.LlmModel, batches, cancellationToken).ConfigureAwait(false));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(suggestedPath)!);
        run.Protection.WriteLines(suggestedPath, suggestions.Select(s => JsonSerializer.Serialize(s, JsonSerializerOptions.Web)));
        await run.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{suggestions.Count} suggestions ({suggestions.Count(s => s.Source == "llm")} from the LLM) in dictionary.suggested.jsonl. Review them (Edit-CortexEvalFile.ps1 -Tool eval-corpus -Name dictionary.suggested.jsonl -Pull) and move accepted terms into dictionary.jsonl.")).ConfigureAwait(false);
        if (Directory.Exists(memoryRun))
        {
            Directory.Delete(memoryRun, recursive: true);
        }

        return 0;
    }

    // A version frozen before the dictionary grew is checked again for the new terms (the other patterns were checked at freeze).
    private static async Task<int> CheckAsync(EvaluationRun run, FrozenCorpusStore store, string lexiconPath)
    {
        var version = Required(run.Args, "--version");
        var manifest = store.Read(version) ?? throw new InvalidOperationException($"No frozen corpus {version}.");
        var lexicon = File.Exists(lexiconPath) ? SanitiserLexicon.Parse(run.Protection.ReadLines(lexiconPath)) : SanitiserLexicon.Empty;
        var scanner = new LeakageScanner(lexicon, [], termsOnly: true);
        var protection = run.Protection.ForFrozenSource();
        var directory = store.DirectoryOf(version);
        foreach (var text in ReadMessageTexts(Path.Combine(directory, "messages.db"), protection.SourcePassword(MessageStore.PasswordPurpose)))
        {
            scanner.Scan("messages.Content", text);
        }

        foreach (var relative in new[] { FrozenCorpusBuilder.MemoriesFileName, "history/dataset.jsonl", "memory/dataset.jsonl" })
        {
            foreach (var line in protection.ReadLines(Path.Combine(directory, relative)))
            {
                scanner.Scan(relative, line);
            }
        }

        foreach (var finding in scanner.Findings)
        {
            await run.Output.WriteLineAsync($"finding: {finding.Location} {finding.Kind} ×{finding.Count}").ConfigureAwait(false);
        }

        await run.Output.WriteLineAsync(scanner.Passed
            ? $"{manifest.Version}: no dictionary term found in {scanner.Scanned} texts."
            : $"{manifest.Version} holds terms the dictionary now lists: delete it (eval-corpus delete --version {version}) and freeze again.").ConfigureAwait(false);
        return scanner.Passed ? 0 : 1;
    }

    private static async Task<int> FreezeAsync(EvaluationRun run, EvalCorpusContext context, FrozenCorpusStore store, string lexiconPath, CancellationToken cancellationToken)
    {
        var sources = new FreezeSources(
            context.MessagesDb,
            context.Memory,
            new EvalWorkspace(context.HistoryWorkspace, run.Protection),
            new MemoryEvalWorkspace(context.MemoryWorkspace, run.Protection),
            lexiconPath);
        var result = await FrozenCorpusBuilder.FreezeAsync(sources, store, run.Protection, context.Time, cancellationToken).ConfigureAwait(false);
        if (result.Manifest is not { } manifest)
        {
            foreach (var finding in result.Findings)
            {
                await run.Output.WriteLineAsync($"leak: {finding.Location} {finding.Kind} ×{finding.Count}").ConfigureAwait(false);
            }

            await run.Output.WriteLineAsync(
                "Freeze refused: personal values remain after sanitising (nothing was kept). Add the missing spellings to dictionary.jsonl " +
                "(eval-corpus suggest helps find them) and freeze again.").ConfigureAwait(false);
            return 1;
        }

        await run.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Frozen {manifest.Version}: {manifest.Messages} messages, {manifest.Memories} memories, {manifest.HistoryQueries} history and {manifest.MemoryQueries} memory queries; leakage check passed on {manifest.LeakageTextsScanned} texts. Next: eval-corpus export --version {manifest.Version}.")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ExportAsync(EvaluationRun run, FrozenCorpusStore store, EvalCorpusContext context)
    {
        var version = Required(run.Args, "--version");
        if (run.Passphrase is not { Length: >= PassphraseKdf.MinimumPassphraseLength } passphrase)
        {
            throw new InvalidOperationException($"export needs a package passphrase of at least {PassphraseKdf.MinimumPassphraseLength} characters.");
        }

        var package = FrozenCorpusPackage.Export(store, version, run.Protection, passphrase, context.Time);
        await run.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Package {package.FileName}: {package.Length} bytes, sha256 {package.Sha256}. Download it from the Bridge (Export-CortexEvalCorpus.ps1), attach it to the SafeExchange secret {OffsiteCopy.SecretName} under that file name, then record the upload: eval-corpus uploaded --version {version} --content-id <id> --sha256 {package.Sha256}.")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> UploadedAsync(EvaluationRun run, FrozenCorpusStore store, EvalCorpusContext context)
    {
        var version = Required(run.Args, "--version");
        var manifest = store.Read(version) ?? throw new InvalidOperationException($"No frozen corpus {version}.");
        var sha256 = Required(run.Args, "--sha256").ToLowerInvariant();
        if (!string.Equals(manifest.PackageSha256, sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The uploaded file's SHA-256 is not the exported package's: upload the package again.");
        }

        (manifest with { Offsite = new OffsiteCopy { ContentId = Required(run.Args, "--content-id"), Sha256 = sha256, UploadedAt = context.Time.GetUtcNow() } })
            .Write(Path.Combine(store.DirectoryOf(version), FrozenCorpusManifest.FileName));
        await run.Output.WriteLineAsync($"Recorded: {version} is attached to {OffsiteCopy.SecretName}.").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ImportAsync(EvaluationRun run, FrozenCorpusStore store)
    {
        var name = Path.GetFileName(Required(run.Args, "--package"));
        if (!name.EndsWith(".cxpkg", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("--package takes the file name of a .cxpkg in the packages directory.");
        }

        if (run.Passphrase is not { Length: > 0 } passphrase)
        {
            throw new InvalidOperationException("import needs the package passphrase.");
        }

        var manifest = FrozenCorpusPackage.Import(store, Path.Combine(store.PackagesDirectory, name), run.Protection, passphrase);
        await run.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Imported {manifest.Version}: {manifest.Messages} messages, {manifest.Memories} memories; every file matched the package manifest. Evaluate it with --corpus frozen:{manifest.Version}.")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> CompareAsync(EvaluationRun run, FrozenCorpusStore store, EvalCorpusContext context)
    {
        var version = Required(run.Args, "--version");
        _ = store.Read(version) ?? throw new InvalidOperationException($"No frozen corpus {version}.");
        var tool = Option(run.Args, "--tool") ?? "history-eval";
        var (rawWorkspace, sanitisedWorkspace) = tool switch
        {
            "history-eval" => (context.HistoryWorkspace, Path.Combine(store.DirectoryOf(version), "history")),
            "memory-eval" => (context.MemoryWorkspace, Path.Combine(store.DirectoryOf(version), "memory")),
            _ => throw new InvalidOperationException("--tool takes history-eval or memory-eval."),
        };
        var rawRun = RunDirectory(Path.Combine(rawWorkspace, "runs"), Option(run.Args, "--raw"));
        var sanitisedRun = RunDirectory(Path.Combine(sanitisedWorkspace, "runs"), Option(run.Args, "--sanitised"));
        var raw = ReadReport(rawRun);
        var sanitised = ReadReport(sanitisedRun);
        var configs = SanitisedComparison.Configs(raw, sanitised);
        var comparisons = SanitisedComparison.Comparisons(raw, sanitised);
        var directory = Path.Combine(context.EvaluationDirectory, "compare");
        Directory.CreateDirectory(directory);
        var name = string.Create(CultureInfo.InvariantCulture, $"{tool}-{version}-{context.Time.GetUtcNow():yyyyMMdd-HHmmss}");
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new { tool, version, raw = Path.GetFileName(rawRun), sanitised = Path.GetFileName(sanitisedRun), configs, comparisons }, Json)).ConfigureAwait(false);
        var report = SanitisedComparison.Markdown($"{tool} {Path.GetFileName(rawRun)} vs {version}/{Path.GetFileName(sanitisedRun)}", configs, comparisons);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".md"), report).ConfigureAwait(false);
        await run.Output.WriteLineAsync(report).ConfigureAwait(false);
        await run.Output.WriteLineAsync($"Report: {Path.Combine(directory, name + ".md")}").ConfigureAwait(false);
        return 0;
    }

    // The named run, or the newest holdout run.
    private static string RunDirectory(string runs, string? name)
    {
        if (name is not null)
        {
            var path = Path.Combine(runs, Path.GetFileName(name));
            return File.Exists(Path.Combine(path, "results.json")) ? path : throw new InvalidOperationException($"No results.json in run {Path.GetFileName(name)}.");
        }

        return (Directory.Exists(runs) ? Directory.EnumerateDirectories(runs, "holdout-*") : [])
            .Where(d => File.Exists(Path.Combine(d, "results.json")))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .FirstOrDefault() ?? throw new InvalidOperationException($"No holdout run with results in {runs}: run holdout first (and on the frozen copy with --corpus frozen:<version>).");
    }

    private static AblationReport ReadReport(string run) =>
        JsonSerializer.Deserialize<AblationReport>(File.ReadAllText(Path.Combine(run, "results.json")), Json)
        ?? throw new InvalidDataException($"{Path.GetFileName(run)}/results.json is empty.");

    private static List<string> ReadMessageTexts(string db, string? password)
    {
        var texts = new List<string>();
        if (!File.Exists(db))
        {
            return texts;
        }

        using var connection = new SqliteConnection(EncryptedSqlite.ConnectionString(db, password, SqliteOpenMode.ReadOnly, pooling: false));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Content FROM Messages;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            texts.Add(reader.GetString(0));
        }

        return texts;
    }

    private static string OffsiteReminder(FrozenCorpusStore store, string version) =>
        store.PendingDeletions().FirstOrDefault(p => p.Version == version) is { } pending
            ? $"Its off-machine copy must be deleted too: file {pending.PackageFileName} of SafeExchange secret {pending.Secret}; then acknowledge it on the Recall page."
            : "It was never exported.";

    private static string Required(IReadOnlyList<string> args, string name) =>
        Option(args, name) ?? throw new InvalidOperationException($"{name} is required.");

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
```

`src/Cortex.Contained.Agent.Host/Evaluation/Frozen/SanitisedComparison.cs` (new):

```csharp
using System.Globalization;
using System.Text;
using MemoryMcp.Core.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>Raw-vs-sanitised difference of one configuration (§4.2: "report both and the difference").</summary>
public sealed record ConfigDifference(
    string Config,
    int Queries,
    double RawNdcg10,
    double SanitisedNdcg10,
    double DeltaNdcg10,
    double DeltaLow,
    double DeltaHigh,
    double RawRecall10,
    double SanitisedRecall10,
    double RawMrr,
    double SanitisedMrr);

/// <summary>Raw-vs-sanitised result of one paired comparison (e.g. the holdout configuration against the baseline).</summary>
public sealed record ComparisonDifference(string Config, string Baseline, string Slice, double RawMean, double RawLow, double RawHigh, double SanitisedMean, double SanitisedLow, double SanitisedHigh);

/// <summary>
/// Compares a run on the raw local data with the same run on the sanitised frozen copy: per configuration the
/// metrics of both and the paired per-query nDCG@10 difference (sanitised − raw, bootstrap 95% CI over the queries
/// both runs scored), and per paired comparison both confidence intervals, so the owner sees whether sanitising
/// changes the Gate H conclusion. Ids and numbers only.
/// </summary>
public static class SanitisedComparison
{
    /// <summary>Per-configuration differences, by configuration name.</summary>
    public static IReadOnlyList<ConfigDifference> Configs(AblationReport raw, AblationReport sanitised)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(sanitised);
        var result = new List<ConfigDifference>();
        foreach (var config in raw.Summaries.Select(s => s.Config).Intersect(sanitised.Summaries.Select(s => s.Config), StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var rawScores = raw.Scores.Where(s => s.Config == config).ToDictionary(s => s.QueryId, StringComparer.Ordinal);
            var paired = sanitised.Scores.Where(s => s.Config == config && rawScores.ContainsKey(s.QueryId)).OrderBy(s => s.QueryId, StringComparer.Ordinal).ToList();
            var sanitisedNdcg = paired.Select(s => s.Ndcg10).ToList();
            var rawNdcg = paired.Select(s => rawScores[s.QueryId].Ndcg10).ToList();
            var (mean, low, high) = EvalStatistics.PairedBootstrap(sanitisedNdcg, rawNdcg);
            var rawSummary = raw.Summaries.Single(s => s.Config == config);
            var sanitisedSummary = sanitised.Summaries.Single(s => s.Config == config);
            result.Add(new ConfigDifference(
                config, paired.Count, rawSummary.Ndcg10, sanitisedSummary.Ndcg10, mean, low, high,
                rawSummary.Recall10, sanitisedSummary.Recall10, rawSummary.Mrr, sanitisedSummary.Mrr));
        }

        return result;
    }

    /// <summary>The paired comparisons present in both runs.</summary>
    public static IReadOnlyList<ComparisonDifference> Comparisons(AblationReport raw, AblationReport sanitised)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(sanitised);
        return raw.Comparisons
            .Join(
                sanitised.Comparisons,
                r => (r.Config, r.Baseline, r.Slice),
                s => (s.Config, s.Baseline, s.Slice),
                (r, s) => new ComparisonDifference(r.Config, r.Baseline, r.Slice, r.Mean, r.Low, r.High, s.Mean, s.Low, s.High))
            .ToList();
    }

    /// <summary>The report.</summary>
    public static string Markdown(string title, IReadOnlyList<ConfigDifference> configs, IReadOnlyList<ComparisonDifference> comparisons)
    {
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(comparisons);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Raw vs sanitised copy — {title}").AppendLine();
        text.AppendLine("| Config | Queries | nDCG@10 raw | nDCG@10 sanitised | Δ (sanitised − raw) [95% CI] | Recall@10 raw / sanitised | MRR raw / sanitised |");
        text.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var c in configs)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {c.Config} | {c.Queries} | {c.RawNdcg10:F3} | {c.SanitisedNdcg10:F3} | {c.DeltaNdcg10:+0.000;-0.000} [{c.DeltaLow:+0.000;-0.000}, {c.DeltaHigh:+0.000;-0.000}] | {c.RawRecall10:F3} / {c.SanitisedRecall10:F3} | {c.RawMrr:F3} / {c.SanitisedMrr:F3} |");
        }

        text.AppendLine().AppendLine("| Comparison | Slice | Raw Δ nDCG@10 [95% CI] | Sanitised Δ nDCG@10 [95% CI] |");
        text.AppendLine("|---|---|---|---|");
        foreach (var c in comparisons)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {c.Config} vs {c.Baseline} | {c.Slice} | {c.RawMean:+0.000;-0.000} [{c.RawLow:+0.000;-0.000}, {c.RawHigh:+0.000;-0.000}] | {c.SanitisedMean:+0.000;-0.000} [{c.SanitisedLow:+0.000;-0.000}, {c.SanitisedHigh:+0.000;-0.000}] |");
        }

        text.AppendLine().AppendLine("Gate decisions use the raw run (D6); the sanitised run shows how far the off-machine copy can stand in for it.");
        return text.ToString();
    }
}
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~EvalCorpusCliTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 3 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Evaluation\EvaluationRegistration.cs src\Cortex.Contained.Agent.Host\Evaluation\Frozen\EvalCorpusCli.cs src\Cortex.Contained.Agent.Host\Evaluation\Frozen\SanitisedComparison.cs tests\Cortex.Contained.Agent.Host.Tests\Evaluation\Frozen\EvalCorpusCliTests.cs
git commit -m "feat(eval): eval-corpus command and raw-vs-sanitised comparison" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 19: D3: purges destroy overlapping frozen versions

The `smoke-artifacts` erasure step also destroys every frozen version whose range overlaps the purge and every incomplete version directory, with its runs and package; exported versions enter the off-machine deletion ledger (E13). `FrozenCorpusStore` is registered in DI.

**Files:**
- Modify: `src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs`
- Modify: `src/Cortex.Contained.Agent.Host/History/ErasureStepHandlers.cs`
- Test (modify): `tests/Cortex.Contained.Agent.Host.Tests/History/ErasureStepTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/History/ErasureStepTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Agent.Host.Tests/History/ErasureStepTests.cs
+++ b/tests/Cortex.Contained.Agent.Host.Tests/History/ErasureStepTests.cs
@@ -97,8 +97,38 @@ public sealed class ErasureStepTests : IDisposable
         Assert.True(File.Exists(Path.Combine(eval, "dataset.jsonl")));
         Assert.True(File.Exists(Path.Combine(pool, "keep.txt")));
     }
 
+    [Fact]
+    public async Task FrozenCorpora_OverlappingVersionsDestroyedExportedOnesQueuedForOffsiteDeletion()
+    {
+        var store = new Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusStore(Path.Combine(this.dir, "state-eval"));
+        void Version(string name, DateTimeOffset from, DateTimeOffset to, bool exported)
+        {
+            Directory.CreateDirectory(store.DirectoryOf(name));
+            new Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusManifest { Version = name, CorpusFrom = from, CorpusTo = to, ExportedAt = exported ? T0 : null }
+                .Write(Path.Combine(store.DirectoryOf(name), "manifest.json"));
+        }
+
+        Version("v1", T0.AddDays(-10), T0.AddHours(2), exported: true);
+        Version("v2", T0.AddDays(-10), T0.AddHours(2), exported: false);
+        Version("v3", T0.AddDays(5), T0.AddDays(9), exported: true);
+        Directory.CreateDirectory(store.DirectoryOf(store.ReserveNextVersion()) + ".partial");
+        Directory.CreateDirectory(store.PackagesDirectory);
+        await File.WriteAllTextAsync(store.PackageOf("v1"), "package");
+        var monitor = Substitute.For<IOptionsMonitor<HistoryOptions>>();
+        monitor.CurrentValue.Returns(new HistoryOptions { SmokeArtifactsDirectory = Path.Combine(this.dir, "none"), EvalArtifactsDirectory = Path.Combine(this.dir, "none") });
+
+        Assert.True(await new SmokeArtifactErasureStep(monitor, store).RunAsync(Task(T0, T0.AddDays(1)), CancellationToken.None));
+
+        Assert.Equal(["v3"], store.List().Select(m => m.Version));
+        Assert.False(Directory.Exists(store.DirectoryOf("v4") + ".partial"));
+        Assert.False(File.Exists(store.PackageOf("v1")));
+        var pending = Assert.Single(store.PendingDeletions());
+        Assert.Equal(("v1", "cortex-recovery-kit", "cortex-eval-corpus-v1.cxpkg"), (pending.Version, pending.Secret, pending.PackageFileName));
+        Assert.Equal("v5", store.ReserveNextVersion());
+    }
+
     [Fact]
     public async Task SmokeArtifacts_RootWithoutRunsDirectory_DeletesNothing()
     {
         var root = Path.Combine(this.dir, "smoke");
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~ErasureStepTests|FullyQualifiedName~HistoryPurgeServiceTests" -v q`
Expected: build FAILS — `'SmokeArtifactErasureStep' does not contain a constructor that takes 2 arguments`.

- [ ] **Step 3: Implement**

`src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs
+++ b/src/Cortex.Contained.Agent.Host/Evaluation/EvaluationRegistration.cs
@@ -18,8 +18,9 @@ public static class EvaluationRegistration
     public static void AddEvaluation(IServiceCollection services, StorageEncryptionContext encryption)
     {
         ArgumentNullException.ThrowIfNull(services);
         ArgumentNullException.ThrowIfNull(encryption);
+        services.AddSingleton(new FrozenCorpusStore(encryption.Layout.EvaluationDirectory));
         services.AddSingleton(sp =>
         {
             var history = sp.GetRequiredService<IOptionsMonitor<HistoryOptions>>();
             var memory = sp.GetRequiredService<IOptionsMonitor<MemoryMcpOptions>>();
```

`src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs
+++ b/src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs
@@ -80,8 +80,40 @@ public sealed class FrozenCorpusStore
             ? store.DirectoryOf(version)
             : throw new InvalidOperationException($"No frozen corpus {version}: list them with 'eval-corpus list'.");
     }
 
+    /// <summary>
+    /// Versions a purge of <paramref name="from"/>..<paramref name="to"/> must destroy (D3): every version whose corpus
+    /// range overlaps, plus every version directory without a readable manifest (an interrupted freeze or import:
+    /// its range is unknown). Channel-scoped purges are treated like all-channel ones (a version spans every channel).
+    /// </summary>
+    public IReadOnlyList<string> Overlapping(DateTimeOffset? from, DateTimeOffset to)
+    {
+        if (!Directory.Exists(this.Root))
+        {
+            return [];
+        }
+
+        var result = new List<string>();
+        foreach (var directory in Directory.EnumerateDirectories(this.Root))
+        {
+            var name = Path.GetFileName(directory);
+            var version = name.EndsWith(".partial", StringComparison.Ordinal) ? name[..^".partial".Length] : name;
+            if (FrozenCorpusManifest.NumberOf(version) is null)
+            {
+                continue;
+            }
+
+            var manifest = name == version ? FrozenCorpusManifest.Read(Path.Combine(directory, FrozenCorpusManifest.FileName)) : null;
+            if (manifest is null || (manifest.CorpusFrom <= to && (from is null || manifest.CorpusTo >= from)))
+            {
+                result.Add(version);
+            }
+        }
+
+        return result.Distinct(StringComparer.Ordinal).ToList();
+    }
+
     /// <summary>Reserves the next version name (never reused, even after a version was destroyed).</summary>
     public string ReserveNextVersion()
     {
         lock (this.sync)
```

`src/Cortex.Contained.Agent.Host/History/ErasureStepHandlers.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/History/ErasureStepHandlers.cs
+++ b/src/Cortex.Contained.Agent.Host/History/ErasureStepHandlers.cs
@@ -141,11 +141,16 @@ public sealed class SubagentErasureStep(SubagentSessionStore store) : IErasureSt
 /// <c>history-eval</c> workspace (<c>EvalArtifactsDirectory</c>). Both hold raw copies of
 /// <c>messages.db</c> and index copies. A run directory without a readable <c>manifest.json</c> is
 /// destroyed too (the overlap cannot be determined). Only the children of <c>runs</c> are ever
 /// enumerated, so owner-authored files and directories in the workspace root (queries, dataset,
-/// analysis plan, pool) are kept.
+/// analysis plan, pool) are kept. With a <see cref="Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusStore"/>, frozen corpus
+/// versions whose range overlaps are destroyed as well (their runs and export package with them); an exported
+/// one is recorded for deletion of its off-machine copy, which the owner removes from SafeExchange.
 /// </summary>
-public sealed class SmokeArtifactErasureStep(IOptionsMonitor<HistoryOptions> options) : IErasureStep
+public sealed class SmokeArtifactErasureStep(
+    IOptionsMonitor<HistoryOptions> options,
+    Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusStore? frozen = null,
+    TimeProvider? time = null) : IErasureStep
 {
     private static readonly JsonSerializerOptions manifestJson = new(JsonSerializerDefaults.Web);
 
     /// <inheritdoc />
@@ -191,8 +196,16 @@ public sealed class SmokeArtifactErasureStep(IOptionsMonitor<HistoryOptions> opt
                 }
             }
         }
 
+        if (frozen is not null)
+        {
+            foreach (var version in frozen.Overlapping(task.RangeFrom, to))
+            {
+                allGone &= frozen.Destroy(version, (time ?? TimeProvider.System).GetUtcNow());
+            }
+        }
+
         return Task.FromResult(allGone);
     }
 
     private static bool Overlaps(string manifestPath, DateTimeOffset? from, DateTimeOffset to)
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~ErasureStepTests|FullyQualifiedName~HistoryPurgeServiceTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 16 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src\Cortex.Contained.Agent.Host\Evaluation\EvaluationRegistration.cs src\Cortex.Contained.Agent.Host\Evaluation\Frozen\FrozenCorpusStore.cs src\Cortex.Contained.Agent.Host\History\ErasureStepHandlers.cs tests\Cortex.Contained.Agent.Host.Tests\History\ErasureStepTests.cs
git commit -m "feat(history): purges destroy overlapping frozen evaluation corpora (D3)" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 20: Recall and Storage surfacing, package download

The purge preview lists the frozen versions a deletion destroys and the confirmation names the SafeExchange attachment to delete; the Recall status carries pending off-machine deletions (banner with a password-confirmed acknowledgement); the Storage page lists frozen corpora and downloads packages (password again, rate limited, streamed from the agent in 4 MiB chunks); `scripts/Export-CortexEvalCorpus.ps1` saves a package to a host folder. The Storage inventory reports the evaluation directory as encrypted on an encrypted install.

**Files:**
- Create: `scripts/Export-CortexEvalCorpus.ps1`
- Modify: `src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Evaluation.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Program.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Recall/RecallControlService.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionService.cs`
- Modify: `src/Cortex.Contained.Agent.Host/Storage/StorageInventory.cs`
- Modify: `src/Cortex.Contained.Bridge/Endpoints/RecallEndpoints.cs`
- Modify: `src/Cortex.Contained.Bridge/Evaluation/EvaluationEndpoints.cs`
- Modify: `src/Cortex.Contained.Bridge/Evaluation/IEvaluationAgentClient.cs`
- Modify: `src/Cortex.Contained.Bridge/Hub/HubClient.Evaluation.cs`
- Modify: `src/Cortex.Contained.Bridge/Recall/HistoryPurgeConfirmation.cs`
- Modify: `src/Cortex.Contained.Bridge/wwwroot/app.html`
- Modify: `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-recall.js`
- Modify: `src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-storage.js`
- Modify: `src/Cortex.Contained.Contracts/Hub/IEvaluationHub.cs`
- Modify: `src/Cortex.Contained.Contracts/Hub/RecallTypes.cs`
- Test (modify): `tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallControlServiceTests.cs`
- Test (modify): `tests/Cortex.Contained.Agent.Host.Tests/Storage/StorageInventoryTests.cs`
- Test (modify): `tests/Cortex.Contained.Bridge.Tests/Evaluation/EvaluationEndpointsTests.cs`
- Test (modify): `tests/Cortex.Contained.Bridge.Tests/Recall/HistoryPurgeConfirmationTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallControlServiceTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallControlServiceTests.cs
+++ b/tests/Cortex.Contained.Agent.Host.Tests/Recall/RecallControlServiceTests.cs
@@ -17,8 +17,9 @@ public sealed class RecallControlServiceTests : IAsyncDisposable
     private readonly HistoryHarness h = new();
     private readonly ServiceProvider provider;
     private readonly IMemoryCreationTimes times = Substitute.For<IMemoryCreationTimes>();
     private readonly RecallControlService recall;
+    private readonly Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusStore frozen;
 
     public RecallControlServiceTests()
     {
         var services = new ServiceCollection();
@@ -49,9 +50,10 @@ public sealed class RecallControlServiceTests : IAsyncDisposable
             new HistoryHealthReporter(this.h.Store, this.h.Store, this.h.Status, new HistoryToolsSwitch(historyMonitor), historyMonitor),
             () => new RerankHealth(false, "off", 0, null, null, null, null),
             quarantine,
             new QuarantineReviewService(quarantine, memories),
-            purge);
+            purge,
+            frozen: this.frozen = new Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusStore(Path.Combine(this.h.Root, "state", "eval")));
     }
 
     public async ValueTask DisposeAsync()
     {
@@ -96,8 +98,28 @@ public sealed class RecallControlServiceTests : IAsyncDisposable
         Assert.Equal("off", status.Reranker.State);
         Assert.False(status.EventBackfill.Running);
     }
 
+    [Fact]
+    public async Task PreviewPurgeAsync_OverlappingFrozenCorpus_ListedAndDestroyedOneIsPendingOffsite()
+    {
+        await this.h.SayAsync("a", 0, channel: "x");
+        foreach (var (version, from) in new[] { ("v1", HistoryHarness.T0.AddDays(-1)), ("v2", HistoryHarness.T0.AddDays(30)) })
+        {
+            Assert.Equal(version, this.frozen.ReserveNextVersion());
+            Directory.CreateDirectory(this.frozen.DirectoryOf(version));
+            new Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusManifest { Version = version, CorpusFrom = from, CorpusTo = from.AddDays(2), ExportedAt = HistoryHarness.T0 }
+                .Write(Path.Combine(this.frozen.DirectoryOf(version), "manifest.json"));
+        }
+
+        var preview = await this.recall.PreviewPurgeAsync(new HistoryPurgeRequestDto { ChannelId = "x" }, CancellationToken.None);
+        this.frozen.Destroy("v1", HistoryHarness.T0);
+        var status = await this.recall.GetStatusAsync(CancellationToken.None);
+
+        Assert.Equal(["cortex-eval-corpus-v1.cxpkg"], preview.FrozenCorpusVersions.Select(v => v.PackageFileName));
+        Assert.Equal("v1", Assert.Single(status.PendingOffsiteDeletions).Version);
+    }
+
     [Fact]
     public async Task PreviewPurgeAsync_InvertedRange_Throws()
     {
         await Assert.ThrowsAsync<ArgumentException>(() => this.recall.PreviewPurgeAsync(
```

`tests/Cortex.Contained.Agent.Host.Tests/Storage/StorageInventoryTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Agent.Host.Tests/Storage/StorageInventoryTests.cs
+++ b/tests/Cortex.Contained.Agent.Host.Tests/Storage/StorageInventoryTests.cs
@@ -78,8 +78,9 @@ public sealed class StorageInventoryTests : IDisposable
         Assert.DoesNotContain("other:recall-settings.json", notCovered.Keys);
         Assert.DoesNotContain("other:memory", notCovered.Keys);
         Assert.Equal(1, evaluation!.Files);
         Assert.Equal(StorageItemStates.Plaintext, evaluation.State);
+        Assert.Equal(StorageItemStates.Encrypted, inventory.Evaluation(encryptedCopies: true)!.State);
     }
 
     [Fact]
     public void EmptyState_NothingEncryptedNoEvaluation()
```

`tests/Cortex.Contained.Bridge.Tests/Evaluation/EvaluationEndpointsTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Bridge.Tests/Evaluation/EvaluationEndpointsTests.cs
+++ b/tests/Cortex.Contained.Bridge.Tests/Evaluation/EvaluationEndpointsTests.cs
@@ -5,8 +5,9 @@ using Microsoft.AspNetCore.Authorization;
 using Microsoft.AspNetCore.Builder;
 using Microsoft.AspNetCore.Http;
 using Microsoft.AspNetCore.RateLimiting;
 using Microsoft.AspNetCore.Routing;
+using Microsoft.Extensions.DependencyInjection;
 
 namespace Cortex.Contained.Bridge.Tests.Evaluation;
 
 /// <summary>The evaluation API: re-authentication for anything that starts a job or carries workspace text.</summary>
@@ -93,5 +94,45 @@ public sealed class EvaluationEndpointsTests
 
         Assert.Equal(503, StatusOf(result));
         Assert.Contains("not connected", JsonSerializer.Serialize(result.GetType().GetProperty("Value")!.GetValue(result)), StringComparison.Ordinal);
     }
+
+    [Fact]
+    public async Task DownloadPackage_StreamsEveryChunkOfTheEncryptedPackage()
+    {
+        var package = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();
+        this.agent.ReadPackageAsync("cortex-eval-corpus-v1.cxpkg", Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
+            .Returns(call =>
+            {
+                var offset = (int)call.ArgAt<long>(1);
+                return new EvaluationPackageChunkDto { Length = package.Length, Data = package[offset..Math.Min(package.Length, offset + 4)] };
+            });
+
+        var result = await EvaluationEndpoints.DownloadPackageAsync(this.agent, Verify, "cortex-eval-corpus-v1.cxpkg", new PasswordBody(Password), CancellationToken.None);
+        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
+        using var body = new MemoryStream();
+        http.Response.Body = body;
+        await result.ExecuteAsync(http);
+
+        Assert.Equal(package, body.ToArray());
+    }
+
+    [Theory]
+    [InlineData("wrong", "cortex-eval-corpus-v1.cxpkg", 403)]
+    [InlineData(Password, "../messages.db", 400)]
+    [InlineData(Password, "notes.txt", 400)]
+    public async Task DownloadPackage_WrongPasswordOrNotAPackage_Refused(string password, string name, int status)
+    {
+        Assert.Equal(status, StatusOf(await EvaluationEndpoints.DownloadPackageAsync(this.agent, Verify, name, new PasswordBody(password), CancellationToken.None)));
+        await this.agent.DidNotReceive().ReadPackageAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
+    }
+
+    [Fact]
+    public async Task AcknowledgeOffsiteDeletion_NeedsThePassword()
+    {
+        this.agent.AcknowledgeOffsiteDeletionAsync("v1", Arg.Any<CancellationToken>()).Returns(true);
+
+        Assert.Equal(403, StatusOf(await EvaluationEndpoints.AcknowledgeOffsiteDeletionAsync(this.agent, Verify, "v1", new PasswordBody("wrong"), CancellationToken.None)));
+        Assert.Equal(200, StatusOf(await EvaluationEndpoints.AcknowledgeOffsiteDeletionAsync(this.agent, Verify, "v1", new PasswordBody(Password), CancellationToken.None)));
+        await this.agent.Received(1).AcknowledgeOffsiteDeletionAsync("v1", Arg.Any<CancellationToken>());
+    }
 }
```

`tests/Cortex.Contained.Bridge.Tests/Recall/HistoryPurgeConfirmationTests.cs` — apply:

```diff
--- a/tests/Cortex.Contained.Bridge.Tests/Recall/HistoryPurgeConfirmationTests.cs
+++ b/tests/Cortex.Contained.Bridge.Tests/Recall/HistoryPurgeConfirmationTests.cs
@@ -28,5 +28,27 @@ public sealed class HistoryPurgeConfirmationTests
     {
         Assert.EndsWith("No memories were created in that period.", HistoryPurgeConfirmation.Describe(new HistoryPurgePreviewDto { Messages = 1 }, DependentMemoryActions.Delete), StringComparison.Ordinal);
         Assert.Equal("Nothing matches: no messages would be deleted.", HistoryPurgeConfirmation.Describe(new HistoryPurgePreviewDto(), DependentMemoryActions.Review));
     }
+
+    [Fact]
+    public void Describe_OverlappingExportedFrozenCorpus_NamesTheOffMachineCopyToDelete()
+    {
+        var preview = new HistoryPurgePreviewDto
+        {
+            Messages = 2,
+            FrozenCorpusVersions =
+            [
+                new FrozenCorpusVersionDto { Version = "v1", PackageFileName = "cortex-eval-corpus-v1.cxpkg", ExportedAt = DateTimeOffset.UnixEpoch },
+                new FrozenCorpusVersionDto { Version = "v2", PackageFileName = "cortex-eval-corpus-v2.cxpkg" },
+            ],
+        };
+
+        var text = HistoryPurgeConfirmation.Describe(preview, DependentMemoryActions.Review);
+
+        Assert.EndsWith(
+            "No memories were created in that period. It also destroys the frozen evaluation corpus v1, v2 on this machine (freeze a new version afterwards). " +
+            "Its off-machine copy must be deleted by you: file cortex-eval-corpus-v1.cxpkg of the SafeExchange secret cortex-recovery-kit (Recall lists it until you confirm the deletion).",
+            text,
+            StringComparison.Ordinal);
+    }
 }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~RecallControlServiceTests|FullyQualifiedName~StorageInventoryTests|FullyQualifiedName~StorageEncryptionServiceTests" -v q` and `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~EvaluationEndpointsTests|FullyQualifiedName~HistoryPurgeConfirmationTests|FullyQualifiedName~RecallEndpointsTests" -v q`
Expected: build FAILS — `'HistoryPurgePreviewDto' does not contain a definition for 'FrozenCorpusVersions'`.

- [ ] **Step 3: Implement**

`scripts/Export-CortexEvalCorpus.ps1` (new):

```powershell
<#
.SYNOPSIS
    Downloads a passphrase-encrypted evaluation corpus package (cortex-eval-corpus-vN.cxpkg) to a host folder.

.DESCRIPTION
    The package was written by `eval-corpus export --version vN` inside the agent (Invoke-CortexEval.ps1). It is
    encrypted with the package passphrase (PBKDF2 600k + AES-256-GCM, chunked), so the host copy is safe to keep
    until it is attached to the SafeExchange secret cortex-recovery-kit. The Bridge checks the password again and
    streams the package from the agent; nothing is decrypted on the way. Prints the SHA-256 to compare with the
    export output and with SafeExchange after the upload.

.EXAMPLE
    .\scripts\Export-CortexEvalCorpus.ps1 -Version v1
    .\scripts\Export-CortexEvalCorpus.ps1 -Version v2 -OutDirectory D:\backups\cortex
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v[1-9][0-9]*$')]
    [string]$Version,

    [string]$OutDirectory = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'cortex-eval-packages'),
    [string]$TenantId = 'default',
    [string]$BridgeUrl = 'http://localhost:5080',
    [securestring]$Password
)

$ErrorActionPreference = 'Stop'

if (-not $Password) {
    $Password = Read-Host -AsSecureString -Prompt 'Bridge password'
}

$plain = [System.Net.NetworkCredential]::new('', $Password).Password
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-RestMethod -Method Post -Uri "$BridgeUrl/api/auth/login" -WebSession $session `
    -ContentType 'application/json' -Body (@{ password = $plain } | ConvertTo-Json) | Out-Null

$name = "cortex-eval-corpus-$Version.cxpkg"
New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
$target = Join-Path $OutDirectory $name
$tenant = [uri]::EscapeDataString($TenantId)
try {
    Invoke-WebRequest -Method Post -Uri "$BridgeUrl/api/tenants/$tenant/eval/frozen/packages/$name" -WebSession $session `
        -ContentType 'application/json' -Body (@{ password = $plain } | ConvertTo-Json) -OutFile $target
}
finally {
    $plain = $null
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash.ToLowerInvariant()
Write-Host "Saved $target ($((Get-Item -LiteralPath $target).Length) bytes)"
Write-Host "SHA-256 $hash"
Write-Host "Next: attach it to the SafeExchange secret cortex-recovery-kit under the file name $name (docs/evaluation-runbook.md), then record the upload with eval-corpus uploaded."
```

`src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs
+++ b/src/Cortex.Contained.Agent.Host/Evaluation/Frozen/FrozenCorpusStore.cs
@@ -13,8 +13,11 @@ public sealed class FrozenCorpusStore
 {
     private const string LedgerFileName = "offsite-deletions.json";
     private const string CounterFileName = "last-version";
 
+    /// <summary>Largest package chunk the Bridge may read at once (well below the hub's message limit).</summary>
+    public const int MaxChunkBytes = 4 * 1024 * 1024;
+
     private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
 
     private readonly Lock sync = new();
 
@@ -112,8 +115,57 @@ public sealed class FrozenCorpusStore
 
         return result.Distinct(StringComparer.Ordinal).ToList();
     }
 
+    /// <summary>Versions, packages and pending off-machine deletions for the Bridge.</summary>
+    public FrozenCorpusStatusDto Status() => new()
+    {
+        Versions = this.List().Select(ToDto).ToList(),
+        Packages = this.Packages(),
+        PendingOffsiteDeletions = this.PendingDeletions(),
+    };
+
+    /// <summary>The Bridge view of a manifest.</summary>
+    public static FrozenCorpusVersionDto ToDto(FrozenCorpusManifest manifest)
+    {
+        ArgumentNullException.ThrowIfNull(manifest);
+        return new FrozenCorpusVersionDto
+        {
+            Version = manifest.Version,
+            CreatedAt = manifest.CreatedAt,
+            CorpusFrom = manifest.CorpusFrom,
+            CorpusTo = manifest.CorpusTo,
+            Messages = manifest.Messages,
+            Memories = manifest.Memories,
+            HistoryQueries = manifest.HistoryQueries,
+            MemoryQueries = manifest.MemoryQueries,
+            PackageFileName = manifest.PackageFileName,
+            ExportedAt = manifest.ExportedAt,
+            Uploaded = manifest.Offsite is not null,
+            Imported = manifest.Imported,
+        };
+    }
+
+    /// <summary>Up to <paramref name="count"/> bytes (at most 4 MiB) of the package <paramref name="name"/> from <paramref name="offset"/>.</summary>
+    public EvaluationPackageChunkDto ReadPackage(string name, long offset, int count)
+    {
+        if (!this.Packages().Contains(name, StringComparer.Ordinal) || offset < 0 || count is <= 0 or > MaxChunkBytes)
+        {
+            return new EvaluationPackageChunkDto { Error = "No such package, or an invalid range." };
+        }
+
+        using var stream = new FileStream(Path.Combine(this.PackagesDirectory, name), FileMode.Open, FileAccess.Read, FileShare.Read);
+        if (offset > stream.Length)
+        {
+            return new EvaluationPackageChunkDto { Length = stream.Length, Error = "The offset is past the end of the package." };
+        }
+
+        stream.Position = offset;
+        var buffer = new byte[(int)Math.Min(count, stream.Length - offset)];
+        stream.ReadExactly(buffer);
+        return new EvaluationPackageChunkDto { Length = stream.Length, Data = buffer };
+    }
+
     /// <summary>Reserves the next version name (never reused, even after a version was destroyed).</summary>
     public string ReserveNextVersion()
     {
         lock (this.sync)
```

`src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Evaluation.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Evaluation.cs
+++ b/src/Cortex.Contained.Agent.Host/Hubs/AgentHub.Evaluation.cs
@@ -3,9 +3,9 @@ using Cortex.Contained.Contracts.Hub;
 using Microsoft.AspNetCore.SignalR;
 
 namespace Cortex.Contained.Agent.Host.Hubs;
 
-/// <summary><see cref="IEvaluationHub"/>: evaluation jobs and workspace files for the Bridge evaluation endpoints.</summary>
+/// <summary><see cref="IEvaluationHub"/>: evaluation jobs, workspace files and frozen corpora for the Bridge evaluation endpoints.</summary>
 public sealed partial class AgentHub
 {
     /// <inheritdoc />
     public Task<EvaluationJobDto> StartEvaluation(EvaluationStartRequestDto request)
@@ -50,10 +50,23 @@ public sealed partial class AgentHub
             throw new HubException("The storage key has not arrived from the Bridge yet.");
         }
     }
 
+    /// <inheritdoc />
+    public Task<FrozenCorpusStatusDto> GetFrozenCorpusStatus() => Task.FromResult(this.FrozenCorpora.Status());
+
+    /// <inheritdoc />
+    public Task<EvaluationPackageChunkDto> ReadEvaluationPackage(string name, long offset, int count) =>
+        Task.FromResult(this.FrozenCorpora.ReadPackage(name, offset, count));
+
+    /// <inheritdoc />
+    public Task<bool> AcknowledgeOffsiteDeletion(string version) => Task.FromResult(this.FrozenCorpora.AcknowledgeDeletion(version));
+
     private Evaluation.EvaluationJobService EvaluationJobs =>
         this.evaluationJobs ?? throw new HubException("Evaluation jobs are not available on this agent.");
 
+    private Evaluation.Frozen.FrozenCorpusStore FrozenCorpora =>
+        this.frozenCorpora ?? throw new HubException("Frozen evaluation corpora are not available on this agent.");
+
     private Evaluation.EvaluationFiles EvaluationFiles =>
         this.evaluationFiles ?? throw new HubException("Evaluation files are not available on this agent.");
 }
```

`src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs
+++ b/src/Cortex.Contained.Agent.Host/Hubs/AgentHub.cs
@@ -48,8 +48,9 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
     private readonly Cortex.Contained.Agent.Host.Recall.RecallControlService? recall;
     private readonly Storage.StorageEncryptionService? storageEncryption;
     private readonly Cortex.Contained.Agent.Host.Evaluation.EvaluationJobService? evaluationJobs;
     private readonly Cortex.Contained.Agent.Host.Evaluation.EvaluationFiles? evaluationFiles;
+    private readonly Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusStore? frozenCorpora;
     private readonly Cortex.Contained.Agent.Host.Mcp.McpToolStore mcpToolStore;
     private readonly IHttpClientFactory httpClientFactory;
     private readonly AgentMetrics metrics;
     private readonly SubagentExecutionCoordinator subagentCoordinator;
@@ -88,9 +89,10 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
         Cortex.Contained.Agent.Host.Reranking.RerankerAccess? rerankerAccess = null,
         Cortex.Contained.Agent.Host.Recall.RecallControlService? recall = null,
         Storage.StorageEncryptionService? storageEncryption = null,
         Cortex.Contained.Agent.Host.Evaluation.EvaluationJobService? evaluationJobs = null,
-        Cortex.Contained.Agent.Host.Evaluation.EvaluationFiles? evaluationFiles = null)
+        Cortex.Contained.Agent.Host.Evaluation.EvaluationFiles? evaluationFiles = null,
+        Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusStore? frozenCorpora = null)
     {
         this.runtime = runtime;
         this.llmClient = llmClient;
         this.bridgeClientAccessor = bridgeClientAccessor;
@@ -117,8 +119,9 @@ public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
         this.recall = recall;
         this.storageEncryption = storageEncryption;
         this.evaluationJobs = evaluationJobs;
         this.evaluationFiles = evaluationFiles;
+        this.frozenCorpora = frozenCorpora;
         this.mcpToolStore = mcpToolStore;
         this.httpClientFactory = httpClientFactory;
         this.metrics = metrics;
         this.subagentCoordinator = subagentCoordinator;
```

`src/Cortex.Contained.Agent.Host/Program.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Program.cs
+++ b/src/Cortex.Contained.Agent.Host/Program.cs
@@ -647,9 +647,10 @@ builder.Services.AddSingleton(sp => new Cortex.Contained.Agent.Host.Recall.Recal
     () => sp.GetRequiredService<Cortex.Contained.Agent.Host.Reranking.RerankStage>().Snapshot(),
     sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemoryQuarantine>(),
     sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.QuarantineReviewService>(),
     sp.GetRequiredService<HistoryPurgeService>(),
-    sp.GetRequiredService<Cortex.Contained.Agent.Host.Events.EventBackfillService>()));
+    sp.GetRequiredService<Cortex.Contained.Agent.Host.Events.EventBackfillService>(),
+    sp.GetRequiredService<Cortex.Contained.Agent.Host.Evaluation.Frozen.FrozenCorpusStore>()));
 
 // --- Automatic Memory Extraction (hosted background service with Channel<T> queue) ---
 builder.Services.AddSingleton(sp =>
     new Cortex.Contained.Agent.Host.Memory.MemoryExtractionService(
```

`src/Cortex.Contained.Agent.Host/Recall/RecallControlService.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Recall/RecallControlService.cs
+++ b/src/Cortex.Contained.Agent.Host/Recall/RecallControlService.cs
@@ -24,8 +24,9 @@ public sealed class RecallControlService
     private readonly MemoryQuarantine quarantine;
     private readonly QuarantineReviewService review;
     private readonly HistoryPurgeService purge;
     private readonly EventBackfillService? backfill;
+    private readonly Evaluation.Frozen.FrozenCorpusStore? frozen;
 
     public RecallControlService(
         RecallSettingsStore store,
         IOptionsMonitor<HistoryOptions> history,
@@ -35,9 +36,10 @@ public sealed class RecallControlService
         Func<RerankHealth> rerankHealth,
         MemoryQuarantine quarantine,
         QuarantineReviewService review,
         HistoryPurgeService purge,
-        EventBackfillService? backfill = null)
+        EventBackfillService? backfill = null,
+        Evaluation.Frozen.FrozenCorpusStore? frozen = null)
     {
         this.store = store;
         this.history = history;
         this.reranker = reranker;
@@ -47,8 +49,9 @@ public sealed class RecallControlService
         this.quarantine = quarantine;
         this.review = review;
         this.purge = purge;
         this.backfill = backfill;
+        this.frozen = frozen;
     }
 
     /// <summary>The effective settings.</summary>
     public RecallSettingsDto GetSettings() =>
@@ -74,9 +77,10 @@ public sealed class RecallControlService
     public async Task<RecallStatusDto> GetStatusAsync(CancellationToken cancellationToken)
     {
         var historyHealth = await this.health.SnapshotAsync(cancellationToken).ConfigureAwait(false);
         var quarantined = (await this.quarantine.GetQuarantinedIdsAsync(cancellationToken).ConfigureAwait(false)).Count;
-        return RecallStatusMapper.Status(this.GetSettings(), historyHealth, this.rerankHealth(), this.backfill?.Status, quarantined);
+        return RecallStatusMapper.Status(this.GetSettings(), historyHealth, this.rerankHealth(), this.backfill?.Status, quarantined)
+            with { PendingOffsiteDeletions = this.frozen?.PendingDeletions() ?? [] };
     }
 
     /// <summary>What a purge of <paramref name="request"/> would delete.</summary>
     /// <exception cref="ArgumentException">The time range is empty or inverted.</exception>
@@ -95,8 +99,11 @@ public sealed class RecallControlService
             Messages = preview.Messages,
             DependentMemories = preview.DependentMemories,
             RangeFrom = preview.RangeFrom,
             RangeTo = preview.RangeTo,
+            FrozenCorpusVersions = this.frozen is { } frozen && preview.Messages > 0 && preview.RangeTo is { } to
+                ? frozen.Overlapping(preview.RangeFrom, to).Select(frozen.Read).OfType<Evaluation.Frozen.FrozenCorpusManifest>().Select(Evaluation.Frozen.FrozenCorpusStore.ToDto).ToList()
+                : [],
         };
     }
 
     /// <summary>One page of quarantined memories with their purge source.</summary>
```

`src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionService.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionService.cs
+++ b/src/Cortex.Contained.Agent.Host/Storage/StorageEncryptionService.cs
@@ -57,9 +57,9 @@ public sealed partial class StorageEncryptionService
     {
         var covered = this.inventory.Covered();
         var health = this.GetHealth();
         List<StorageItemDto> items = [.. covered];
-        if (this.inventory.Evaluation() is { } evaluation)
+        if (this.inventory.Evaluation(encryptedCopies: this.context.Enabled) is { } evaluation)
         {
             items.Add(evaluation);
         }
```

`src/Cortex.Contained.Agent.Host/Storage/StorageInventory.cs` — apply:

```diff
--- a/src/Cortex.Contained.Agent.Host/Storage/StorageInventory.cs
+++ b/src/Cortex.Contained.Agent.Host/Storage/StorageInventory.cs
@@ -77,10 +77,14 @@ public sealed class StorageInventory
         this.MemoryContent(),
         this.Snapshot(),
     ];
 
-    /// <summary>Plaintext copies of history and memory made by the evaluation commands (null when there are none).</summary>
-    public StorageItemDto? Evaluation()
+    /// <summary>
+    /// The evaluation directory (null when it is empty): plaintext copies of history and memory made before encryption was
+    /// enabled, or — with <paramref name="encryptedCopies"/> — evaluation copies encrypted with the evaluation key plus
+    /// plaintext reports (ids and numbers only).
+    /// </summary>
+    public StorageItemDto? Evaluation(bool encryptedCopies = false)
     {
         var (bytes, files) = this.Measure(this.layout.EvaluationDirectory);
         return files == 0
             ? null
@@ -88,12 +92,14 @@ public sealed class StorageInventory
             {
                 Id = EvaluationId,
                 Label = "Evaluation runs (history-eval, history-smoke, memory-eval)",
                 Path = "eval/",
-                State = StorageItemStates.Plaintext,
+                State = encryptedCopies ? StorageItemStates.Encrypted : StorageItemStates.Plaintext,
                 Bytes = bytes,
                 Files = files,
-                Detail = "Raw copies of messages.db and the memory store with their datasets. Erase them when enabling; the commands refuse to run while encryption is on.",
+                Detail = encryptedCopies
+                    ? "Corpus copies and frozen corpora are encrypted with the evaluation key and datasets are sealed; reports, verdicts and manifests hold ids and numbers only."
+                    : "Raw copies of messages.db and the memory store with their datasets. Erase them when enabling; afterwards the evaluations run encrypted inside the agent.",
             };
     }
 
     /// <summary>Items that stay in plaintext, with the reason (including unrecognized entries of the state volume).</summary>
```

`src/Cortex.Contained.Bridge/Endpoints/RecallEndpoints.cs` — apply:

```diff
--- a/src/Cortex.Contained.Bridge/Endpoints/RecallEndpoints.cs
+++ b/src/Cortex.Contained.Bridge/Endpoints/RecallEndpoints.cs
@@ -99,8 +99,9 @@ internal static class RecallEndpoints
                     preview.Messages,
                     preview.DependentMemories,
                     preview.RangeFrom,
                     preview.RangeTo,
+                    preview.FrozenCorpusVersions,
                     Confirmations = new
                     {
                         Review = HistoryPurgeConfirmation.Describe(preview, DependentMemoryActions.Review),
                         Delete = HistoryPurgeConfirmation.Describe(preview, DependentMemoryActions.Delete),
```

`src/Cortex.Contained.Bridge/Evaluation/EvaluationEndpoints.cs` — apply:

```diff
--- a/src/Cortex.Contained.Bridge/Evaluation/EvaluationEndpoints.cs
+++ b/src/Cortex.Contained.Bridge/Evaluation/EvaluationEndpoints.cs
@@ -14,19 +14,28 @@ public sealed record EvaluationFileReadRequest(string? Password, string? Tool, s
 
 /// <summary>Body of <c>POST /api/tenants/{tenantId}/eval/files/write</c>.</summary>
 public sealed record EvaluationFileWriteRequest(string? Password, string? Tool, string? Name, string? Content);
 
+/// <summary>Body of the package download and the off-machine deletion acknowledgement (the Bridge password again).</summary>
+public sealed record PasswordBody(string? Password);
+
 /// <summary>
 /// Evaluation API (all require authorization): <c>/api/tenants/{tenantId}/eval/jobs</c> (POST start — password
 /// again, rate limited; GET list), <c>/eval/jobs/{jobId}</c> (GET), <c>/eval/jobs/{jobId}/cancel</c> (POST),
 /// <c>/eval/files/read</c> and <c>/eval/files/write</c> (POST, password again, rate limited: datasets carry
-/// personal text). The commands run inside the agent process, which holds the storage key; the Bridge only relays.
+/// personal text), <c>/eval/frozen</c> (GET: versions, packages, pending off-machine deletions),
+/// <c>/eval/frozen/packages/{name}</c> (POST, password again, rate limited: downloads a passphrase-encrypted
+/// package) and <c>/eval/frozen/{version}/offsite-deleted</c> (POST, password again). The commands run inside the
+/// agent process, which holds the storage key; the Bridge only relays.
 /// </summary>
 internal static class EvaluationEndpoints
 {
     /// <summary>Longest workspace file accepted (datasets are JSONL; 8 Mi characters is far above 500 labelled queries).</summary>
     internal const int MaxFileChars = 8 * 1024 * 1024;
 
+    /// <summary>Package bytes fetched from the agent per hub call.</summary>
+    internal const int PackageChunkBytes = 4 * 1024 * 1024;
+
     /// <summary>Maps the evaluation endpoints onto <paramref name="app"/>.</summary>
     public static void MapEvaluationEndpoints(this WebApplication app)
     {
         app.MapPost("/api/tenants/{tenantId}/eval/jobs", (string tenantId, EvaluationJobRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
@@ -48,8 +57,100 @@ internal static class EvaluationEndpoints
         }).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);
 
         app.MapPost("/api/tenants/{tenantId}/eval/files/write", (string tenantId, EvaluationFileWriteRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
             WriteFileAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), body, ct)).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);
+
+        app.MapGet("/api/tenants/{tenantId}/eval/frozen", (string tenantId, [FromServices] TenantRouter router, CancellationToken ct) =>
+            FrozenStatusAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), ct)).RequireAuthorization();
+
+        app.MapPost("/api/tenants/{tenantId}/eval/frozen/packages/{name}", async (string tenantId, string name, PasswordBody body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
+        {
+            http.Response.Headers.CacheControl = "no-store";
+            return await DownloadPackageAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), name, body, ct).ConfigureAwait(false);
+        }).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);
+
+        app.MapPost("/api/tenants/{tenantId}/eval/frozen/{version}/offsite-deleted", (string tenantId, string version, PasswordBody body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
+            AcknowledgeOffsiteDeletionAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), version, body, ct)).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);
+    }
+
+    internal static Task<IResult> FrozenStatusAsync(IEvaluationAgentClient? agent, CancellationToken ct) =>
+        agent is null ? Task.FromResult(NotConnected()) : RecallEndpoints.RelayAsync(() => agent.GetFrozenStatusAsync(ct), Results.Ok);
+
+    /// <summary>
+    /// Streams a passphrase-encrypted export package from the agent in chunks (it never exists in plaintext, here or there).
+    /// Only <c>*.cxpkg</c> file names the agent lists are served.
+    /// </summary>
+    internal static async Task<IResult> DownloadPackageAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, string name, PasswordBody? body, CancellationToken ct)
+    {
+        if (string.IsNullOrWhiteSpace(name) || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) || !name.EndsWith(".cxpkg", StringComparison.Ordinal))
+        {
+            return BadRequest("Not a package name.");
+        }
+
+        if (!verifyPassword(body?.Password))
+        {
+            return Forbidden();
+        }
+
+        if (agent is null)
+        {
+            return NotConnected();
+        }
+
+        EvaluationPackageChunkDto first;
+        try
+        {
+            first = await agent.ReadPackageAsync(name, 0, PackageChunkBytes, ct).ConfigureAwait(false);
+        }
+#pragma warning disable CA1031 // Any transport failure is reported as "agent not available", like the other tenant endpoints
+        catch (Exception ex) when (ex is not OperationCanceledException)
+        {
+            return Results.Json(new { error = "Agent not available: " + ex.Message }, statusCode: 503);
+        }
+#pragma warning restore CA1031
+
+        if (first.Error is not null)
+        {
+            return Results.Json(new { error = first.Error }, statusCode: 404);
+        }
+
+        return Results.Stream(
+            async stream =>
+            {
+                var chunk = first;
+                long offset = 0;
+                while (true)
+                {
+                    await stream.WriteAsync(chunk.Data, ct).ConfigureAwait(false);
+                    offset += chunk.Data.Length;
+                    if (offset >= chunk.Length || chunk.Data.Length == 0)
+                    {
+                        break;
+                    }
+
+                    chunk = await agent.ReadPackageAsync(name, offset, PackageChunkBytes, ct).ConfigureAwait(false);
+                    if (chunk.Error is not null)
+                    {
+                        throw new InvalidOperationException(chunk.Error);
+                    }
+                }
+            },
+            "application/octet-stream",
+            name);
+    }
+
+    internal static async Task<IResult> AcknowledgeOffsiteDeletionAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, string version, PasswordBody? body, CancellationToken ct)
+    {
+        if (!verifyPassword(body?.Password))
+        {
+            return Forbidden();
+        }
+
+        return agent is null
+            ? NotConnected()
+            : await RecallEndpoints.RelayAsync(
+                () => agent.AcknowledgeOffsiteDeletionAsync(version, ct),
+                acknowledged => acknowledged ? Results.Ok(new { acknowledged }) : Results.Json(new { error = $"No off-machine deletion is pending for {version}." }, statusCode: 404)).ConfigureAwait(false);
     }
 
     internal static async Task<IResult> StartAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, EvaluationJobRequest? body, CancellationToken ct)
     {
```

`src/Cortex.Contained.Bridge/Evaluation/IEvaluationAgentClient.cs` — apply:

```diff
--- a/src/Cortex.Contained.Bridge/Evaluation/IEvaluationAgentClient.cs
+++ b/src/Cortex.Contained.Bridge/Evaluation/IEvaluationAgentClient.cs
@@ -16,8 +16,14 @@ public interface IEvaluationAgentClient
 
     Task<EvaluationFileDto> ReadFileAsync(EvaluationFileRequestDto request, CancellationToken cancellationToken);
 
     Task<EvaluationFileWriteResultDto> WriteFileAsync(EvaluationFileWriteDto request, CancellationToken cancellationToken);
+
+    Task<FrozenCorpusStatusDto> GetFrozenStatusAsync(CancellationToken cancellationToken);
+
+    Task<EvaluationPackageChunkDto> ReadPackageAsync(string name, long offset, int count, CancellationToken cancellationToken);
+
+    Task<bool> AcknowledgeOffsiteDeletionAsync(string version, CancellationToken cancellationToken);
 }
 
 /// <summary>Adapts a connected <see cref="HubClient"/> to <see cref="IEvaluationAgentClient"/>.</summary>
 public sealed class HubEvaluationAgentClient(HubClient client) : IEvaluationAgentClient
@@ -35,5 +41,12 @@ public sealed class HubEvaluationAgentClient(HubClient client) : IEvaluationAgen
 
     public Task<EvaluationFileDto> ReadFileAsync(EvaluationFileRequestDto request, CancellationToken cancellationToken) => client.ReadEvaluationFileAsync(request, cancellationToken);
 
     public Task<EvaluationFileWriteResultDto> WriteFileAsync(EvaluationFileWriteDto request, CancellationToken cancellationToken) => client.WriteEvaluationFileAsync(request, cancellationToken);
+
+    public Task<FrozenCorpusStatusDto> GetFrozenStatusAsync(CancellationToken cancellationToken) => client.GetFrozenCorpusStatusAsync(cancellationToken);
+
+    public Task<EvaluationPackageChunkDto> ReadPackageAsync(string name, long offset, int count, CancellationToken cancellationToken) =>
+        client.ReadEvaluationPackageAsync(name, offset, count, cancellationToken);
+
+    public Task<bool> AcknowledgeOffsiteDeletionAsync(string version, CancellationToken cancellationToken) => client.AcknowledgeOffsiteDeletionAsync(version, cancellationToken);
 }
```

`src/Cortex.Contained.Bridge/Hub/HubClient.Evaluation.cs` — apply:

```diff
--- a/src/Cortex.Contained.Bridge/Hub/HubClient.Evaluation.cs
+++ b/src/Cortex.Contained.Bridge/Hub/HubClient.Evaluation.cs
@@ -46,5 +46,26 @@ public sealed partial class HubClient
     {
         EnsureConnected();
         return await this.connection!.InvokeAsync<EvaluationFileWriteResultDto>(nameof(IAgentHub.WriteEvaluationFile), request, cancellationToken).ConfigureAwait(false);
     }
+
+    /// <summary>Frozen corpora, packages and pending off-machine deletions.</summary>
+    public async Task<FrozenCorpusStatusDto> GetFrozenCorpusStatusAsync(CancellationToken cancellationToken)
+    {
+        EnsureConnected();
+        return await this.connection!.InvokeAsync<FrozenCorpusStatusDto>(nameof(IAgentHub.GetFrozenCorpusStatus), cancellationToken).ConfigureAwait(false);
+    }
+
+    /// <summary>One chunk of an export package.</summary>
+    public async Task<EvaluationPackageChunkDto> ReadEvaluationPackageAsync(string name, long offset, int count, CancellationToken cancellationToken)
+    {
+        EnsureConnected();
+        return await this.connection!.InvokeAsync<EvaluationPackageChunkDto>(nameof(IAgentHub.ReadEvaluationPackage), name, offset, count, cancellationToken).ConfigureAwait(false);
+    }
+
+    /// <summary>Records the owner's deletion of an off-machine package.</summary>
+    public async Task<bool> AcknowledgeOffsiteDeletionAsync(string version, CancellationToken cancellationToken)
+    {
+        EnsureConnected();
+        return await this.connection!.InvokeAsync<bool>(nameof(IAgentHub.AcknowledgeOffsiteDeletion), version, cancellationToken).ConfigureAwait(false);
+    }
 }
```

`src/Cortex.Contained.Bridge/Recall/HistoryPurgeConfirmation.cs` — apply:

```diff
--- a/src/Cortex.Contained.Bridge/Recall/HistoryPurgeConfirmation.cs
+++ b/src/Cortex.Contained.Bridge/Recall/HistoryPurgeConfirmation.cs
@@ -18,14 +18,30 @@ public static class HistoryPurgeConfirmation
             return "Nothing matches: no messages would be deleted.";
         }
 
         var text = string.Create(CultureInfo.InvariantCulture, $"This deletes {preview.Messages} messages and erases them from the search index.");
+        var frozen = FrozenCorpora(preview);
         if (preview.DependentMemories == 0)
         {
-            return text + " No memories were created in that period.";
+            return text + " No memories were created in that period." + frozen;
         }
 
         return text + (DependentMemoryActions.IsDelete(dependentMemories)
             ? string.Create(CultureInfo.InvariantCulture, $" {preview.DependentMemories} memories were created in that period. You chose Delete: they are erased permanently together with the history.")
-            : string.Create(CultureInfo.InvariantCulture, $" {preview.DependentMemories} memories were created in that period. You chose Review: they are hidden from the agent until you keep or delete them under Recall → Needs review."));
+            : string.Create(CultureInfo.InvariantCulture, $" {preview.DependentMemories} memories were created in that period. You chose Review: they are hidden from the agent until you keep or delete them under Recall → Needs review.")) + frozen;
+    }
+
+    // D3: overlapping frozen evaluation corpora are destroyed here; an exported one leaves a copy only the owner can delete.
+    private static string FrozenCorpora(HistoryPurgePreviewDto preview)
+    {
+        if (preview.FrozenCorpusVersions.Count == 0)
+        {
+            return string.Empty;
+        }
+
+        var text = $" It also destroys the frozen evaluation corpus {string.Join(", ", preview.FrozenCorpusVersions.Select(v => v.Version))} on this machine (freeze a new version afterwards).";
+        var offsite = preview.FrozenCorpusVersions.Where(v => v.ExportedAt is not null || v.Uploaded).Select(v => v.PackageFileName).ToList();
+        return offsite.Count == 0
+            ? text
+            : text + $" Its off-machine copy must be deleted by you: file {string.Join(", ", offsite)} of the SafeExchange secret cortex-recovery-kit (Recall lists it until you confirm the deletion).";
     }
 }
```

`src/Cortex.Contained.Bridge/wwwroot/app.html` — apply:

```diff
--- a/src/Cortex.Contained.Bridge/wwwroot/app.html
+++ b/src/Cortex.Contained.Bridge/wwwroot/app.html
@@ -1221,8 +1221,24 @@
             <div x-show="!connected" class="alert alert-warning py-2">
                 <i class="bi bi-plug me-1"></i>The agent is not connected; settings and status are unavailable.
             </div>
 
+            <!-- Off-machine evaluation copies a deletion destroyed locally (D3): only the owner can delete them in SafeExchange. -->
+            <div x-show="pendingOffsite.length > 0" class="alert alert-danger py-2">
+                <div class="fw-semibold mb-1"><i class="bi bi-cloud-slash me-1"></i>Delete these off-machine evaluation copies</div>
+                <p class="small mb-2">A history deletion destroyed the frozen evaluation corpus on this machine. Its exported package still exists in
+                    SafeExchange: delete the file from the secret named below (your assistant: <code>secret_get</code> for the contentId, then
+                    <code>file_delete</code>), then confirm here.</p>
+                <template x-for="item in pendingOffsite" :key="item.version">
+                    <div class="d-flex align-items-center gap-2 small mb-1">
+                        <span><b x-text="item.version"></b>: file <code x-text="item.packageFileName"></code> of secret <code x-text="item.secret"></code>
+                            <span x-show="item.contentId" x-text="'(contentId ' + item.contentId + ')'"></span></span>
+                        <button class="btn btn-sm btn-outline-light ms-auto" :disabled="!offsite.password || offsite.busy === item.version" @click="acknowledgeOffsite(item.version)">I deleted it</button>
+                    </div>
+                </template>
+                <input type="password" class="form-control form-control-sm mt-1" style="max-width: 260px;" placeholder="Bridge password to confirm" x-model="offsite.password" autocomplete="current-password">
+            </div>
+
             <!-- ═══ Settings ═══ -->
             <div class="card mb-3" x-show="form">
                 <div class="card-body">
                     <h6 class="text-uppercase text-secondary border-bottom pb-2 mb-3">Recall settings</h6>
@@ -1656,8 +1672,44 @@
                     </details>
                 </div>
             </div>
 
+            <!-- ═══ Evaluation corpora (sanitised frozen copies, D6) ═══ -->
+            <div class="card mb-3" x-show="frozen && (frozen.versions.length > 0 || frozen.pendingOffsiteDeletions.length > 0)">
+                <div class="card-body">
+                    <h6 class="text-uppercase text-secondary border-bottom pb-2 mb-3">Evaluation corpora</h6>
+                    <p class="small text-body-secondary">
+                        Sanitised, frozen copies of history and memories for the quality gates (pseudonymised; the local copy is encrypted with the storage key).
+                        The package is encrypted with its own passphrase and is the only form that leaves this machine: attach it to the SafeExchange
+                        secret <code>cortex-recovery-kit</code> under its file name (see docs/evaluation-runbook.md).
+                    </p>
+                    <table class="table table-sm small mb-2">
+                        <thead><tr><th>Version</th><th>Range</th><th>Messages</th><th>Queries</th><th>Exported</th><th>Uploaded</th><th></th></tr></thead>
+                        <tbody>
+                            <template x-for="v in frozen?.versions || []" :key="v.version">
+                                <tr>
+                                    <td x-text="v.version + (v.imported ? ' (imported)' : '')"></td>
+                                    <td x-text="new Date(v.corpusFrom).toLocaleDateString() + ' – ' + new Date(v.corpusTo).toLocaleDateString()"></td>
+                                    <td x-text="v.messages + ' · ' + v.memories + ' memories'"></td>
+                                    <td x-text="v.historyQueries + ' / ' + v.memoryQueries"></td>
+                                    <td x-text="formatDateTime(v.exportedAt)"></td>
+                                    <td x-text="v.uploaded ? 'yes' : 'no'"></td>
+                                    <td class="text-end">
+                                        <button class="btn btn-sm btn-outline-primary" x-show="hasPackage(v)" :disabled="!frozenPassword || downloading === v.packageFileName" @click="downloadPackage(v.packageFileName)">
+                                            <span x-show="downloading === v.packageFileName" class="spinner-border spinner-border-sm me-1"></span><i class="bi bi-download"></i>
+                                        </button>
+                                    </td>
+                                </tr>
+                            </template>
+                        </tbody>
+                    </table>
+                    <input type="password" class="form-control form-control-sm mb-2" style="max-width: 260px;" placeholder="Bridge password to download" x-model="frozenPassword" autocomplete="current-password">
+                    <div class="small text-danger" x-show="(frozen?.pendingOffsiteDeletions || []).length > 0">
+                        <i class="bi bi-cloud-slash me-1"></i>Off-machine copies to delete are listed on the Recall page.
+                    </div>
+                </div>
+            </div>
+
             <!-- Enable confirmation modal -->
             <div x-show.important="showEnableModal" class="modal d-block" tabindex="-1" style="background: rgba(0,0,0,0.5);">
                 <div class="modal-dialog modal-lg">
                     <div class="modal-content">
```

`src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-recall.js` — apply:

```diff
--- a/src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-recall.js
+++ b/src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-recall.js
@@ -30,8 +30,11 @@ function tenantRecallPage() {
         connected: true,
         statusTimer: null,
         backfillRunning: false,
 
+        // ── Off-machine evaluation copies to delete (D3) ─────────
+        offsite: { password: "", busy: "" },
+
         // ── Delete history ───────────────────────────────────────
         channels: [],
         purge: { channelId: "", time: "all", from: "", olderThan: "", dependentMemories: "review" },
         preview: null,
@@ -182,8 +185,24 @@ function tenantRecallPage() {
         percent(part, total) {
             return total > 0 ? Math.round((100 * part) / total) : 0;
         },
 
+        get pendingOffsite() { return this.status?.pendingOffsiteDeletions || []; },
+
+        /** The owner deleted the package from SafeExchange: record it (Bridge password again). */
+        async acknowledgeOffsite(version) {
+            this.offsite.busy = version;
+            try {
+                await api.post(`/api/tenants/${encodeURIComponent(this.tenantId)}/eval/frozen/${encodeURIComponent(version)}/offsite-deleted`, { password: this.offsite.password });
+                Alpine.store("toast").success(`Recorded: the off-machine copy of ${version} is deleted`);
+                this.offsite.password = "";
+                await this.loadStatus();
+            } catch (e) {
+                Alpine.store("toast").error("Not recorded: " + e.message);
+            }
+            this.offsite.busy = "";
+        },
+
         formatDateTime(ts) {
             return ts ? new Date(ts).toLocaleString() : "—";
         },
```

`src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-storage.js` — apply:

```diff
--- a/src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-storage.js
+++ b/src/Cortex.Contained.Bridge/wwwroot/js/pages/tenant-storage.js
@@ -30,8 +30,12 @@ function tenantStoragePage() {
         enable: { password: "", confirm: "", erase: true, acceptNoKit: false, snapshotTaken: false },
         enabling: false,
         restarting: false,
 
+        frozen: null,
+        frozenPassword: "",
+        downloading: "",
+
         async init() {
             await this.loadStatus();
             this.statusTimer = setInterval(() => this.loadStatus(), 5000);
         },
@@ -52,8 +56,48 @@ function tenantStoragePage() {
             if (this.restarting && this.status?.encryptAtRest && this.status.messagesState === "open" && this.status.memoryState === "open") {
                 this.restarting = false;
                 Alpine.store("toast").success("Encryption at rest is on and every encrypted store is open");
             }
+            try {
+                this.frozen = await api.get(`/api/tenants/${encodeURIComponent(this.tenantId)}/eval/frozen`);
+            } catch {
+                this.frozen = null;
+            }
+        },
+
+        hasPackage(version) {
+            return (this.frozen?.packages || []).includes(version.packageFileName);
+        },
+
+        /** Downloads a passphrase-encrypted evaluation package (Bridge password again); upload it to SafeExchange as described in the runbook. */
+        async downloadPackage(name) {
+            this.downloading = name;
+            try {
+                const resp = await fetch(`/api/tenants/${encodeURIComponent(this.tenantId)}/eval/frozen/packages/${encodeURIComponent(name)}`, {
+                    method: "POST",
+                    headers: { "Content-Type": "application/json" },
+                    body: JSON.stringify({ password: this.frozenPassword }),
+                });
+                if (resp.status === 401) {
+                    window.location.href = "/login.html";
+                    return;
+                }
+                if (!resp.ok) {
+                    const err = await resp.json().catch(() => ({ error: `HTTP ${resp.status}` }));
+                    throw new Error(err.error || `HTTP ${resp.status}`);
+                }
+                const url = URL.createObjectURL(await resp.blob());
+                const a = document.createElement("a");
+                a.href = url;
+                a.download = name;
+                a.click();
+                URL.revokeObjectURL(url);
+                this.frozenPassword = "";
+                Alpine.store("toast").success(`${name} downloaded. Attach it to the SafeExchange secret cortex-recovery-kit under this file name.`);
+            } catch (e) {
+                Alpine.store("toast").error("Download: " + e.message);
+            }
+            this.downloading = "";
         },
 
         applyStatus(data) {
             this.status = data?.agent || null;
```

`src/Cortex.Contained.Contracts/Hub/IEvaluationHub.cs` — apply:

```diff
--- a/src/Cortex.Contained.Contracts/Hub/IEvaluationHub.cs
+++ b/src/Cortex.Contained.Contracts/Hub/IEvaluationHub.cs
@@ -1,10 +1,10 @@
 namespace Cortex.Contained.Contracts.Hub;
 
 /// <summary>
 /// Evaluation commands (<c>history-eval</c>, <c>memory-eval</c>, <c>eval-corpus</c>) run inside the agent process,
-/// where the storage key lives, and the owner-authored workspace files. Part of the composed
-/// <see cref="IAgentHub"/> surface; the Bridge evaluation endpoints use it.
+/// where the storage key lives, the owner-authored workspace files and the frozen evaluation corpora (D3, D6).
+/// Part of the composed <see cref="IAgentHub"/> surface; the Bridge evaluation endpoints use it.
 /// </summary>
 public interface IEvaluationHub
 {
     /// <summary>Starts a command as a background job (one at a time); a refusal comes back as a job in state <c>refused</c>.</summary>
@@ -23,8 +23,17 @@ public interface IEvaluationHub
     Task<EvaluationFileDto> ReadEvaluationFile(EvaluationFileRequestDto request);
 
     /// <summary>Writes an owner-authored workspace file (sealed when it carries text on an encrypted install).</summary>
     Task<EvaluationFileWriteResultDto> WriteEvaluationFile(EvaluationFileWriteDto request);
+
+    /// <summary>Frozen corpus versions, export packages and off-machine copies awaiting deletion.</summary>
+    Task<FrozenCorpusStatusDto> GetFrozenCorpusStatus();
+
+    /// <summary>Up to <paramref name="count"/> bytes of an export package from <paramref name="offset"/> (passphrase-encrypted bytes).</summary>
+    Task<EvaluationPackageChunkDto> ReadEvaluationPackage(string name, long offset, int count);
+
+    /// <summary>Records that the owner deleted the off-machine package of <paramref name="version"/> from SafeExchange.</summary>
+    Task<bool> AcknowledgeOffsiteDeletion(string version);
 }
 
 /// <summary>String values of <see cref="EvaluationJobDto.State"/>.</summary>
 public static class EvaluationJobStates
```

`src/Cortex.Contained.Contracts/Hub/RecallTypes.cs` — apply:

```diff
--- a/src/Cortex.Contained.Contracts/Hub/RecallTypes.cs
+++ b/src/Cortex.Contained.Contracts/Hub/RecallTypes.cs
@@ -114,8 +114,11 @@ public sealed record RecallStatusDto
     public required EventBackfillStatusDto EventBackfill { get; init; }
 
     /// <summary>Curated memories currently hidden for review (D10).</summary>
     public int QuarantinedMemories { get; init; }
+
+    /// <summary>Off-machine evaluation packages a purge destroyed locally and the owner still has to delete from SafeExchange (D3).</summary>
+    public IReadOnlyList<OffsiteDeletionDto> PendingOffsiteDeletions { get; init; } = [];
 }
 
 /// <summary>History index build progress (the <c>/health</c> history block).</summary>
 public sealed record HistoryIndexStatusDto
@@ -214,8 +217,11 @@ public sealed record HistoryPurgePreviewDto
     public DateTimeOffset? RangeFrom { get; init; }
 
     /// <summary>Newest affected message, or null.</summary>
     public DateTimeOffset? RangeTo { get; init; }
+
+    /// <summary>Frozen evaluation corpus versions the deletion destroys (D3); exported ones leave an off-machine copy to delete.</summary>
+    public IReadOnlyList<FrozenCorpusVersionDto> FrozenCorpusVersions { get; init; } = [];
 }
 
 /// <summary>One quarantined memory with the purge that quarantined it.</summary>
 public sealed record QuarantinedMemoryDto
```

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "FullyQualifiedName~RecallControlServiceTests|FullyQualifiedName~StorageInventoryTests|FullyQualifiedName~StorageEncryptionServiceTests" -v q` and `dotnet test tests\Cortex.Contained.Bridge.Tests --filter "FullyQualifiedName~EvaluationEndpointsTests|FullyQualifiedName~HistoryPurgeConfirmationTests|FullyQualifiedName~RecallEndpointsTests" -v q`, then `dotnet build tests\Cortex.Contained.Agent.Host.Tests -v q`, `dotnet build tests\Cortex.Contained.Bridge.Tests -v q`, `dotnet build tests\Cortex.Contained.Contracts.Tests -v q`
Expected: `Cortex.Contained.Agent.Host.Tests` Passed! — 32 tests, 0 failed; `Cortex.Contained.Bridge.Tests` Passed! — 37 tests, 0 failed; every build `0 Warning(s) 0 Error(s)`.
Check the scripts and pages: `node --check src\Cortex.Contained.Bridge\wwwroot\js\pages\tenant-recall.js`, the same for `tenant-storage.js` (no output), and parse `scripts\Export-CortexEvalCorpus.ps1` as in Task 9 (`0`).

- [ ] **Step 5: Commit**

```powershell
git add scripts\Export-CortexEvalCorpus.ps1 src\Cortex.Contained.Agent.Host\Evaluation\Frozen\FrozenCorpusStore.cs src\Cortex.Contained.Agent.Host\Hubs\AgentHub.Evaluation.cs src\Cortex.Contained.Agent.Host\Hubs\AgentHub.cs src\Cortex.Contained.Agent.Host\Program.cs src\Cortex.Contained.Agent.Host\Recall\RecallControlService.cs src\Cortex.Contained.Agent.Host\Storage\StorageEncryptionService.cs src\Cortex.Contained.Agent.Host\Storage\StorageInventory.cs src\Cortex.Contained.Bridge\Endpoints\RecallEndpoints.cs src\Cortex.Contained.Bridge\Evaluation\EvaluationEndpoints.cs src\Cortex.Contained.Bridge\Evaluation\IEvaluationAgentClient.cs src\Cortex.Contained.Bridge\Hub\HubClient.Evaluation.cs src\Cortex.Contained.Bridge\Recall\HistoryPurgeConfirmation.cs src\Cortex.Contained.Bridge\wwwroot\app.html src\Cortex.Contained.Bridge\wwwroot\js\pages\tenant-recall.js src\Cortex.Contained.Bridge\wwwroot\js\pages\tenant-storage.js src\Cortex.Contained.Contracts\Hub\IEvaluationHub.cs src\Cortex.Contained.Contracts\Hub\RecallTypes.cs tests\Cortex.Contained.Agent.Host.Tests\Recall\RecallControlServiceTests.cs tests\Cortex.Contained.Agent.Host.Tests\Storage\StorageInventoryTests.cs tests\Cortex.Contained.Bridge.Tests\Evaluation\EvaluationEndpointsTests.cs tests\Cortex.Contained.Bridge.Tests\Recall\HistoryPurgeConfirmationTests.cs
git commit -m "feat(recall): frozen corpus status, purge confirmation and package download" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 21: Documentation and the owner runbook

`docs/evaluation-runbook.md` (new) is the owner's step-by-step guide; the encryption, security, API and architecture docs and the master spec's D6 row are updated.

**Files:**
- Modify: `docs/api-reference.md`
- Modify: `docs/architecture.md`
- Modify: `docs/encryption-at-rest.md`
- Create: `docs/evaluation-runbook.md`
- Modify: `docs/security.md`
- Modify: `docs/superpowers/specs/2026-11-21-episodic-history-memory-design.md`

- [ ] **Step 1: Write the documentation**

`docs/api-reference.md` — apply:

```diff
--- a/docs/api-reference.md
+++ b/docs/api-reference.md
@@ -57,8 +57,17 @@
 | POST | `/api/tenants/{tenantId}/storage/encryption/preview` | Yes | Changes nothing. `{ preview: { alreadyEnabled, keyFingerprint, willEncrypt[], willErase[], notCovered[], requiredFreeBytes, availableFreeBytes, requiredTempBytes, availableTempBytes, blockers[], warnings[] }, bridge }` |
 | POST | `/api/tenants/{tenantId}/storage/encryption/enable` | Yes + password | Body `{ password, confirm: "ENCRYPT", eraseEvaluationArtifacts = true, acceptNoRecoveryKit = false }`. `403` wrong password; `400` without the confirmation or without an exported kit (unless accepted); `409` when the agent holds encrypted data the Bridge has no key for, or refuses the key. Pushes the key, then `202` with `{ accepted, alreadyEnabled, restartScheduled, errors[] }` (one-way; the agent restarts) |
 | POST | `/api/tenants/{tenantId}/storage/recovery-kit` | Yes + password | Body `{ password, passphrase }` (passphrase ≥ 12). Returns the kit file (`application/json`, `Cache-Control: no-store`) and records the export time |
 | POST | `/api/tenants/{tenantId}/storage/recovery-kit/restore` | Yes + password | Body `{ password, passphrase, kit }`. Installs the kit key only when the Bridge has no usable key or the agent reports its data belongs to the kit's key; `409` otherwise; `400` wrong passphrase or another tenant's kit. Returns `{ restored, fingerprint, applied, message }` |
+| POST | `/api/tenants/{tenantId}/eval/jobs` | Yes + password | Body `{ password, tool, args[], passphrase? }` (`tool`: `history-eval`, `memory-eval`, `eval-corpus`). Starts the command inside the agent: `202` with the job, `409` when refused (another job runs, storage locked, unknown tool, or `--dir`/`--source`/`--labels`/`--embedder`/`--reranker`), `403` wrong password. Rate limited (`reauth`) |
+| GET | `/api/tenants/{tenantId}/eval/jobs` | Yes | `{ jobs: [EvaluationJobDto] }`, newest first |
+| GET | `/api/tenants/{tenantId}/eval/jobs/{jobId}` | Yes | `{ id, tool, command, state, exitCode, encrypted, startedAt, finishedAt, output }` (`state`: `running`, `succeeded`, `failed`, `cancelled`, `refused`); `404` unknown |
+| POST | `/api/tenants/{tenantId}/eval/jobs/{jobId}/cancel` | Yes | `{ cancelled }` |
+| POST | `/api/tenants/{tenantId}/eval/files/read` | Yes + password | Body `{ password, tool, name }` (allow-listed workspace files). `{ exists, sealed, content }`, `Cache-Control: no-store`. Rate limited |
+| POST | `/api/tenants/{tenantId}/eval/files/write` | Yes + password | Body `{ password, tool, name, content }` (≤ 8 Mi characters). Seals datasets and the dictionary on an encrypted install; identical content is not rewritten. `{ written, unchanged, sealed }`. Rate limited |
+| GET | `/api/tenants/{tenantId}/eval/frozen` | Yes | `{ versions[], packages[], pendingOffsiteDeletions[] }` (ids, counts, file names) |
+| POST | `/api/tenants/{tenantId}/eval/frozen/packages/{name}` | Yes + password | Body `{ password }`. Streams a passphrase-encrypted `*.cxpkg` package (`application/octet-stream`, `Cache-Control: no-store`); `404` unknown, `400` not a package name. Rate limited |
+| POST | `/api/tenants/{tenantId}/eval/frozen/{version}/offsite-deleted` | Yes + password | Body `{ password }`. Records that the owner deleted the version's attachment from SafeExchange; `404` when none is pending. Rate limited |
 | POST | `/api/channels/{type}` | Yes | Update channel configuration |
 | GET | `/api/mcp/actions` | Yes | List approval-gated MCP mutation actions (filter by `serverKey`, `toolName`, `state`, `workerId`; paged with `before`/`limit`) |
 | GET | `/api/mcp/actions/{actionId}` | Yes | One action, including its canonical arguments (for review) and result |
 | POST | `/api/mcp/actions/{actionId}/approve` | Yes | Approve, bound to the exact `argumentsHash`. Body: `{ argumentsHash, reason?, expiresAtUtc? }` (default TTL 1h) |
@@ -192,8 +201,22 @@ same workflow (always `review`).
 | `GetStorageEncryptionStatus()` -> `StorageEncryptionStatusDto` | Mode and source (`off`/`config`/`marker`/`detected`), restart pending, key state and fingerprints, store states, every item (header-only inventory) |
 | `PreviewStorageEncryption()` -> `StorageEncryptionPreviewDto` | What enabling would encrypt, erase and leave alone; disk-space pre-flight; changes nothing |
 | `EnableStorageEncryption(StorageEncryptionEnableRequestDto)` -> `StorageEncryptionEnableResultDto` | Requires the pushed key with the given fingerprint; erases evaluation copies on request; writes the one-way marker; stops the agent so Docker restarts it encrypted |
 
+#### Evaluation (`IEvaluationHub`)
+
+| Method | Description |
+|--------|-------------|
+| `StartEvaluation(EvaluationStartRequestDto)` -> `EvaluationJobDto` | Runs `history-eval`, `memory-eval` or `eval-corpus` in the agent process (encrypted-workspace mode on an encrypted install); one job at a time; refusals come back as state `refused` |
+| `GetEvaluationJob(jobId)` / `ListEvaluationJobs()` / `CancelEvaluationJob(jobId)` | Job state and output (ids, numbers, paths; ≤ 64 KiB) |
+| `ReadEvaluationFile(EvaluationFileRequestDto)` / `WriteEvaluationFile(EvaluationFileWriteDto)` | Allow-listed workspace files, sealed ones decrypted / sealed |
+| `GetFrozenCorpusStatus()` -> `FrozenCorpusStatusDto` | Frozen versions, export packages, pending off-machine deletions |
+| `ReadEvaluationPackage(name, offset, count)` -> `EvaluationPackageChunkDto` | ≤ 4 MiB of a passphrase-encrypted package |
+| `AcknowledgeOffsiteDeletion(version)` -> `bool` | The owner deleted the version's SafeExchange attachment |
+
+`PreviewHistoryPurge` also returns `frozenCorpusVersions` (versions the purge destroys) and `GetRecallStatus` returns
+`pendingOffsiteDeletions`.
+
 #### `SetHistoryRecallTools(enabled)` -> `bool`
 
 Show or hide the history recall tools at runtime; returns the effective state.
```

`docs/architecture.md` — apply:

```diff
--- a/docs/architecture.md
+++ b/docs/architecture.md
@@ -243,9 +243,12 @@ SQLite-backed (`/app/state/scheduler/tasks.db`). Tick interval: 15 seconds. Recu
 |------|------|--------|
 | Messages | `/app/state/messages.db` | SQLite (WAL, `secure_delete`; SQLite3MC-encrypted with `Storage:EncryptAtRest`), incl. `Events`, `HistoryJournal`, `Meta`, memory-quarantine tables |
 | History index | `/app/state/history/history-index.<gen>.db` + `history-index.current` | Contentless FTS5 plus chunk vectors (derived, no message text; vectors erased and encrypted with the generation) |
 | Smoke-eval workspace | `/app/state/eval/smoke` | Local only, never committed; `runs` destroyed by overlapping purges |
-| History-eval workspace | `/app/state/eval/hybrid` | Local only, never committed; `runs` (raw copies) destroyed by overlapping purges |
+| History-eval workspace | `/app/state/eval/hybrid` | Local only, never committed; `runs` (corpus copies, encrypted with the evaluation key on encrypted installs) destroyed by overlapping purges; `dataset.jsonl` sealed |
+| Memory-eval workspace | `/app/state/eval/memory` | Local only; the store copy of each command is deleted when it ends; `dataset.jsonl` and `seeds.jsonl` sealed on encrypted installs |
+| Frozen evaluation corpora | `/app/state/eval/frozen/vN` | Sanitised history and memories, datasets, plans, registrations (`manifest.json` with hashes); evaluation key; destroyed by overlapping purges (`offsite-deletions.json` lists exported ones) |
+| Evaluation packages | `/app/state/eval/packages/*.cxpkg` | Passphrase-encrypted exports of frozen corpora (off-machine backup in SafeExchange) |
 | Memories | `/app/state/memory/memory.db` | SQLite + sqlite-vec + FTS5; SQLite3MC-encrypted with encryption at rest |
 | Memory content | `/app/state/memory/memories/*.memory.data` | Text files; AES-256-GCM with encryption at rest |
 | Session snapshot | `/app/state/sessions-snapshot.json` | Live sessions saved at shutdown; AES-256-GCM with encryption at rest |
 | Encryption marker | `/app/state/storage-encryption.json` | One-way switch written by the Storage page |
```

`docs/encryption-at-rest.md` — apply:

```diff
--- a/docs/encryption-at-rest.md
+++ b/docs/encryption-at-rest.md
@@ -59,10 +59,14 @@ rekeys came in 2.7.0).
 | Logs | contain no message or memory text (guarded by tests) |
 | Bridge recording sessions | opt-in debugging recorder |
 | Volume snapshots, host backups, provider-side copies, filesystem free space | outside what the application can reach |
 
-Evaluation runs (`eval/`) hold raw plaintext copies of your history and memories. Enabling erases them by default,
-and `history-eval`, `history-smoke` and `memory-eval` refuse to run while encryption is on.
+Evaluation runs (`eval/`) made before encryption was enabled hold raw plaintext copies of your history and memories;
+enabling erases them by default. With encryption on, `history-eval`, `memory-eval` and `eval-corpus` run inside the
+agent as evaluation jobs (`scripts/Invoke-CortexEval.ps1`): their corpus copies are encrypted with a dedicated
+evaluation key, datasets and seeds are sealed, and reports hold ids and numbers only. Run through `docker exec`, the
+commands that copy stores refuse and point to the script; `history-smoke` keeps only its synthetic checks. See the
+[evaluation runbook](evaluation-runbook.md).
 
 The Bridge itself keeps no message history. `%LOCALAPPDATA%\Cortex\messages.db`, if present, is an unused file
 from older versions. It is encrypted with the Bridge database key, so you can delete it.
```

`docs/evaluation-runbook.md` (new):

````markdown
# Evaluation runbook (Gate H, Gate R, sanitised corpus)

This is the owner's step-by-step guide for the quality gates of episodic history and curated memory on an install
with **encryption at rest** (it works the same without encryption). Design:
[encrypted evaluation and sanitised corpus](superpowers/specs/2026-11-23-encrypted-eval-sanitised-corpus-design.md).

Everything runs **inside the agent process** as an evaluation job, because only that process holds the storage key.
You start jobs from Windows with `scripts\Invoke-CortexEval.ps1`; the Bridge asks for its password every time. Corpus
copies are encrypted with a dedicated evaluation key, datasets are sealed, and reports hold ids and numbers only.

| Script | What it does |
|---|---|
| `Invoke-CortexEval.ps1 -Tool <history-eval\|memory-eval\|eval-corpus> -- <command> [options]` | Runs a command as a job and prints its output. `-PromptPassphrase` for package export and import. |
| `Edit-CortexEvalFile.ps1 -Tool <tool> -Name <file>` | Opens a workspace file (dataset, plan, dictionary) in your editor and saves it back (sealed). `-Pull -Path` / `-Push -Path` move it to and from an explicit file instead. |
| `Export-CortexEvalCorpus.ps1 -Version vN` | Downloads the passphrase-encrypted package of a frozen version to a folder on this PC. |

Jobs refuse `--dir`, `--source`, `--labels`, `--embedder` and `--reranker`: they always use the configured workspaces
(`/app/state/eval/hybrid`, `/app/state/eval/memory`), stores and sidecars. One job runs at a time. Reports are plain
files in the container; read them with `docker exec cortex-agent cat <path printed by the job>`.

> Never paste dataset text, pools or seeds into chats, issues or commits. Files you `-Pull` to disk are plaintext:
> keep them under your user profile and delete them when you are done.

## 1. History search — Gate H

1. **Workspace:** `.\scripts\Invoke-CortexEval.ps1 -Tool history-eval -- init`
2. **Queries and labels** (§4.2 protocol): write `dataset.jsonl` with your assistant —
   `.\scripts\Edit-CortexEvalFile.ps1 -Tool history-eval -Name dataset.jsonl -Pull -Path .\work\history-dataset.jsonl`,
   label (graded 0–2 per message, query and judge model families differ), then
   `-Push -Path .\work\history-dataset.jsonl`. Delete the local copy afterwards.
3. `… -Tool history-eval -- validate` until it reports 0 problems; `… -- guard` (paraphrase guard).
4. **κ:** double-label a sample by hand; push `judge-agreement.csv` (`messageId,llmGrade,humanGrade` per line) with
   `Edit-CortexEvalFile.ps1 -Name judge-agreement.csv -Push`; `… -- kappa` must report ≥ 0.6.
5. **Dev ablations:** `… -- ablate --split dev`; read the report; pick the configuration; write it into
   `ablation.json` (`holdout.config`, `holdout.margin`) and fill in `analysis-plan.md` (both via `Edit-CortexEvalFile.ps1`).
6. **Register:** `… -- register`. From now on the dataset, the plan and the grid are fixed.
7. **Holdout on raw data:** `… -- holdout`. This is the Gate H result (D6).
8. **Reranker (Gate R, optional):** `… -- rerank-curve --condition normal` (also `embedder-busy`, `cold`), then
   `… -- gate-r --config <name>`. The Bridge must have the reranker switched on (Recall page).

## 2. Curated memory — Gate H for memory

Same steps with `-Tool memory-eval`: `init`, `sample --count 300` (writes sealed `seeds.jsonl`; pull it to write
queries, delete the pulled copy), write `dataset.jsonl` (≥ 250 queries), `pool` (ids only, for the judge), labels,
`validate`, `guard`, `kappa`, `ablate --split dev`, `register`, `holdout`, optionally `rerank-curve` / `gate-r`.

## 3. The sanitiser dictionary

The sanitised copy replaces names, places, organisations and handles that are listed in the dictionary, plus every
e-mail, phone number, CPR number, IBAN, card number, GPS position, URL path, non-public URL host and number with
9 or more digits. Dates, times, prices and short numbers stay.

1. `.\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -- suggest` — proper-noun candidates from your history and
   memories. Add `--llm` to let the configured LLM provider propose names too (the text goes to that provider, as in
   normal chat; nothing else sees it). Suggestions are never applied automatically.
2. Pull `dictionary.suggested.jsonl`, review it, and write the accepted terms into `dictionary.jsonl`
   (`Edit-CortexEvalFile.ps1 -Tool eval-corpus -Name dictionary.jsonl`). One JSON object per line:
   ```json
   {"term":"PersonA","kind":"person"}
   {"term":"Persona","kind":"person","entity":"persona"}
   {"term":"TownB","kind":"place"}
   {"term":"example.org","kind":"public-host"}
   ```
   Kinds: `person`, `place`, `org`, `handle`, `other`, `public-host` (a URL host that is kept). `entity` groups
   spellings, nicknames and scripts of one person under one pseudonym. Inflected forms (genitive *-s*, Russian case
   endings up to three letters) and spellings without diacritics are matched automatically.

## 4. Freeze a version

`.\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -- freeze`

Both workspaces must be registered and unchanged, and every label must point to a message or memory that still
exists. The job builds `/app/state/eval/frozen/vN` (encrypted), then runs the **leakage check**. If it prints lines
like `leak: messages.Content dictionary-term ×3`, nothing was kept: add the missing spellings to the dictionary
(another `suggest` helps) and freeze again. When it passes: `Frozen vN: … leakage check passed …`.

When the dictionary grows later, re-check older versions: `… -- check --version vN`.

## 5. Raw vs sanitised comparison (§4.2)

1. `… -Tool history-eval -- holdout --corpus frozen:vN` (and `-Tool memory-eval -- holdout --corpus frozen:vN`).
2. `… -Tool eval-corpus -- compare --version vN` (add `--tool memory-eval` for memory). The report
   (`/app/state/eval/compare/*.md`) shows nDCG@10, recall@10 and MRR on both corpora, the per-query difference with
   a 95% CI, and both confidence intervals of the pre-registered comparison. Report both numbers and the difference
   with the gate result. **The gate decision uses the raw run.** Results on a mostly pre-event-log snapshot are
   provisional (re-run after ≥ 4 weeks of live capture).

## 6. Off-machine backup in SafeExchange

Packages are attached to the **existing secret `cortex-recovery-kit`** (it also holds the storage recovery kit). Never
create, list or read any other secret for this.

1. **Export:** `.\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -PromptPassphrase -- export --version vN`. Use a
   passphrase of at least 12 characters that differs from the recovery kit's; keep it with your other offline secrets.
   The job prints the package name `cortex-eval-corpus-vN.cxpkg`, its size and SHA-256.
2. **Download:** `.\scripts\Export-CortexEvalCorpus.ps1 -Version vN` (or the download button under Storage →
   Evaluation corpora). The script prints the SHA-256; it must match step 1.
3. **Upload** with your coding assistant, as a file of `cortex-recovery-kit` named exactly `cortex-eval-corpus-vN.cxpkg`:
   * up to a few MB: `file_upload_begin` (name `cortex-recovery-kit`, fileName as above), `file_upload_chunk`
     (≤ 256 KB per chunk, index 0, 1, …, SHA-256 of each chunk, the ticket of the previous chunk),
     `file_upload_complete` (SHA-256 of the whole file);
   * larger: ask for `file_transfer_info` (name `cortex-recovery-kit`, direction `upload`) and follow it with your
     own token (`az account get-access-token` with the scope it names): create the file content, upload 4 MB chunks
     with their hashes and the ticket, commit with the whole-file hash. Do not save the URLs or scopes it returns in
     any repository.
4. **Verify:** `secret_get cortex-recovery-kit` lists the attachment with its `contentId` and SHA-256; the hash must
   match. `audit_read cortex-recovery-kit` shows the upload.
5. **Record it:** `… -Tool eval-corpus -- uploaded --version vN --content-id <contentId> --sha256 <sha256>`.
6. Delete the host copy of the package once it is uploaded (it is encrypted, but there is no reason to keep it).

## 7. When history is deleted (D3)

A deletion whose period overlaps a frozen version destroys that version on this machine (its runs and package too).
The deletion confirmation says so and names the attachment, and the Recall page then shows
**Delete these off-machine evaluation copies**. To finish:

1. With your assistant: `secret_get cortex-recovery-kit`, find the file `cortex-eval-corpus-vN.cxpkg` (or the
   recorded contentId), `file_delete` it (`confirm: true`). Check with `secret_get` and `audit_read`.
2. On the Recall page, enter the Bridge password and press **I deleted it**.
3. Freeze a new version (§4); labels that pointed to deleted messages must be removed and the workspace registered
   again first (the freeze tells you how many). Versions are never renumbered.

To drop a version yourself: `… -Tool eval-corpus -- delete --version vN` (an exported one is queued for off-machine
deletion the same way).

## 8. Evaluating elsewhere (import)

On another Cortex install (it may be unencrypted): copy the package into the agent's packages folder,
`docker cp cortex-eval-corpus-vN.cxpkg cortex-agent:/app/state/eval/packages/`, then
`.\scripts\Invoke-CortexEval.ps1 -Tool eval-corpus -PromptPassphrase -- import --package cortex-eval-corpus-vN.cxpkg`.
The import checks every file against the package's hashes and registers the sanitised datasets. Run
`holdout --corpus frozen:vN` there. The pseudonym secret never leaves the original machine, so the copy cannot be
mapped back to real names.

## 9. Without encryption at rest

The in-container commands keep working (`docker exec cortex-agent dotnet Cortex.Contained.Agent.Host.dll history-eval …`),
and so do the scripts. On an encrypted install, `docker exec` runs only `kappa` and the synthetic `history-smoke`
checks; everything else points you to `Invoke-CortexEval.ps1`.
````

`docs/security.md` — apply:

```diff
--- a/docs/security.md
+++ b/docs/security.md
@@ -97,10 +97,10 @@ reports `unavailable`. To stop history text leaving the agent process for the em
 `History__Dense__Enabled=false` (or switch **Dense embeddings for history** off on the Recall page);
 search then stays keyword-only. Inputs are sent with an explicit `num_ctx` and `truncate=false`. Vectors can leak
 content (embedding inversion), so they are treated like text: they live only in the history index
 generation (encrypted with it, erased with it under `secure_delete`, byte-level tested) and are served
-only while the source segment is unchanged. `history-eval` run directories hold raw copies of
-`messages.db`; purges destroy the ones overlapping the purged range. See
+only while the source segment is unchanged. `history-eval` run directories hold copies of
+`messages.db` (encrypted with the evaluation key on encrypted installs); purges destroy the ones overlapping the purged range. See
 [history-hybrid-search.md](history-hybrid-search.md).
 
 ## Reranker sidecar exposure
 
@@ -121,10 +121,11 @@ and their stems) next to the chunk metadata and vectors; the text itself stays i
 files. With encryption at rest the whole memory store — the index, the vectors and the `.memory.data` files — is encrypted with the tenant key (see below). Deleting or editing a memory removes its row in the write
 transaction and rewrites the index with FTS5 `optimize` under `PRAGMA secure_delete=ON`, so no
 token of the deleted text stays in the database file (byte-level integrity tests). Quarantined
 memories (D10) are excluded before scoring in keyword and semantic search. `memory-eval` searches a
-private copy of the memory store that is deleted when the command ends; its `seeds.jsonl` holds raw
-memory text and is deleted by the owner after the dataset is written. See
+private copy of the memory store (encrypted with the evaluation key on encrypted installs) that is deleted when the
+command ends; its `seeds.jsonl` holds memory text (sealed on encrypted installs) and is deleted by the owner after the
+dataset is written. See
 [memory-hybrid-search.md](memory-hybrid-search.md).
 
 ## Agent storage encryption and event payloads
 
@@ -137,8 +138,27 @@ that introduced tenant storage keys (`TenantStorageKeys` in `secrets.json`). An
 knows the key nor, before this change, preserved unknown fields when it saved the file, so the
 encrypted `messages.db` could become unreadable. Current Bridges keep unknown `secrets.json` fields
 across load/save for future downgrades. Agent images older than the Storage page cannot open encrypted stores and stop at startup rather than write plaintext; the Bridge never mints a tenant key while the agent reports encrypted data, and the agent rejects any key whose fingerprint differs from the one recorded when encryption was enabled.
 
+## Evaluation copies and the sanitised corpus
+
+The quality-gate evaluations (`history-eval`, `memory-eval`, `eval-corpus`) run as jobs inside the agent process,
+the only process that holds the storage key; the Bridge starts them after checking the owner's password again and
+refuses options that would point them at other files or endpoints. Their corpus and index copies are encrypted with
+an evaluation key derived from the tenant key (HKDF, purpose `eval`), never with a live store's key; datasets, seeds
+and the sanitiser dictionary are sealed with AES-256-GCM bound to the file name. Reports, verdicts and manifests hold
+ids and numbers only. In-container processes (including the agent's own `run_command`) never receive the key: run
+through `docker exec`, the copying commands refuse.
+
+The off-machine backup (D6) is a **sanitised** frozen corpus: names, places, organisations and handles from the owner's
+dictionary, e-mail addresses, phone, CPR, IBAN and card numbers, GPS positions, URL paths, non-public hosts and numbers
+of nine or more digits are replaced by HMAC pseudonyms under a per-version secret that never leaves the machine; a
+fail-closed leakage check deletes the version if any listed term or pattern remains. Dates, prices and short numbers
+are kept (accepted risk). The export is one passphrase-encrypted file (PBKDF2-HMAC-SHA256, 600,000 iterations;
+AES-256-GCM in authenticated 1 MiB chunks) that the owner attaches to the single SafeExchange secret
+`cortex-recovery-kit`. A history deletion overlapping a version destroys it locally and lists the attachment for the
+owner to delete. See [evaluation-runbook.md](evaluation-runbook.md).
+
 ## Content security
 
 ### Prompt injection defense
```

`docs/superpowers/specs/2026-11-21-episodic-history-memory-design.md` — apply:

```diff
--- a/docs/superpowers/specs/2026-11-21-episodic-history-memory-design.md
+++ b/docs/superpowers/specs/2026-11-21-episodic-history-memory-design.md
@@ -49,9 +49,9 @@ The work is also the foundation for the ambient-agent model: an event log as the
 | D2 | A short **maintenance window** (agent paused) is acceptable for event backfill, restore and tenant import. |
 | D3 | When history is deleted: live history and all indexes are erased physically. Curated memories derived from the deleted range are **flagged for review** by default; the owner may instead choose, per purge, to **delete them with the history** (amended 2026-11-22, Recall settings UI). Frozen evaluation snapshots that overlap the range are **destroyed and regenerated** as a new version. |
 | D4 | Encryption at rest arrives in increment 2 for `messages.db` and every index database, using **SQLite3 Multiple Ciphers** (the free SQLCipher bundle is deprecated in SQLitePCLRaw 3.0). The key is held by the Bridge (DPAPI) and pushed to the agent like other credentials. |
 | D5 | Cloud models may process history (the same trust boundary as live chat). **No LLM digests**, as a quality decision: summaries lose detail and make evaluation circular. |
-| D6 | Quality gates run on **raw local data**. The sanitised frozen copy is the off-machine backup (SafeExchange). |
+| D6 | Quality gates run on **raw local data**. The sanitised frozen copy is the off-machine backup (SafeExchange). *(2026-11-23: on encrypted installs the gates run inside the agent with encrypted copies; the sanitised copy is a file of the SafeExchange secret `cortex-recovery-kit` — see [encrypted evaluation and sanitised corpus](2026-11-23-encrypted-eval-sanitised-corpus-design.md).)* |
 | D7 | Subagents get the same history tools as the main agent, with no extra restrictions. Re-confirmed in round 2. The privacy reviewer's objection is recorded as an accepted risk. |
 | D8 | The search engine is SQLite: FTS5 BM25 plus exact vector search with filters applied before scoring, fused with RRF. ANN indexes or a new engine only follow the triggers in the scale-path record. |
 | D9 | `Internal` rows (scheduled-task instructions, worker summaries, subagent completions) are indexed, labelled `internal (not necessarily delivered)`, excluded from results by default, and included with `includeInternal=true`. |
 | D10 | Curated memories whose `CreatedAt` falls in a purged time window are **quarantined**: hidden from `memory_search`, seeding and consolidation until the owner keeps or deletes them. They are never auto-deleted unless the owner explicitly chooses, for that purge, to delete them with the history (owner opt-in, amended 2026-11-22); the delete confirmation shows a preview count and states the chosen option. Time-window quarantine remains the mechanism; event provenance for memories (which events a memory was extracted from) is a possible future step. |
```

- [ ] **Step 2: Check the documents**

Every relative link in the changed files resolves (`docs/evaluation-runbook.md`, the design spec), the runbook names only the secret `cortex-recovery-kit`, and `git grep -n -i "secret_create\|secret_list" -- docs/evaluation-runbook.md` prints nothing.

- [ ] **Step 3: Commit**

```powershell
git add docs\api-reference.md docs\architecture.md docs\encryption-at-rest.md docs\evaluation-runbook.md docs\security.md docs\superpowers\specs\2026-11-21-episodic-history-memory-design.md
git commit -m "docs: encrypted evaluation, sanitised corpus and the owner runbook" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 22: Final verification

- [ ] **Step 1: Whole solution, zero warnings**

Run: `dotnet build cortex-contained.sln -v q`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 2: The test gate**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests --no-build -v q
dotnet test tests\Cortex.Contained.Bridge.Tests --no-build -v q
dotnet test tests\Cortex.Contained.Contracts.Tests --no-build -v q
Get-ChildItem tests -Directory -Filter *.Tests | Where-Object Name -notmatch 'Agent.Host|Bridge|Contracts' | ForEach-Object { dotnet test $_.FullName --no-build -v q }
```

Expected: every project `Passed!`. Replay counts: Agent.Host 2,952, Bridge 2,023, Contracts 267. If `SubAgentSetGoalToolTests` or `MaintenanceStoreTests.PurgeOldRuns_RemovesOldCompletedRuns` fails, rerun it alone (pre-existing load flakes, Gotcha 12).

- [ ] **Step 3: Integrity gate**

Run: `dotnet test tests\Cortex.Contained.Agent.Host.Tests --filter "Category=Integrity" -v q`
Expected: `Passed!` (the `EncryptedSqliteTests` copies included).

- [ ] **Step 4: No plaintext marker left by the evaluation tests**

```powershell
dotnet test tests\Cortex.Contained.Agent.Host.Tests --no-build --filter "FullyQualifiedName~Evaluation|FullyQualifiedName~EvalCorpusTests|FullyQualifiedName~EvalCliTests" -v q
```

Expected: `Passed!`. These tests assert that `zqrawtext`, `zqmemtext` and the dictionary names never appear in any file of a run directory, frozen version or package.

- [ ] **Step 5: In-container guard on an encrypted state (no Docker needed)**

```powershell
$root = Join-Path (Get-Location) ".eval-guard"; New-Item -ItemType Directory -Force "$root\state" | Out-Null
'{"keyFingerprint":"fp","enabledAt":"2026-11-23T00:00:00Z"}' | Set-Content "$root\state\storage-encryption.json"
$env:CORTEX_STATE_PATH="$root\state"; $env:CORTEX_DATA_PATH="$root\state"
dotnet run --project src\Cortex.Contained.Agent.Host --no-build -- history-eval ablate --split dev; $LASTEXITCODE
dotnet run --project src\Cortex.Contained.Agent.Host --no-build -- history-smoke embedder-check --dir "$root\smoke" --embedder http://127.0.0.1:1; $LASTEXITCODE
Remove-Item -Recurse -Force $root
```

Expected: the first prints `history-eval cannot run in a separate process while encryption at rest is on … Invoke-CortexEval.ps1 -Tool history-eval -- ablate --split dev` and exits `3`; the second is not refused: it runs the synthetic embedder check, reports the unreachable endpoint and exits `1`.

- [ ] **Step 6: Hygiene**

```powershell
git status --short                                  # only intended files
git --no-pager diff main --stat -- version.json     # empty
git --no-pager log --oneline main..HEAD             # 21 commits, each with the Co-authored-by trailer
git grep -n -i -E "secret_create|secret_list" -- docs scripts src   # nothing
```

- [ ] **Step 7: Owner walk-through after deployment (`.\scripts\Self-Update.ps1 -Schedule -SkipPull`)**

1. Storage page: the evaluation directory shows `encrypted`; no "Evaluation corpora" card yet.
2. `.\scripts\Invoke-CortexEval.ps1 -Tool history-eval -- init`, then `-- validate`: the job reports `encrypted workspace: True` and a clear message about the missing dataset.
3. Follow `docs/evaluation-runbook.md` §§1–6 when the datasets are ready; the Storage page then lists the frozen version and offers the package download.
4. Recall page: a purge preview over the frozen range states that the version is destroyed and names `cortex-eval-corpus-vN.cxpkg` of `cortex-recovery-kit` (cancel the purge unless intended).

---

## Spec coverage

| Spec item | Task(s) |
|---|---|
| E1 jobs in the agent process; in-container refusal only for copying commands; store-free commands allowed | 7, 8, 9 |
| E2 evaluation key ring (`Derive("eval")`) | 1, 2 |
| E3 `VACUUM INTO` keyed copies, no plaintext on disk | 1, 3, 4, 13, 16 |
| E4 sealed datasets/seeds/dictionary, fail closed; reports plaintext ids and numbers | 2, 5, 6 |
| E5 files edited through the Bridge with re-authentication; identical content not rewritten | 7, 9 |
| E6 deterministic detectors + owner lexicon; shared redactor detectors; LLM only suggests | 10, 11, 14 |
| E7 HMAC pseudonyms per version, consistent across corpus, memories, datasets; collisions fail | 11, 13 |
| E8 kept/changed/dropped fields; dates and prices kept; tool names kept | 11, 13 |
| E9 fail-closed leakage check; re-check after the dictionary grows | 12, 13, 18 |
| E10 frozen version layout, registration, manifest hashes; never-reused versions | 13 |
| E11 passphrase package (PBKDF2 600k shared with the recovery kit, chunked AES-256-GCM), import with hash verification | 15, 16 |
| E12 SafeExchange single secret `cortex-recovery-kit`, upload recorded, deletion by contentId | 18, 21 |
| E13 D3: destroy overlapping versions, ledger, purge confirmation, Recall banner, acknowledgement | 19, 20 |
| E14 raw-vs-sanitised comparison report; gate decision on raw | 17, 18, 21 |
| §2 goal 4: Bridge download with password re-check and rate limit, or a host folder | 20 (`Export-CortexEvalCorpus.ps1`) |
| §2 goal 5: owner runbook | 21 |
| §5 security: option refusal, re-authentication, no key outside the agent | 7, 8, 9, 20, 22 |
