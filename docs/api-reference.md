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
| GET | `/health` | No | Health check (returns hub connection status) |
| GET | `/api/setup/status` | No | Check if initial setup is needed |
| GET | `/api/setup/providers` | No | List available LLM provider templates |
| POST | `/api/setup/copilot-auth` | No | Start GitHub OAuth device flow |
| POST | `/api/setup/copilot-poll` | No | Poll GitHub OAuth token endpoint |
| POST | `/api/setup/fetch-models` | No | Fetch models from a provider API |
| POST | `/api/setup/save` | No | Save setup configuration |
| GET | `/api/settings` | Yes | Get current settings |
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

Update agent config at runtime. Fields: `systemPrompt`, `maxTokens`, `temperature`.

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

Tool lifecycle event. Fields: `conversationId`, `toolName`, `status` (Started/Completed/Failed), `input`, `output`, `duration`.

#### `OnStatusChanged(AgentStatusInfo)`

Agent status change notification.

#### `OnError(AgentErrorMessage)`

Error during processing. Fields: `conversationId`, `errorCode`, `message`, `isRetryable`.

#### `OnConversationUpdated(ConversationInfo)`

Conversation metadata change (title, message count).

#### `ProvideCredentials(LlmCredentials)`

Bridge pushes LLM credentials to agent at startup and on reconnect. Fields: `providerId`, `apiKey`, `baseUrl`, `models`, `apiType`, `priority`. Agent holds these in memory only.

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
