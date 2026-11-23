# API Reference

## HTTP Endpoints

### Agent Host

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/health` | No | Health check with subsystem status (incl. `storage`: encryption mode and whether the encrypted stores are open) |
| GET | `/` | No | Liveness check ("Cortex Agent Host is running.") |

### Bridge

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/health` | No | Health check: `{ healthy, timestamp, version, metrics, mcpActions }`. `metrics` (agent-reported, incl. `metrics.subagents` pool aggregate) and `mcpActions` (Bridge-local action-state aggregate) are both optional and degrade to `null` + a logged warning on failure — a probe failure never flips `healthy` |
| GET | `/api/setup/status` | No | Check if initial setup is needed |
| GET | `/api/setup/providers` | No | List available LLM provider templates |
| POST | `/api/setup/copilot-auth` | No | Start GitHub OAuth device flow |
| POST | `/api/setup/copilot-poll` | No | Poll GitHub OAuth token endpoint |
| POST | `/api/setup/fetch-models` | No | Fetch models from a provider API |
| POST | `/api/setup/save` | No | Save setup configuration |
| GET | `/api/settings` | Yes | Get current settings, incl. `maxConcurrentSubagents` and its `maxConcurrentSubagentsLimits` (1-50, default 5) |
| POST | `/api/settings` | Yes | Update settings. `maxConcurrentSubagents` outside [1, 50] returns `400` without mutating, persisting, or pushing it (rejected, never clamped) |
| GET | `/api/tenants/{id}/personality` | Yes | Get tenant personality |
| PUT | `/api/tenants/{id}/personality` | Yes | Update tenant personality |
| DELETE | `/api/tenants/{id}/personality` | Yes | Reset tenant personality to default |
| GET | `/api/memory` | Yes | List all memories |
| GET | `/api/memory/{id}` | Yes | Get a specific memory |
| POST | `/api/memory` | Yes | Create a memory |
| PUT | `/api/memory/{id}` | Yes | Update a memory |
| DELETE | `/api/memory/{id}` | Yes | Delete a memory |
| POST | `/api/memory/search` | Yes | Semantic memory search |
| GET | `/api/memory/settings` | Yes | Get memory configuration |
| PUT | `/api/memory/settings` | Yes | Update memory configuration (runtime) |
| GET | `/api/messages/{channelId}` | Yes | Get message history for a channel |
| DELETE | `/api/tenants/{tenantId}/history` | Yes | Delete all (or `?olderThan=`) history through the erasure workflow. `?dependentMemories=review` (default) quarantines the memories created in that period, `delete` erases them with the history. Returns `{ deletedCount, erasureComplete, cleanupPending, quarantinedMemories, deletedMemories, dependentMemories, message }` |
| DELETE | `/api/tenants/{tenantId}/channels/{channelId}/history` | Yes | Same for one channel (previously `204`; now returns the same body; same `dependentMemories` query) |
| PUT | `/api/tenants/{tenantId}/history/recall-tools` | Yes | Body `{ enabled }`: show/hide `history_search` / recall-mode `history_read` at runtime (no effect unless `History:Enabled`) |
| POST | `/api/tenants/{tenantId}/history/event-backfill` | Yes | Run the explicit event backfill (maintenance window). Returns `{ scanned, inserted, elapsedMs }`. Driven by `scripts/Invoke-HistoryBackfill.ps1` and the Recall page |
| GET | `/api/tenants/{tenantId}/memories/quarantine` | Yes | Memories quarantined after a history deletion (D10), awaiting review |
| POST | `/api/tenants/{tenantId}/memories/{memoryId}/keep` | Yes | Keep a quarantined memory (visible to the agent again) |
| GET | `/api/tenants/{tenantId}/recall/settings` | Yes | Effective recall settings of the tenant's agent: `{ historyEnabled, historySearchMode, historyDenseEnabled, memorySearchMode, rerankHistoryTopN, rerankHistoryMaxDocChars, rerankMemoryTopN, rerankMemoryMaxDocChars, rerankTimeoutMs, overridden[] }`. `503` while the agent is disconnected |
| PUT | `/api/tenants/{tenantId}/recall/settings` | Yes | Patch (null members unchanged; `resetToDefaults: true` drops every override first). Persisted in the agent's state volume (`recall-settings.json`) and applied live. Returns `{ applied, errors[], notices[], settings }`; `400` with the same body when validation fails |
| GET | `/api/tenants/{tenantId}/recall/status` | Yes | `{ connected, agent: { settings, history, dense, reranker, eventBackfill, quarantinedMemories }, reranker: { enabled, sidecar: { state, problem, at } } }` (`503` with `connected: false` and the reranker block while disconnected) |
| POST | `/api/tenants/{tenantId}/recall/purge/preview` | Yes | Body `{ channelId?, from?, olderThan?, dependentMemories? }`. Deletes nothing. Returns `{ messages, dependentMemories, rangeFrom, rangeTo, confirmations: { review, delete } }` |
| POST | `/api/tenants/{tenantId}/recall/purge` | Yes | Same body plus optional `expectedMessages` (the preview count); deletes through the erasure workflow (`from` inclusive, `olderThan` exclusive; `from ≥ olderThan` is `400`). `409` when more than 50 messages beyond `expectedMessages` are now in scope — preview again. Returns the history DELETE body |
| GET | `/api/tenants/{tenantId}/recall/quarantine` | Yes | `?offset=0&limit=200` (limit 1–500). `{ items: [{ memory, purgeId, purgedAt, purgeChannelId, windowFrom, windowTo }], total, offset, limit }`, newest first; only the page loads memory content |
| GET | `/api/tenants/{tenantId}/recall/quarantine/ids` | Yes | `{ ids: [...] }` — every quarantined memory id (no content), for "select all"; the UI keeps/deletes them in sequential batches of 1000 |
| POST | `/api/tenants/{tenantId}/recall/quarantine/keep` | Yes | Body `{ memoryIds: [...] }` (1–1000). Keeps the quarantined ones; returns `{ affected, skipped[] }` |
| POST | `/api/tenants/{tenantId}/recall/quarantine/delete` | Yes | Same body; erases the quarantined ones in one batch (`DeleteManyAsync`); ids not in quarantine are skipped |
| GET | `/api/recall/reranker` | Yes | Bridge-wide reranker switch and sidecar state `{ enabled, sidecar: { state, problem, at } }` |
| PUT | `/api/recall/reranker` | Yes | Body `{ enabled }`. Persists `reranker: enabled` in `cortex.yml`, then (in the background) starts the sidecar and grants every agent, or withdraws the grant and stops the sidecar |
| GET | `/api/recall/embeddings-gpu` | Yes | Bridge-wide "run embeddings on the GPU" switch, the sidecar state and where it really runs: `{ enabled, sidecar: { state, problem, at }, mode, processor }` — `mode` is `gpu`/`cpu` (the running container's device request) or null when it is not running; `processor` is the `ollama ps` PROCESSOR (`100% GPU`) or null while no model is loaded. `sidecar.problem` is set when the GPU start failed and the sidecar fell back to the CPU |
| PUT | `/api/recall/embeddings-gpu` | Yes | Body `{ enabled }`. Persists `embeddings: gpu` in `cortex.yml` (`500` if it cannot be written; the switch is restored), then (in the background) recreates the `embeddings` container with or without `docker-compose.embeddings-gpu.yml`; falls back to the CPU when the GPU start fails |
| GET | `/api/tenants/{tenantId}/storage/status` | Yes | `{ connected, agent: StorageEncryptionStatusDto, bridge: { keyPresent, keyUnavailable, keyFingerprint, recoveryKitExportedAt }, bridgeStores: [{ id, label, state, detail }] }` (`503` with `connected: false` and the Bridge view while disconnected). Never returns key material |
| POST | `/api/tenants/{tenantId}/storage/encryption/preview` | Yes | Changes nothing. `{ preview: { alreadyEnabled, keyFingerprint, willEncrypt[], willErase[], notCovered[], requiredFreeBytes, availableFreeBytes, requiredTempBytes, availableTempBytes, blockers[], warnings[] }, bridge }` |
| POST | `/api/tenants/{tenantId}/storage/encryption/enable` | Yes + password | Body `{ password, confirm: "ENCRYPT", eraseEvaluationArtifacts = true, acceptNoRecoveryKit = false }`. `403` wrong password; `400` without the confirmation or without an exported kit (unless accepted); `409` when the agent holds encrypted data the Bridge has no key for, or refuses the key. Pushes the key, then `202` with `{ accepted, alreadyEnabled, restartScheduled, errors[] }` (one-way; the agent restarts) |
| POST | `/api/tenants/{tenantId}/storage/recovery-kit` | Yes + password | Body `{ password, passphrase }` (passphrase ≥ 12). Returns the kit file (`application/json`, `Cache-Control: no-store`) and records the export time |
| POST | `/api/tenants/{tenantId}/storage/recovery-kit/restore` | Yes + password | Body `{ password, passphrase, kit }`. Installs the kit key only when the Bridge has no usable key or the agent reports its data belongs to the kit's key; `409` otherwise; `400` wrong passphrase or another tenant's kit. Returns `{ restored, fingerprint, applied, message }` |
| POST | `/api/tenants/{tenantId}/eval/jobs` | Yes + password | Body `{ password, tool, args[], passphrase? }` (`tool`: `history-eval`, `memory-eval`, `eval-corpus`). Starts the command inside the agent: `202` with the job, `409` when refused (another job runs, storage locked, unknown tool, or `--dir`/`--source`/`--labels`/`--embedder`/`--reranker`), `403` wrong password. Rate limited (`reauth`) |
| GET | `/api/tenants/{tenantId}/eval/jobs` | Yes | `{ jobs: [EvaluationJobDto] }`, newest first |
| GET | `/api/tenants/{tenantId}/eval/jobs/{jobId}` | Yes | `{ id, tool, command, state, exitCode, encrypted, startedAt, finishedAt, output }` (`state`: `running`, `succeeded`, `failed`, `cancelled`, `refused`); `404` unknown |
| POST | `/api/tenants/{tenantId}/eval/jobs/{jobId}/cancel` | Yes | `{ cancelled }` |
| POST | `/api/tenants/{tenantId}/eval/files/read` | Yes + password | Body `{ password, tool, name }` (allow-listed workspace files). `{ exists, sealed, content }`, `Cache-Control: no-store`. Rate limited |
| POST | `/api/tenants/{tenantId}/eval/files/write` | Yes + password | Body `{ password, tool, name, content }` (≤ 8 Mi characters). Seals datasets and the dictionary on an encrypted install; identical content is not rewritten. `{ written, unchanged, sealed }`. Rate limited |
| GET | `/api/tenants/{tenantId}/eval/frozen` | Yes | `{ versions[], packages[], pendingOffsiteDeletions[] }` (ids, counts, file names) |
| POST | `/api/tenants/{tenantId}/eval/frozen/packages/{name}` | Yes + password | Body `{ password }`. Streams a passphrase-encrypted `*.cxpkg` package (`application/octet-stream`, `Cache-Control: no-store`); `404` unknown, `400` not a package name. Rate limited |
| POST | `/api/tenants/{tenantId}/eval/frozen/{version}/offsite-deleted` | Yes + password | Body `{ password }`. Records that the owner deleted the version's attachment from SafeExchange; `404` when none is pending. Rate limited |
| POST | `/api/channels/{type}` | Yes | Update channel configuration |
| GET | `/api/mcp/actions` | Yes | List approval-gated MCP mutation actions (filter by `serverKey`, `toolName`, `state`, `workerId`; paged with `before`/`limit`) |
| GET | `/api/mcp/actions/{actionId}` | Yes | One action, including its canonical arguments (for review) and result |
| POST | `/api/mcp/actions/{actionId}/approve` | Yes | Approve, bound to the exact `argumentsHash`. Body: `{ argumentsHash, reason?, expiresAtUtc? }` (default TTL 1h) |
| POST | `/api/mcp/actions/{actionId}/reject` | Yes | Reject. Body: `{ argumentsHash, reason? }` |
| POST | `/api/mcp/actions/{actionId}/cancel` | Yes | Cancel a proposed/approved action, or request cancellation of a dispatching one. Body: `{ argumentsHash }` |
| POST | `/api/mcp/actions/{actionId}/reconcile` | Yes | Record the real-world outcome of an `outcome_unknown` action. Body: `{ argumentsHash, outcome: "succeeded"\|"failed", evidence, remoteReference? }` |
| GET | `/api/tenants/{tenantId}/operations/subagents` | Yes | Live, content-free subagent worker-pool snapshot (page + pool-wide aggregate). Query: `limit`, `includeTerminal`, `staleAfterSeconds`. Returns `503` while the agent is disconnected |
| GET | `/api/operations/mcp-actions` | Yes | Content-free MCP action history/aggregate (Bridge-local — stays available while the agent is disconnected). Query: `tenantId`, `beforeId`, `limit`, `serverKey`, `toolName`, `outcome`, `workerTaskId` |

The `/api/mcp/actions*` decision endpoints share one HTTP status contract: `400` malformed input,
`404` absent action, `409` stale `argumentsHash` or an invalid state transition, `410` expired.
Every decision requires the caller to pass the action's *current* `argumentsHash` — approving,
rejecting, cancelling, or reconciling with a stale hash never mutates anything. See
[MCP plugin system → Approval-gated mutations](mcp-plugin-system.md#approval-gated-mutations-invocation-identity-and-reliability-guarantees)
for the full lifecycle and canonicalization rules.

## SignalR Hub

**Endpoint**: `/hub/agent`
**Auth**: Bearer token via `access_token` query parameter or `Authorization` header.

### Hub Methods (Bridge -> Agent)

#### `Ping` -> `HealthInfo`

Health check. Returns `{ healthy, timestamp, version }`.

#### `SendMessage(HubInboundMessage)` -> `SendMessageResult`

Send a user message for processing.

| Field | Type | Description |
|-------|------|-------------|
| `conversationId` | string | Target conversation |
| `channelType` | string | Source channel (`webchat`, `discord`, `voice`) |
| `channelId` | string | Channel instance ID |
| `senderIdHash` | string | Hashed sender identity |
| `text` | string | Message text |
| `attachments` | MediaAttachment[]? | File/image attachments |
| `timestamp` | DateTimeOffset | When message was sent |
| `correlationId` | string? | Tracing ID |

Returns `{ accepted, conversationId, rejectionReason }`.

#### `CreateConversation(CreateConversationRequest)` -> `ConversationInfo`

Create a new conversation for a channel.

#### `GetConversations()` -> `ConversationInfo[]`

List all active conversations.

#### `GetHistory(conversationId, limit)` -> `HubChatMessage[]`

Get message history. Each message has `messageId`, `role`, `text`, `timestamp`, `toolCalls`, `usage`.

#### `GetStatus()` -> `AgentStatusInfo`

Agent status: `Idle`, `Processing`, `Streaming`, `Error`, `ShuttingDown`. Includes active conversation count, current model, uptime.

#### `DeleteConversation(conversationId)`

Delete a conversation and its history.

#### `AbortGeneration(conversationId)`

Cancel an in-progress LLM generation.

#### `UpdateConfig(AgentConfigUpdate)`

Update agent config at runtime. Fields: `systemPrompt`, `maxTokens`, `temperature`,
`maxConcurrentSubagents` (nullable — pushed after initial connection, after watchdog
reconstruction, and after every reconnect so a restarted Agent Host always converges on the
Bridge's persisted value, 1-50).

#### `GetSubagentSnapshots(SubagentSnapshotQuery)` -> `SubagentObservabilitySnapshot`

Live, content-free subagent worker-pool snapshot — backs
`GET /api/tenants/{tenantId}/operations/subagents`. Never returns prompt, message history, result,
or eval text.

#### `GetMemoryConfig()` -> `MemoryConfig`

Get current memory settings: `duplicateThreshold`, `compactionSimilarityThreshold`, `compactionEnabled`.

#### `UpdateMemoryConfig(MemoryConfig)`

Update memory settings at runtime. Takes effect immediately (no restart).

#### Memory Management

| Method | Description |
|--------|-------------|
| `ListMemories(offset, limit)` | Paginated memory listing |
| `GetMemory(memoryId)` | Get a specific memory |
| `CreateMemory(request)` | Create a memory (title, content, tags) |
| `UpdateMemory(request)` | Update a memory |
| `DeleteMemory(memoryId)` | Delete a memory |
| `SearchMemories(request)` | Semantic search (query, limit, minScore, tags) |
| `ListQuarantinedMemories()` | Memories hidden by the D10 quarantine, awaiting review |
| `KeepQuarantinedMemory(memoryId)` | Keep a quarantined memory |

#### `PurgeHistory(HistoryPurgeRequestDto)` -> `HistoryPurgeResultDto`

Delete history (optional `channelId`; optional `from` (inclusive) and `olderThan` (exclusive);
none = everything) through the erasure workflow. `dependentMemories` is `review` (default:
curated memories created in the purged period are quarantined) or `delete` (they are erased
with the history by the `dependent-memories` erasure step; until it completes they stay
quarantined). Returns `{ deleted, erasureComplete, cleanupPending[], quarantinedMemories,
deletedMemories, dependentMemories }`. An inverted range or an unknown action is a `HubException`.
All other delete methods (`DeleteConversation`, `ClearAllMessages`, `DeleteMessagesOlderThan`,
`DeleteChannelMessagesOlderThan`, `ClearAll`, `ImportMessages` with `clearExisting`) use the
same workflow (always `review`).

#### Recall (`IRecallHub`)

| Method | Description |
|--------|-------------|
| `GetRecallSettings()` -> `RecallSettingsDto` | Effective settings (configured defaults + persisted overrides; `overridden[]` names the overrides) |
| `UpdateRecallSettings(RecallSettingsPatchDto)` -> `RecallSettingsUpdateResultDto` | Validate, persist (`<state>/recall-settings.json`) and apply live; returns errors or notices (index rebuild, tools hidden, …) |
| `GetRecallStatus()` -> `RecallStatusDto` | History index build, dense embedding progress, reranker health, event backfill, quarantined count |
| `PreviewHistoryPurge(HistoryPurgeRequestDto)` -> `HistoryPurgePreviewDto` | Messages and dependent memories a purge would affect; deletes nothing |
| `ListQuarantineReview(offset, limit)` -> `QuarantineReviewListDto` | One page of quarantined memories with the purge (id, time, channel, window) that quarantined each, plus `Total` |
| `ListQuarantineReviewIds()` -> `string[]` | Every quarantined memory id (no content) |
| `KeepQuarantinedMemories(ids)` / `DeleteQuarantinedMemories(ids)` -> `QuarantineDecisionResultDto` | Bulk keep / bulk erase (one `DeleteManyAsync` batch); at most 1000 ids; ids not in quarantine are returned as `skipped` |

#### Storage (`IStorageHub`)

| Method | Description |
|--------|-------------|
| `GetStorageEncryptionStatus()` -> `StorageEncryptionStatusDto` | Mode and source (`off`/`config`/`marker`/`detected`), restart pending, key state and fingerprints, store states, every item (header-only inventory) |
| `PreviewStorageEncryption()` -> `StorageEncryptionPreviewDto` | What enabling would encrypt, erase and leave alone; disk-space pre-flight; changes nothing |
| `EnableStorageEncryption(StorageEncryptionEnableRequestDto)` -> `StorageEncryptionEnableResultDto` | Requires the pushed key with the given fingerprint; erases evaluation copies on request; writes the one-way marker; stops the agent so Docker restarts it encrypted |

#### Evaluation (`IEvaluationHub`)

| Method | Description |
|--------|-------------|
| `StartEvaluation(EvaluationStartRequestDto)` -> `EvaluationJobDto` | Runs `history-eval`, `memory-eval` or `eval-corpus` in the agent process (encrypted-workspace mode on an encrypted install); one job at a time; refusals come back as state `refused` |
| `GetEvaluationJob(jobId)` / `ListEvaluationJobs()` / `CancelEvaluationJob(jobId)` | Job state and output (ids, numbers, paths; ≤ 64 KiB) |
| `ReadEvaluationFile(EvaluationFileRequestDto)` / `WriteEvaluationFile(EvaluationFileWriteDto)` | Allow-listed workspace files, sealed ones decrypted / sealed |
| `GetFrozenCorpusStatus()` -> `FrozenCorpusStatusDto` | Frozen versions, export packages, pending off-machine deletions |
| `ReadEvaluationPackage(name, offset, count)` -> `EvaluationPackageChunkDto` | ≤ 4 MiB of a passphrase-encrypted package |
| `AcknowledgeOffsiteDeletion(version)` -> `bool` | The owner deleted the version's SafeExchange attachment |

`PreviewHistoryPurge` also returns `frozenCorpusVersions` (versions the purge destroys) and `GetRecallStatus` returns
`pendingOffsiteDeletions`.

#### `SetHistoryRecallTools(enabled)` -> `bool`

Show or hide the history recall tools at runtime; returns the effective state.

#### `BackfillHistoryEvents()` -> `EventBackfillResultDto`

`legacy_import` events for messages stored before the event log; idempotent.

#### `ProvideStorageKey(StorageKeyGrant)` -> `bool`

The tenant storage master key (Base64, 256-bit), pushed before `ProvideCredentials`. `false` when
malformed or different from the key already in use.

### Hub Callbacks (Agent -> Bridge)

#### `OnResponseChunk(ResponseChunkMessage)`

Streaming text chunk. Fields: `conversationId`, `text`, `sequenceNumber`, `isComplete`, `correlationId`.

#### `OnResponseComplete(ResponseCompleteMessage)`

Full response after streaming. Fields: `conversationId`, `messageId`, `fullText`, `timestamp`, `usage`.

#### `OnToolExecution(ToolExecutionMessage)`

Tool lifecycle event. Fields: `conversationId`, `toolName`, `status` (Started/Completed/Failed), `input`, `output`, `duration`. For any `mcp__*` tool, `input`/`output` are replaced with a fixed `[redacted MCP payload]` placeholder (`McpTelemetrySanitizer`) before this message is sent — the LLM itself still received the real result over the normal tool-result message; only this telemetry stream is redacted.

#### `OnStatusChanged(AgentStatusInfo)`

Agent status change notification.

#### `OnError(AgentErrorMessage)`

Error during processing. Fields: `conversationId`, `errorCode`, `message`, `isRetryable`.

#### `OnConversationUpdated(ConversationInfo)`

Conversation metadata change (title, message count).

#### `ProvideCredentials(LlmCredentials)`

Bridge pushes LLM credentials to agent at startup and on reconnect. Fields: `providerId`, `apiKey`, `baseUrl`, `models`, `apiType`, `priority`. Agent holds these in memory only.

#### `InvokeMcpTool(McpToolInvocation)` -> `McpToolResult`

The agent's `mcp__{server}__{tool}` proxy tools call this to run one MCP tool on the host. Every
invocation carries a stable `InvocationId` (`Guid.CreateVersion7`); the result's `Outcome` is one
of `Succeeded`, `Failed`, `Cancelled`, or `OutcomeUnknown` (ambiguous post-dispatch failure — never
auto-retried). A mutation-classified tool never dispatches here directly; it returns
`AwaitingApproval` content instead. See
[MCP plugin system](mcp-plugin-system.md#approval-gated-mutations-invocation-identity-and-reliability-guarantees).

#### `CancelMcpTool(McpToolCancellation)`

Best-effort cancellation of an in-flight MCP invocation by its `InvocationId`. A no-op if the
invocation already completed or is unknown to the Bridge.

#### `GetMcpActionStatus(McpActionStatusRequest)` -> `McpActionStatusResponse`

Backs the `mcp_action_status` agent tool: look up one approval-gated action by id.

#### `CancelMcpAction(McpActionCancelRequest)` -> `McpActionCancelResponse`

Backs the `mcp_action_cancel` agent tool: cancel one action, bound to its exact
`argumentsHash`. Proposed/approved actions cancel immediately; a dispatching action only records
the request and asks the active invocation to cancel — if dispatch already reached the remote
server, the action resolves to `outcome_unknown`, never `cancelled`.

## Built-in Agent Tools

### File System (sandboxed to `/app/data`)

| Tool | Description |
|------|-------------|
| `file_read` | Read file contents (with optional offset/limit) |
| `file_write` | Create or overwrite a file |
| `file_edit` | Find-and-replace within a file |
| `file_delete` | Delete a file |
| `file_list` | List directory contents |
| `file_find` | Search for files by glob pattern |

### Execution

| Tool | Description |
|------|-------------|
| `run_command` | Execute a shell command in the sandbox |

### MCP actions (approval-gated mutations)

| Tool | Description |
|------|-------------|
| `mcp_action_status` | Look up the current state of one approval-gated MCP mutation action by `action_id` |
| `mcp_action_cancel` | Cancel a proposed/approved action, or request cancellation of a dispatching one, bound to `action_id` + `arguments_hash` |

### Memory

| Tool | Description |
|------|-------------|
| `memory_ingest` | Save a new memory (title + content) |
| `memory_get` | Get a specific memory by ID |
| `memory_update` | Update an existing memory |
| `memory_delete` | Delete a memory |
| `memory_search` | Semantic similarity search |

### Scheduler

| Tool | Description |
|------|-------------|
| `schedule_task` | Create a one-shot or recurring task |
| `list_tasks` | List all tasks with status and next execution |

### Communication

| Tool | Description |
|------|-------------|
| `send_message` | Send a message to a specific channel/conversation |

### Utility

| Tool | Description |
|------|-------------|
| `date_time` | Get current date/time in any timezone |

### History (on by default; off with `History:Enabled=false`)

| Tool | Description |
|------|-------------|
| `history_list_channels` | List channels with message counts and last activity |
| `history_search` | Find when/where something was said (keyword, phrase, `stem*`; `order=relevance\|time`; coverage-limited, verified hits with honest labels) |
| `history_read` | Read the exact source: `channelId` window, or `aroundMessageId` from a `history_search` hit |
| `event_get` | Read one recorded event by evidence ID (`ev_…`): tool calls and result digests, deliveries and their evidence, or a message with its current text |
