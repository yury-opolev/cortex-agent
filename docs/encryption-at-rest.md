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
