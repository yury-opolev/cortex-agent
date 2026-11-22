# Encryption at rest, complete — design

Date: 2026-11-22 · Status: proposed · Builds on: [increment 2 (event log, D4 encryption)](2026-11-21-inc2-event-log-design.md),
[recall settings UI](2026-11-22-recall-settings-ui-design.md) · Plan: [2026-11-22-encryption-at-rest-complete](../plans/2026-11-22-encryption-at-rest-complete.md)

## 1. Problem

Increment 2 added optional encryption at rest (SQLite3 Multiple Ciphers) for `messages.db` and the history
index generations, keyed by a per-tenant master key that the Bridge keeps under DPAPI and pushes over the hub.
It is not usable and not complete:

1. **No way to turn it on.** The switch is the agent setting `Storage:EncryptAtRest`. The agent has no mounted
   `cortex.yml`, and the Launcher's `ComposeFileSync` rewrites the installed compose file on every start, so an
   environment override does not survive. There is no API or UI.
2. **The curated memory store stays in plaintext**: `memory.db` (chunk metadata, sqlite-vec vectors, the FTS5
   keyword index) and every `memories/*.memory.data` content file.
3. **Other plaintext copies** of history and memory exist next to the encrypted stores (§3).
4. Nothing reports **what is encrypted**.
5. The key has **no backup**: DPAPI is the only custodian, so a Windows profile reset loses everything.
6. Nothing stops an **older or reset component** from re-creating plaintext stores or minting a new key over
   encrypted data.

## 2. Goals and non-goals

Goals:

- G1. An owner-facing, one-way switch (UI + API) with clear warnings, a preview of what gets encrypted, pre-flight
  checks (key present, disk space, backup recommendation), re-authentication and a typed confirmation.
- G2. `memory.db` encrypted with SQLite3MC and the content files with AES-256-GCM, both under the tenant key. The
  migration is crash-safe, and the plaintext is overwritten.
- G3. Every other plaintext copy is either encrypted, erased or listed as not covered, with the reason.
- G4. Status reporting: a Storage page, a hub method and a `/health` block.
- G5. An owner-initiated recovery kit (password-wrapped key file) plus a guarded restore.
- G6. Rollback and downgrade guards: no silent plaintext and no minted key.

Non-goals (YAGNI):

- Key rotation.
- Turning encryption off from the UI. Decrypting is not offered.
- Encrypting the tool sandbox (`/app/data`).
- Column-level encryption of the small operational stores (§6.6).
- Protection against an attacker with code execution inside the running agent: the key is in memory there by design.

## 3. Findings (investigation, verified in a scratch worktree)

| # | Finding | Evidence |
|---|---|---|
| F1 | The SQLite engine of the agent process is SQLite3 Multiple Ciphers. `MemoryMcp.Core` references `SQLitePCLRaw.bundle_e_sqlcipher`, but the Agent Host's `SQLite3MC.PCLRaw.bundle` provider is the one in use. `Program.cs` already refuses `MemoryMcp:Encryption:Enabled` because that path is SQLCipher plus an OS keychain. | `SqliteRuntimeInfo`; startup guard |
| F2 | **sqlite-vec loads under SQLite3MC and works on an encrypted `memory.db`.** A plaintext store with `vec0` vectors, FTS5 index, triggers and `vec_writer_meta` migrates through an export (`VACUUM INTO` a keyed sibling file, verified, then swapped; superseding the original in-place `VACUUM` + `PRAGMA rekey`) without the extension loaded: VACUUM copies virtual-table schema rows and shadow tables as plain tables. After the migration, KNN and BM25 searches return the same hits. | `EncryptedMemoryStoreTests` (Task 7) |
| F3 | The SQLite3MC build compiles `SQLITE_SECURE_DELETE`: `PRAGMA secure_delete` is `1` on a fresh connection, so every agent SQLite store (subagents, todos, scheduler, …) zeroes deleted rows. | probe + `PlaintextResidueTests` (Task 12) |
| F4 | **The Bridge keeps no message history.** It has done so since the public release ("Bridge.Storage removed — all message persistence handled by Agent Host"). A `%LOCALAPPDATA%\Cortex\messages.db` may still exist from older versions: it is SQLite3MC-encrypted with the DPAPI-held Bridge database key and unused. `mcp\actions.db` is encrypted the same way. `docs/security.md` still says otherwise. | code search; header check |
| F5 | `sessions-snapshot.json` is a **full plaintext copy of every live conversation** (history, tool calls, extraction buffer). It is written at every shutdown, including the restart that enabling encryption triggers, and restored at startup before any key exists. | `SessionSnapshotSerializer`, `AgentProcessingService` |
| F6 | **Logs held content.** The following were written to 14-day logs in the data volume: `Memory ADD/UPDATE` with the memory content (Information), full LLM responses on consolidation and extraction parse failures, LLM-written reasons and titles, the local voice channel's transcripts ("transcribed: …"), and memory titles and queries from the hub. | log template scan (Task 13) |
| F7 | `MemoryManagementService` reads `memory.db` and the content files directly (memory list, quarantine creation times), bypassing `IMemoryStore`. | code |
| F8 | memory-mcp writes content files with `File.WriteAllBytesAsync` (not atomic). | code |
| F9 | `CredentialsPusher` mints a tenant key on first push (`GetOrCreateTenantStorageKey`). If the `secrets.json` entry is lost, it would mint a new key while encrypted data exists. | code |
| F10 | `history-eval`, `history-smoke` and `memory-eval` copy the raw stores into `/app/state/eval/**`. They cannot open encrypted sources, and their old run directories hold plaintext copies. | code |

## 4. Decisions

**D1 — The switch is a one-way marker in the agent's state volume.** `EnableStorageEncryption` writes
`/app/state/storage-encryption.json` = `{ version, encryptAtRest: true, keyFingerprint, enabledAt }`. It is written
atomically and durably, then the agent stops itself, and Docker restarts it (`restart: unless-stopped`; only when
`DOTNET_RUNNING_IN_CONTAINER=true`).

At startup the effective mode is **configuration ∨ marker ∨ encrypted files detected** (header sniff of
`messages.db` and `memory.db`, the content-file magic, the snapshot magic). The source is reported as `config`,
`marker` or `detected`.

Rejected alternatives:

- Bridge `cortex.yml`. It is Bridge-wide, the agent needs the value before the hub connects, and it is lost when
  the Bridge is reset.
- `recall-settings.json`. That file holds runtime-mutable, live-applied settings and has "reset to defaults".
  Encryption needs a restart and must not be resettable.
- A live switch without a restart. The writer and pooled readers of `messages.db` would have to be swapped under
  load.

An encrypted process with no marker (configured or detected) writes one with the key fingerprint once its stores
open.

**D2 — UI.** A per-tenant **Storage** page (sidebar link and tab next to Recall) has three parts.

- **Status table.** Lists every item with its state and size, plus the Bridge-side stores.
- **Recovery kit card.** Export and restore.
- **"Turn on encryption…" modal.** Shows the preview: warnings, blockers, will encrypt, will erase, not covered,
  free space. Enabling requires:
  - a checkbox confirming a state-volume snapshot was taken;
  - either an exported kit or a checkbox accepting that losing the profile loses everything;
  - the Bridge password (re-authentication);
  - typing `ENCRYPT`.

  After enabling, the page polls the status until both stores report `open`.

**D3 — Memory store: an Agent.Host decorator, no new memory-mcp encryption API.** `EncryptedMemoryStore`
(`IMemoryStore`, `IMemoryAtRest`) opens lazily and fails closed, following the `MessageStore` pattern. It waits for
the key, then:

1. `EncryptedSqlite.Prepare(memory.db, password("memory.db"))` — migrate or verify.
2. `MemoryContentMigrator.Migrate(memories, cipher)`.
3. Construct `SqliteVecMemoryStore(options, cipher, logger, password)`.

The existing `databaseKey` and `IContentEncryptor` parameters of memory-mcp are reused unchanged.

Content files use `StorageFileCipher`:

- AES-256-GCM, with a fresh 96-bit nonce per write;
- layout `"CXE1" | nonce(12) | tag(16) | ciphertext`;
- associated data `cortex-storage-file/1\n<label>`, so a blob cannot be swapped between purposes;
- key = HKDF-SHA256(master, info `cortex-storage/memory-content`), distinct from `memory.db`, `messages.db`,
  `history-index` and `sessions-snapshot`.

A blob without the magic is refused (plaintext is never served as authenticated).

The only memory-mcp change is the atomic content write (temp file, flush to disk, rename) plus clean-up of
leftover temp files (F8).

**D4 — Crash-safe content migration.** Per plaintext file:

1. Write the ciphertext to `<file>.migrating` and flush.
2. Overwrite the plaintext file with zeros and flush.
3. Rename `.migrating` over it.

The next run recovers an interrupted conversion:

- an authenticating `.migrating` replaces the original;
- a torn one is dropped while the original plaintext is still intact;
- a torn one next to a zeroed original fails closed for manual recovery.

Before touching anything, one already-encrypted file is authenticated, so a wrong key changes no byte
(`StorageKeyMismatchException`). The database migrates first, so its key check runs first.

**D5 — Session snapshot.** The snapshot is written encrypted (`StorageFileCipher`, purpose `sessions-snapshot`)
whenever the process runs encrypted or has a restart pending (`ProtectsNewWrites`). Without the key it is
**skipped, never written in plaintext**.

An encrypted snapshot is restored once the key arrives, and the consumer loop starts after that restore, so
restored history always precedes new messages. Messages that arrive meanwhile wait in the channel. If the key
does not arrive within `KeyWaitSeconds`, processing starts and the snapshot is kept. A plaintext snapshot (written
by the pre-enable process) is restored, overwritten with zeros and deleted.

**D6 — Coverage.** What is not encrypted is listed explicitly, on the Storage page and in the docs.

| Item | Decision | Justification |
|---|---|---|
| `messages.db`, history index generations | encrypted (increment 2) | — |
| `memory.db`, `memories/*.memory.data` | **encrypted** (D3/D4) | curated memory text, vectors, keyword tokens |
| `sessions-snapshot.json` | **encrypted** (D5) | full copy of live conversations |
| `eval/**` (history-eval, history-smoke, memory-eval) | **erased on enable** (default on); the CLIs **refuse to run** while encryption is on | raw plaintext copies; an encrypted source cannot be evaluated without writing plaintext |
| agent logs (`/app/data/logs`) | **scrubbed**: no content placeholders, guarded by reflection tests | logs must never hold message or memory text |
| Bridge/channel logs | scrubbed (voice transcript), guarded | same |
| `subagents/subagents.db` | not covered | 24 h transcript retention (records 7 days), erased by history deletion, deletes zeroed (F3); the store is synchronous and opened at DI construction, so lazy encrypted open is a separate refactor (follow-up: column-level AES-GCM with the same cipher) |
| `todos.db`, `scheduler/tasks.db`, `external-agent/sessions.db` | not covered | small operational state the agent writes for itself; same follow-up |
| `voiceprints.db`, `maintenance.db` | not covered | no message text |
| `self-notes.md`, `context-bootstrap.md` | not covered | agent-authored notes injected into every prompt and edited by tools |
| `/app/data/tool-output` spill files, `/app/data/*` sandbox | not covered | the agent reads them with its file tools; spill files are deleted after 7 days and by history deletion |
| unrecognized state-volume entries (e.g. manual backups) | listed as "other" | the owner decides; the docs say to delete plaintext backups |
| Bridge `mcp\actions.db`, legacy `messages.db` | already encrypted (Bridge database key); reported | F4 |
| Bridge recording sessions (opt-in recorder: WAV and event logs) | not covered | explicit debugging feature, off by default |
| volume snapshots, host backups, provider-side copies, filesystem free space, the one-time migration copy | out of scope | outside what the application can reach (as in increment 2) |

**D7 — Status.**

- **`GetStorageEncryptionStatus`** returns the full status: mode and source, `restartPending`, `enabledAt`, key state
  (`absent`/`accepted`/`rejected`), the fingerprints (accepted and expected), `hasEncryptedData`, the
  messages/memory store states, and every item from `StorageInventory`. The inventory reads headers only: no key,
  nothing opened, nothing created.
- **`/health`** (unauthenticated) carries `subsystems.storage = { status: off|ok|degraded, encryptAtRest, source,
  restartPending, key, messages, memory }`. It has no fingerprints and no paths.
- **The Bridge status endpoint** adds the Bridge key view (present, unavailable, fingerprint, last kit export) and
  the Bridge stores.

**D8 — Recovery kit.** A JSON file in format `cortex-storage-recovery-kit/1`.

- **Cryptography.**
  - Key wrap: PBKDF2-HMAC-SHA256 with 600,000 iterations and a 128-bit salt, then AES-256-GCM.
  - Associated data: `format \n tenantId \n keyFingerprint`, so the kit cannot be relabelled.
  - Passphrase: at least 12 characters (the UI suggests four random words).
  - Opening a kit checks the fingerprint of the decrypted key and refuses fewer than 100,000 iterations.
- **Export** — `POST /storage/recovery-kit`:
  - requires the Bridge password (`SessionManager.VerifyPassword`);
  - the response is `Cache-Control: no-store`;
  - the key is never logged (only "kit exported for tenant X");
  - the export time is recorded in `secrets.json` (`TenantStorageKeyExports`, not secret).
- **Restore** — `POST /storage/recovery-kit/restore`:
  - requires the Bridge password and the kit passphrase, and the kit tenant must match;
  - **never replaces a working key**: allowed when the Bridge has no usable key, or when the agent reports that its
    data belongs to the kit's fingerprint (`expectedKeyFingerprint`);
  - refused when the kit's key differs from the agent's expected one;
  - after the restore the key is pushed. If the agent already accepted another key in this process, the owner
    restarts the agent (keys are never switched at runtime).

**D9 — Rollback and downgrade guards.**

| Scenario | Guard |
|---|---|
| Configuration reset or marker deleted, files encrypted | **Detection** turns encryption on (`detected`) and rewrites the marker; nothing is created in plaintext |
| Bridge lost its key entry (pre-increment-2 Bridge saved `secrets.json`, restored old backup) | **Push policy**: a key is minted only when the agent reports no encrypted data (`StorageKeyPushPolicy`); otherwise the agent stays locked and the log and the Storage page ask for the recovery kit |
| An increment-2 Bridge mints a key anyway | The agent's **expected fingerprint** (from the marker) rejects any other key before it opens anything (`RejectedForData`, key state `rejected`); restore then replaces the minted key |
| Older agent image on encrypted volumes | SQLite refuses a non-database file (error 26), so the old image fails at startup: it never writes plaintext over or next to the encrypted files. The marker is ignored by old images, which is why rolling back across this version is documented as unsupported. Self-Update rolls back only to the previously deployed image, which is at least this version once encryption has been enabled on it. |
| Older Bridge versions | Increment-2+ Bridges keep unknown `secrets.json` fields (`[JsonExtensionData]`), so `TenantStorageKeyExports` survives. Pre-increment-2 Bridges drop `TenantStorageKeys` when they save → recovery kit. |

**D10 — Logs.** Content placeholders (`Content`, `Text`, `Title`, `Query`, `Response`, `LlmResponse`,
`Transcript`, `Prompt`, `Summary`, `Snippet`, `Fact`, `Utterance`) are forbidden in `[LoggerMessage]` templates
of the Agent Host, Bridge and channel assemblies. Reflection tests guard this, and the offending templates log
lengths instead.

## 5. Flows

### 5.1 Enable

```
Owner (Storage page)                Bridge                                   Agent (plaintext, key pushed on connect)
 preview ───────────────────────────► POST …/encryption/preview ────────────► PreviewStorageEncryption (inventory, space)
 export kit (password, passphrase) ─► verify password; key = TryGet ∨ mint-if-no-encrypted-data
                                      kit = PBKDF2+AES-GCM; record export ◄── (status for the mint check)
 enable (password, ENCRYPT, ack) ───► verify; key; ProvideStorageKey ───────► ring accepts (or Unchanged)
                                      EnableStorageEncryption(fingerprint) ─► key fingerprint == request?
                                                                              pre-flight; erase eval/**; write marker
                                                                              RestartPending → snapshot will be encrypted
                                      ◄─────────────────────── 202 ───────── StopApplication after 2 s
                                                                              StopAsync: encrypted sessions-snapshot.json
 poll status ────────────────────────► (reconnect) push key ────────────────► Docker restarts the container
                                                                              marker ⇒ encrypted mode; ring expects fingerprint
                                                                              EncryptedStorageOpener: messages.db migrate,
                                                                              memory.db + content migrate, index rebuild
                                                                              snapshot restored, consumer loop starts
 "on · open · open" ◄──────────────── status ◄──────────────────────────────
```

### 5.2 Key push on connect (`StorageKeyPushPolicy`)

| Bridge key | Agent status | Decision |
|---|---|---|
| present | — | push |
| missing | no storage hub (older agent) | mint and push (increment 2 behaviour) |
| missing | `hasEncryptedData` | **withhold, log critical** (restore the kit) |
| missing | encryption on (config), no data | mint and push |
| missing | encryption off | withhold (minted when the owner exports a kit or enables) |
| undecryptable | — | nothing pushed (increment 2: never regenerated) |

## 6. Components

| Unit | Where | Responsibility |
|---|---|---|
| `StorageKeyFingerprint` | Contracts/Security | 8-byte HKDF key check value (hex); shared by Bridge, agent and kit |
| `SqliteFileHeader` | Contracts/Security | classify a SQLite file as plaintext/encrypted/empty/absent by its 16-byte header |
| `IStorageHub` + DTOs | Contracts/Hub | status, preview, enable |
| `StorageKeyRing` (+) | Agent/Storage | `KeyFor`/`WaitForKeyAsync` raw purpose keys, `Fingerprint`, `ExpectedFingerprint`, `RejectedForData` |
| `StorageFileCipher` | Agent/Storage | AES-GCM file format (`IContentEncryptor`) |
| `StorageEncryptionMarker`, `StorageEncryptionContext`, `StorageLayout` | Agent/Storage | the marker, mode resolution, store locations |
| `MemoryContentMigrator` | Agent/Storage | crash-safe in-place encryption of content files |
| `EncryptedMemoryStore`, `IMemoryAtRest` | Agent/Storage | lazy fail-closed encrypted memory store; access for direct readers |
| `EncryptedStorageOpener` | Agent/Storage | opens and migrates the stores when the key arrives; self-heals the marker |
| `StorageInventory`, `IDiskSpace` | Agent/Storage | header-only inventory; free space |
| `StorageEncryptionService`, `SecureErase` | Agent/Storage | status, preview, enable, restart; overwrite-then-delete |
| `EvaluationGuard` | Agent/Storage | evaluation CLIs refuse while encrypted |
| `AgentProcessingService`, `SessionSnapshotSerializer` (+) | Agent | encrypted snapshot, deferred restore |
| `SqliteVecMemoryStore` (+) | memory-mcp | atomic content writes |
| `SecretManager` (+) | Bridge/Security | `TryGetTenantStorageKey`, `ReplaceTenantStorageKey`, export timestamps |
| `StorageRecoveryKit` | Bridge/Security | kit create/open |
| `StorageKeyPushPolicy`, `CredentialsPusher` (+) | Bridge/Hosting | never mint over encrypted data |
| `HubClient.Storage`, `IStorageAgentClient` | Bridge | hub calls; test seam |
| `BridgeStorageReport` | Bridge/Storage | Bridge store states |
| `SessionManager.VerifyPassword` | Bridge/Auth | re-authentication |
| `StorageEndpoints` | Bridge/Storage | the five endpoints |
| `tenant-storage.js` + `app.html` | Bridge UI | Storage page |

## 7. Security properties (what a copy of the volumes shows)

With encryption on and the stores open once after enabling:

- **Encrypted.** `messages.db`, its WAL/SHM, the history index, `memory.db` (vectors, FTS5 tokens, titles, tags)
  and every content file are ciphertext, and so is the snapshot. A wrong key changes nothing on disk.
- **Plaintext.** The not-covered items of D6. The logs hold only identifiers, lengths and states.
- **Residue.** Deleted plaintext pages may remain in filesystem free space after the one-time migration
  (overwrite-before-unlink is best effort on journaling, copy-on-write and flash). Volume snapshots taken before
  enabling stay plaintext: the guide tells the owner to destroy them once the encrypted state is verified.
- **Key custody.** The master key lives in the Bridge's DPAPI store, in the agent's memory, and — wrapped by the
  owner's passphrase — in the recovery kit. A kit without its passphrase reveals only the fingerprint.

## 8. Validation

Integrity gate additions (`Category=Integrity`):

- **No plaintext anywhere after migration.** Markers in content, title, tags and a deleted memory are absent from
  every file under the data directory, including `-journal`, `-wal` and the content files.
- **Fail closed.** A wrong key leaves every file byte-identical. No key means no file is created.
- **Crash recovery.** Crash before the export, after the export (unverified), after verification, after the plaintext
  overwrite and after the swap: the next open completes, and no plaintext remains.
- **Snapshot.** Ciphertext only; restored only with the key; never written without it.
- **End-to-end.** Plaintext agent → enable over the hub → second host on the same volumes → key push → both
  stores open, data intact, no marker bytes in any state file.
- **Residue.** Deleted rows are zeroed by the engine.

Unit coverage: key ring, cipher, context resolution, inventory, service (enable, refusals, restart), Bridge key
custody, kit (round trip, wrong passphrase, relabelling, iterations), push policy, endpoints (re-authentication,
confirmation, mint refusal, restore rules), log hygiene.

## 9. Rollout and owner procedure

1. Deploy the build. Encryption stays off, and the Storage page shows everything as plaintext with the list of
   items not covered.
2. Take a snapshot of the state volume. The archive is plaintext: keep it offline and destroy it after
   verification.
3. Export the recovery kit. Store the file and the passphrase separately.
4. Turn on encryption. The agent restarts and migrates its stores. The history index rebuilds (recall covers recent
   messages until it is done).
5. Verify:
   - the Storage page shows every covered item `encrypted` and both stores `open`;
   - `/health` reports `storage.status = ok`;
   - a byte check finds no known phrase in the state volume;
   - recall and memory still work.
6. Delete the plaintext leftovers: the snapshot archive (after a week), manual backup folders listed as "other",
   and the legacy Bridge `messages.db` if present.

The exact commands are in [encryption-at-rest.md](../../encryption-at-rest.md).

## 10. Risks and accepted gaps

- **Irreversible from the UI.** Going back means restoring the pre-enable snapshot (which loses everything written
  since).
- **The rebuild of the history index** after enabling takes time on large histories (dense embeddings included).
- **The not-covered stores of D6** remain plaintext. They are listed, and the follow-up is column-level encryption.
- **Self-restart depends on Docker's restart policy.** Outside a container the agent reports
  `restartScheduled=false` and the owner restarts it.
- **The kit's security is the passphrase.** PBKDF2 (not memory-hard) is used because it is built into .NET. The
  600,000 iterations follow OWASP 2023 guidance.
- **A pre-existing flaky test** (`SubAgentSetGoalToolTests`) can fail under parallel load. It is unrelated; rerun it.

## 11. Coverage of the owner's request

| Request | Decisions | Plan tasks |
|---|---|---|
| (1) Turn it on: UI + API, warnings, preview, pre-flight (key, disk, backup) | D1, D2, D7 | 4, 10, 11, 16–19 |
| (2) `memory.db` (SQLite3MC; sqlite-vec under SQLite3MC verified) and content files (AES-GCM, HKDF label, per-file nonce, atomic write, migration, secure delete) | D3, D4, F1, F2 | 2, 3, 5, 6, 7, 8 |
| (3) Bridge `messages.db` | F4: no live Bridge history; legacy file and `actions.db` already SQLite3MC under the DPAPI Bridge key; reported, docs corrected | 17, 18, 21 |
| (4) Other plaintext copies (subagents, spill files, eval artifacts, logs) | D5, D6, D10, F3, F5, F6, F10 | 9, 12, 13 |
| (5) Status/health | D7 | 10, 11, 18, 19 |
| (6) Key backup: recovery kit, never logged, explicit action, re-authentication | D8 | 14, 15, 17, 18, 19 |
| (7) Rollback/downgrade guard | D9 | 2, 4, 8, 16 |
