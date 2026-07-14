# API Reference

## HTTP Endpoints

### Agent Host

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/health` | No | Health check with subsystem status |
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
