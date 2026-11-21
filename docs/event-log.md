# Event log and provenance (episodic history memory, increment 2)

Every stored message, tool call, tool result and send gets a content-free event in
`/app/state/messages.db` (`Events` table) with an evidence ID (`ev_…`). Design:
[`superpowers/specs/2026-11-21-inc2-event-log-design.md`](superpowers/specs/2026-11-21-inc2-event-log-design.md).

## What is recorded

| Event | When | Payload |
|---|---|---|
| `message_received` / `message_sent` / `internal_note` / `system_note` | every `Messages` insert (same transaction) | role, category, length, SHA-256 — never the text |
| `message_corrected` / `message_updated` | barge-in truncation / tool-call patch (same transaction) | field, length, SHA-256 |
| `legacy_import` | tenant import, append import, explicit backfill | as above |
| `tool_call` / `tool_result` | every tool execution | metadata always; args ≤ 500 / digest ≤ 1,000 chars only for allowlisted tools in a conversation |
| `delivery_attempted` / `_acknowledged` / `_unknown` | `send_message`, transfer greeting, direct replies | channel, via, evidence IDs, state |
| `history_purged` | every purge (tombstone, kept) | scope, counts, purge generation |

Every event carries its run (`RunId`, author kind and ID): main turn, scheduled task, timer or
subagent. A delivery links to the run's tool results (delivery → run → call → result).

## Tool payload policy

Exact tool-name allowlist, default deny (`Events/ToolPayloadPolicy.cs`). `mcp__*` payloads are
dropped entirely; recall tools store a hash only; file, page and mail bodies are never stored.
Persisted text is redacted (`SensitiveDataRedactor.RedactForPersistence`): known secrets, CPR,
IBAN, cards, emails, Danish phones, GPS. Until the Bridge has pushed credentials, text is stored
as `[REDACTION_UNAVAILABLE]`. A test fails when a new tool is not classified.

## Delivery reports

A message delivered into a conversation from elsewhere (scheduled task, timer, another channel)
is preceded by a system-recorded report naming the producer, run, send state and evidence IDs.
The model is never shown a reconstructed tool call. A tool result cleared from context reads
`[Old tool result content cleared — evidence ev_…; event_get returns what was recorded]`.

## `event_get`

`event_get { id: "ev_…", includeInternal? }` returns the event, the current text of the message it
describes and its linked evidence, fenced as historical data. Hidden with `history_search` when the
recall tools are off.

## Explicit backfill

Rows stored before the event log get `legacy_import` events with
`.\scripts\Invoke-HistoryBackfill.ps1 [-TenantId default] [-BridgeUrl http://localhost:5080]`.
It runs in a maintenance window (recall and purges wait), is idempotent (a second run reports
`Inserted = 0`) and never resurrects deleted history.

## Encryption at rest

`Storage:EncryptAtRest: true` encrypts `messages.db` and every history index generation with
SQLite3 Multiple Ciphers. The per-tenant master key lives in the Bridge's DPAPI secrets file (never
regenerated if it cannot be decrypted) and is pushed to the agent before credentials on every
connect; the agent holds it in memory only and derives one passphrase per database.

| Situation | Behaviour |
|---|---|
| Key not pushed yet | requests wait `Storage:KeyWaitSeconds` (120) then fail; no file is created; `/health` → `history.storage: awaitingKey` |
| Plaintext `messages.db` | encrypted in place on first open (VACUUM + rekey); plaintext index generations are erased and rebuilt |
| Wrong key | store stays locked, file untouched; `/health` → `failed` |

Enable it only after the increment 2 build has run healthily (an older image cannot read the
encrypted files), and keep it on. Losing the Windows profile that holds the DPAPI key loses the
history — back it up. The `history-smoke` CLI reads a plaintext copy: run it before enabling.

## Integrity gate

```powershell
dotnet test tests/Cortex.Contained.Agent.Host.Tests --filter "Category=Integrity"
```
