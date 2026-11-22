# Architecture

## Overview

Cortex is a two-process system: an AI agent running inside a Docker container, and a Windows service (Bridge) running on the host. They communicate over SignalR.

```
+------------------------------------------------------+
|  Windows Host                                        |
|                                                      |
|  Browser (localhost:5080)                             |
|       |                                              |
|  Cortex.Contained.Bridge (Windows Service)                      |
|    - Web UI (Kestrel)                                |
|    - Channels: WebChat, Discord, Voice               |
|    - Secret storage (DPAPI)                          |
|    - Message history (encrypted SQLite)              |
|       |                                              |
+-------|----------------------------------------------+
        | SignalR (token-authenticated, port 5100)
+-------|----------------------------------------------+
|  Docker Container                                    |
|                                                      |
|  Cortex.Contained.Agent.Host (ASP.NET Core)                     |
|    - LLM runtime (DirectLlmClient)                   |
|    - Tool execution (sandboxed to /app/data)         |
|    - Memory system (SQLite + sqlite-vec)             |
|    - Task scheduler                                  |
|    - Memory extraction + compaction (background)     |
|       |                                              |
|  Ollama sidecar                                      |
|    - Local embeddings (qwen3-embedding:0.6b, 1024d)  |
+------------------------------------------------------+
        |
    LLM APIs (OpenAI, Anthropic, GitHub Copilot, Ollama)
```

## Components

### Cortex.Contained.Contracts

Shared library referenced by both Agent Host and Bridge. Contains:
- `IAgentHub` / `IAgentHubClient` -- typed SignalR hub contracts
- DTOs: `HubInboundMessage`, `ResponseChunkMessage`, `MemoryConfig`, `LlmCredentials`, etc.
- `IChannel` interface and channel types
- YAML configuration provider with `${ENV_VAR:-default}` substitution

### Cortex.Contained.Agent.Host

ASP.NET Core app running inside Docker. Key subsystems:

| Subsystem | Description |
|-----------|-------------|
| `AgentRuntime` | Core agent loop -- builds prompts, calls LLM, executes tools, manages sessions |
| `DirectLlmClient` | Calls LLM APIs directly using credentials pushed from Bridge |
| `ToolRegistry` | 16 built-in tools (file ops, shell, memory, scheduler, messaging) |
| `MemoryExtractionService` | Background service -- extracts facts from conversations, resolves conflicts |
| `MemoryCompactionService` | Periodic sweep -- finds and merges near-duplicate memories |
| `MemoryConsolidationService` | Shared LLM-based dedup logic (ADD/UPDATE/DELETE/NOOP decisions) |
| `SchedulerService` | One-shot and recurring task scheduling (SQLite-backed) |
| `ModelProvider` | Shared model name provider (avoids circular DI with `IAgentRuntime`) |
| `MemorySettingsStore` | Runtime-mutable memory settings with `IOptionsMonitor` change tokens |
| `IHybridMemorySearch` / `QuarantineAwareHybridSearch` | `memory_search` in dense, keyword (FTS5/BM25) or hybrid (RRF) mode on the engine shared with history search (`MemoryMcp.Core.Retrieval`); quarantined memories excluded before scoring (see [memory-hybrid-search.md](memory-hybrid-search.md)) |
| `HistoryProjector` / `HistoryRecallService` | Episodic history locator: journal-fed contentless FTS5 index plus exact dense search over transcript chunks (hybrid, RRF), verified `history_search` / `history_read` (see [history-locator.md](history-locator.md), [history-hybrid-search.md](history-hybrid-search.md)) |
| `HistoryPurgeService` | History erasure workflow (index, WAL, tool output, subagents, smoke and history-eval run artifacts) and D10 memory quarantine |
| `Events` (event log) | Content-free provenance events for every message, tool call/result and send; delivery reports, `event_get`, backfill (see [event-log.md](event-log.md)) |

### Cortex.Contained.Bridge

Windows service (or console app in dev) running on the host:

| Subsystem | Description |
|-----------|-------------|
| `HubClient` | SignalR client connecting to Agent Hub |
| `Worker` | Service lifecycle -- connect, push credentials, keep-alive |
| `ChannelManager` | Channel lifecycle and message routing |
| `SecretManager` | DPAPI-encrypted secret storage |
| `MessageStore` | Encrypted SQLite message history |
| Web UI | Static files + REST API + WebChat SignalR hub |

### Channel implementations

| Channel | Library | Description |
|---------|---------|-------------|
| `Cortex.Contained.Channels.WebChat` | SignalR | Browser-based chat UI |
| `Cortex.Contained.Channels.Discord` | Discord.Net | Text + voice messages, guild support |
| `Cortex.Contained.Channels.Voice` | NAudio + Whisper.net | Local mic/speaker, wake word, push-to-talk |

### Cortex.Contained.Speech

Shared STT/TTS library used by Voice channel and Discord voice:

| Engine | Type | Notes |
|--------|------|-------|
| Whisper.net | STT | Local, ggml-base model (~142MB) |
| Kokoro | TTS | ONNX, cross-platform (default) |
| Silero | TTS | ONNX, multilingual (v5 Russian + CIS base) |
| Windows SAPI | TTS | Built-in Windows voices |

## Communication flow

### Inbound message (e.g., Discord -> Agent)

1. Discord.Net receives message in Bridge process
2. `DiscordChannel` converts to `HubInboundMessage`
3. `HubClient` sends via SignalR to Agent Hub
4. `AgentHub` validates token, passes to `AgentRuntime`
5. `AgentRuntime` pre-fetches relevant memories (embedding search, ~50ms)
6. `AgentRuntime` calls LLM via `DirectLlmClient` (using in-memory credentials)
7. Response streams back via `OnResponseChunk` SignalR callbacks
8. Bridge routes response to originating channel
9. Background: `MemoryExtractionService` extracts and stores new facts

### Credential flow

```
Bridge                          Agent Host
  |                                |
  |-- ProvideCredentials --------->|  (API keys, endpoints, provider config)
  |   (at connection startup)      |  [stored in memory only]
  |                                |
  |                                |-- HTTP POST --> LLM API
  |                                |   (Authorization header from in-memory creds)
```

Credentials are never persisted inside the container.

## Cross-channel session transfer

The agent can move a conversation's in-memory session from one channel to another when the user asks (e.g. *"let's continue in voice"*). Implemented as two built-in tools:

- **`transfer_session(target_channel)`** — runs an internal LLM call (the *topic slicer*) to identify the latest topic boundary in the source history, then seeds the target channel's session with a structured summary of pre-topic context plus the verbatim recent exchange. The source session is left untouched. Symmetric UI breadcrumbs (`↳ Continued from {source}` in target, `→ Continued in {target}` in source) are written to `MessageStore` with category `Transfer`. After seeding, the tool emits a single proactive greeting to the target channel via the same path `send_message` uses, so voice channels without the user present get the ring-with-invite flow for free (see *Proactive voice delivery* below).
- **`revert_transfer([channel])`** — restores the target channel's pre-transfer history from an in-memory snapshot captured at transfer time. Snapshots are dropped on session reset and on agent restart; only the most recent transfer per channel can be reverted.

Key types:

| Component | Responsibility |
|-----------|----------------|
| `TransferSessionTool` | Orchestrator: validation, per-target concurrency lock, slicer call, payload build, dispatcher call, breadcrumb writes. |
| `ITopicSlicer` / `LlmTopicSlicer` | Single LLM call → `TopicSliceOutcome` (Success with optional degraded fallback, or Failure). |
| `TransferSessionOptions` | Runtime-tunable model / temperature / prompt override, bound from `Agent:TransferSession`. |
| `IAgentRuntime.TransferSessionAsync` / `RevertTransferAsync` | Owns the session-mutation side: drain extraction buffer, replace history, snapshot management. |
| `IProactiveMessageDispatcher` | Shared abstraction for "push a proactive message to a channel via Bridge + persist + record." Used by both `SendMessageTool` and `TransferSessionTool` so voice-ring fallback is automatic. |
| `MessageCategory.Transfer` | New enum value (= 4). Visible in chat UI with a distinct badge, excluded from re-seeding. |

### Proactive voice delivery (`ProactiveVoiceCoordinator`)

When a proactive message targets a Discord voice channel and the user isn't present, the Discord channel's `ProactiveVoiceCoordinator` (loaded by the Bridge process, lives in `src/Cortex.Contained.Channels.Discord/`):

1. Joins the configured voice channel
2. Creates a short-lived Discord invite
3. DMs the invite URL to the linked user (a "ring")
4. Queues subsequent proactive messages until the user joins or the TTL expires
5. On join → queued messages are spoken via TTS
6. On TTL expiry → queued messages are sent as Discord voice-message DM attachments and the bot leaves voice

This is reused by both `send_message` and `transfer_session`, so the user always gets a reasonable delivery path regardless of presence.

### Proactive delivery trace (`AgentRuntime.BuildProactiveDeliveryGroup`)

A proactive send is issued from a session the *target* conversation cannot see — an ephemeral `scheduled-{taskId}` run, or a fired timer's focused `IntentComposer` run. After the tool loop, the delivered text is injected into the target channel's session history. Injecting it as a bare assistant message left it with no cause: the model found no tool call behind its own words and concluded it had fabricated them, retracting true messages to the user ("those sleep numbers were not real; I made no tool call"). Gluing it onto the previous assistant turn made it worse, producing single messages that read *"…ninety seconds resting. / Rest is up."* — an in-context example the model then reproduced in one turn, announcing a timer the moment it set it.

So a delivery is recorded as a complete, well-formed turn (never glued):

| Role | Type | Content |
|------|------|---------|
| `user` | `ProactiveDeliveryTrace` | `[automated notice — the user did not say this]` + the trigger (`scheduled task …` / `timer …`) and the fact it ran in its own session |
| `assistant` | `ProactiveDeliveryTrace` | tool call: `send_message(channel, text)` — the real tool name and argument shape |
| `tool` | `ProactiveDeliveryTrace` | delivery receipt: where, when, "already received — do not retract" |
| `assistant` | `Proactive` | the delivered text, verbatim — the only visible message |

`ProactiveDeliveryTrace` is internal (hidden from the chat UI and from re-seeding), so the explanation never leaks into what the user sees — the constraint that ruled out annotating the text itself. The group opens on a `user` message because the target conversation usually ends on an assistant turn and consecutive assistant messages are rejected by OpenAI/Copilot. Receipts are exempt from `ContextManager.PruneToolResults`.

A timer run that produces text but never calls `send_message` delivered *nothing*: only the trigger notice plus an internal `[nothing was delivered]` marker is recorded, and the undelivered wording is dropped rather than written back as agent speech. For the same reason a proactive turn's own narration is persisted as `MessageCategory.Internal` — the user never received it.

## Current time stamp

The model has no clock of its own, so every user-role message in its prompt starts with the time it was sent, in the owner's local time zone (the user's message, a scheduled-task or timer trigger, a voice turn, a subagent task or follow-up, a message drained mid-turn):

```
[Sunday 22 November 2026, 16:45 (Europe/Paris, UTC+01:00)]
```

Invariant English, minute precision, IANA zone id and the UTC offset of that instant (so DST is right). The latest user message's stamp is therefore effectively "now". The zone is the owner's — `History:TimeZone` (the container receives it as `CORTEX_TIMEZONE` → `History__TimeZone`; empty means the machine's local zone) — resolved once by `OwnerClock`, which also owns the `TimeProvider` used for testability.

The design is shaped by the provider prompt cache (OpenAI/Copilot are stateless with automatic prefix caching), which keys on the request prefix. Rendering a message differently from one request to the next would make everything after it cache-cold, so the stamp is a pure function of the message itself:

- **Each message is stamped from its OWN `LlmMessage.Timestamp`.** A message renders byte-identically on every request it appears in — later tool rounds, a message drained mid-turn, the next turn, a restart — so the whole earlier request stays a valid cached prefix. There is no in-memory state to lose or to keep in sync. Main-agent timestamps are the queued message's own time (also what `messages.db` records); seeded history keeps the Bridge's timestamps; subagent messages use the `OwnerClock` time when created.
- **Not stamped:** compaction summaries (nobody sent them), messages with no timestamp (`default`, e.g. history seeded without times), and non-user roles. If a request's final message is not a user message it gets no extra stamp.
- **The stamp exists only in the request being built.** `OwnerClock.StampUserMessages` swaps stamped *copies* into the prepared list (`PromptAssembler.BuildPromptAsync` for the main agent, `SubagentCallbacks.PrepareMessagesAsync` for subagents). Session history, `messages.db`, the extraction buffer, compaction input and events never contain it.
- **The system prompt holds only static text.** `OwnerClock.SystemPromptNote` (appended to the main and subagent prompts) says each user message starts with the time it was sent, supplied by the system, that the latest one is the current time, and that delivery reports are in UTC and must be converted using the offset.

Set `Agent:CurrentTimeStamp:Enabled: false` to turn off both the stamps and the system-prompt note (read at startup). Timestamps inside other content — notably delivery reports, which are rendered in UTC — are not rewritten. Within one long tool loop the latest stamp is when the user message was sent, not the instant of each model call.

## Memory system

### Pre-fetch (before each LLM call)

```
User message -> embed (~30ms) -> sqlite-vec search (~20ms) -> inject into system prompt
```

Zero LLM cost. Returns top 5 memories above 0.4 similarity.

### Extraction (after each response, background)

1. LLM extracts salient facts from the conversation as JSON
2. For each fact: embed, search for similar existing memories
3. LLM decides: ADD (new), UPDATE (modify existing), DELETE (outdated), or NOOP (already known)
4. Execute the decision via `IMemoryService.IngestAsync(force: true)`

### Duplicate guard (on every `IngestAsync` call)

Embedding-based pre-check before storing. If similarity to any existing memory exceeds the configurable `DuplicateThreshold` (default 0.90), the ingest is rejected. Bypassed with `force: true` (used by the consolidation pipeline which does its own LLM-based dedup).

### Compaction (periodic background sweep)

`MemoryCompactionService` runs on a configurable schedule. Finds clusters of near-duplicate memories (above `SimilarityThreshold`, default 0.70) and merges them through the consolidation pipeline.

### Image aging

Images in messages older than the Nth-from-end user-role message — where N = `ImageAging.PreserveRecentTurns` (default 4, configurable under the `Agent:ImageAging` section of config) — are stripped at LLM prep time. Only user messages consume the budget; assistant and tool messages do not, so tool-heavy turns don't inflate the count. When `ImageAging.DescribeOnStrip` is true (default), the stripped image is replaced with an LLM-generated textual description produced by `IImageDescriber` using `IModelProvider.MemoryModel` (the cheap-model slot). Descriptions are cached on `LlmContentBlock.ImageDescription` so they're computed at most once per image. Both settings are runtime-mutable via `MemorySettingsStore` (same mechanism as memory tuning knobs). Setting `PreserveRecentTurns = 0` disables image aging entirely.

### Runtime configuration

Memory thresholds (`DuplicateThreshold`, `CompactionSimilarityThreshold`, `CompactionEnabled`) are configurable at runtime via the web UI settings page. Changes flow through:

```
Web UI -> PUT /api/memory/settings -> Bridge -> SignalR UpdateMemoryConfig
  -> AgentHub -> MemorySettingsStore -> IOptionsMonitor invalidation
  -> MemoryService reads new CurrentValue on next call
```

## Task scheduler

SQLite-backed (`/app/state/scheduler/tasks.db`). Tick interval: 15 seconds. Recurring tasks use drift-free scheduling. Completed tasks purged after 7 days.

## Data persistence

### Inside Docker container

| Data | Path | Format |
|------|------|--------|
| Messages | `/app/state/messages.db` | SQLite (WAL, `secure_delete`; SQLite3MC-encrypted with `Storage:EncryptAtRest`), incl. `Events`, `HistoryJournal`, `Meta`, memory-quarantine tables |
| History index | `/app/state/history/history-index.<gen>.db` + `history-index.current` | Contentless FTS5 plus chunk vectors (derived, no message text; vectors erased and encrypted with the generation) |
| Smoke-eval workspace | `/app/state/eval/smoke` | Local only, never committed; `runs` destroyed by overlapping purges |
| History-eval workspace | `/app/state/eval/hybrid` | Local only, never committed; `runs` (raw copies) destroyed by overlapping purges |
| Memories (encrypted DB) | `/app/state/memory/memory.db` | SQLite (SQLCipher) + sqlite-vec |
| Maintenance state | `/app/state/maintenance/maintenance.db` | SQLite |
| Scheduled tasks | `/app/state/scheduler/tasks.db` | SQLite |
| Personality | `/app/data/personality.md` | Markdown |
| Agent logs | `/app/data/logs/agent-*.log` | Rolling, 14-day retention |
| Sandbox files | `/app/data/*` | Anything created by agent tools |

### Outside container (Bridge, `%LOCALAPPDATA%\Cortex`)

| Data | Path | Format |
|------|------|--------|
| Secrets (all credentials) | `secrets\secrets.json` | DPAPI-encrypted JSON |
| Message history | `messages.db` | Encrypted SQLite |
| Configuration | `cortex.yml` | YAML |
| Bridge logs | `logs\bridge-*.log` | Rolling, 30-day retention (redacted) |
| Speech models | `models\` | Downloaded binaries (Whisper, Kokoro, Silero) |

### Docker volumes

| Volume | Mount | Mode | Purpose |
|--------|-------|------|---------|
| `cortex-data` | `/app/data` | read-write | All agent runtime data |
| `cortex-config` | `/app/config` | read-only | Configuration |
| `ollama-data` | `/root/.ollama` | read-write | Ollama model weights |

## Docker setup

Two services in `docker-compose.yml`:
- **cortex-agent** -- multi-stage Dockerfile with `final` (minimal) and `common` (full toolchain + Homebrew) targets
- **ollama** -- `ollama/ollama:latest` with 4GB memory limit

Development override (`docker-compose.override.yml`) bind-mounts source code and uses `dotnet watch` for hot-reload.
