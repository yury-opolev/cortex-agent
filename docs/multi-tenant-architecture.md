# Multi-Tenant Agent Architecture

## Overview

The Bridge becomes a multi-tenant router that serves one shared web UI and routes requests
to multiple Agent containers. Each tenant has their own agent personality, memory, and
conversation history. LLM credentials and global settings are managed centrally by the Bridge.

```
                         ┌─── Agent Container (Admin)
                         │    - Web UI, Voice, Discord channels
Browser (Admin) ────┐    │    - Full personality, memory, tools
                    │    │
Discord User A ─────┼── Bridge ──┼─── Agent Container (Tenant A)
Discord User B ─────┤    │    │    - Discord channel only
Discord User C ─────┘    │    │    - Own personality, memory
                         │    │
                         │    └─── Agent Container (Eval)
                         │         - Ephemeral test tenant
                         │
                         └── Ollama (shared embeddings)
```

## Tenant Types

### Admin Tenant
- **Channels**: Web UI (chat, history, memories, settings), Voice, Discord
- **Access**: Full settings control (global + own tenant settings)
- **One admin** per deployment

### Regular Tenants  
- **Channels**: Discord only (each Discord user → own tenant)
- **Access**: Can configure their personality/wake word via Discord commands
- **Created automatically** when a new Discord user interacts with the bot

### Eval Tenant
- **Channels**: None (API-only, driven by test harness)
- **Access**: Ephemeral, created/destroyed per eval run
- **Purpose**: Self-consciousness evals, memory pipeline testing

## Configuration Split

### Global Settings (Bridge-level, one for all tenants)

| Setting | Where Configured | Notes |
|---|---|---|
| LLM provider connections | `cortex.yml` | API keys, OAuth tokens, endpoints |
| Model configuration | `cortex.yml` | Available models, context windows, fallback order |
| LLM proxy | Bridge in-memory | Token refresh, rate limiting, request routing |
| Speech settings | `cortex.yml` | TTS/STT engines, voices (admin tenant only) |
| Ollama connection | `cortex.yml` | Embedding model endpoint |
| Web UI | `cortex.yml` | Port, bind address |
| Auth / login | `cortex.yml` | Password hash, session config |
| Max sub-agent rounds | `cortex.yml` | Safety-net circuit breaker (200 default) |
| Memory defaults | `cortex.yml` | Compaction thresholds, extraction config |
| Discord bot token | `cortex.yml` | One bot, routes to per-user tenants |

### Per-Tenant Settings (per agent container)

| Setting | Where Configured | Notes |
|---|---|---|
| Personality / system prompt | Tenant config | Agent's character, tone, instructions |
| Agent name | Tenant config | Display name (e.g. "Cortex", "Friday") |
| Wake word | Tenant config | For voice activation (admin only for now) |
| Tool permissions | Tenant config | Which tools this tenant can use |
| Scheduled tasks | Tenant data | Per-tenant task store |

### Per-Tenant Data (per agent container, isolated)

| Data | Storage | Notes |
|---|---|---|
| Memory store | SQLite + embeddings | Facts, preferences |
| Conversation history | SQLite | Per-channel message history |
| Maintenance state | SQLite | Last completed dates for nightly tasks (compaction, etc.) |

## Key Architectural Changes

### 1. LLM Calls Go Through the Bridge

Currently: Agent Host → Anthropic API (with token refresh callback to Bridge)

Proposed: Agent Host → Bridge LLM Proxy → Anthropic API

**Why**: Centralizes credential management. No API keys or OAuth tokens in containers.
Eliminates token refresh callbacks, token revocation conflicts between tenants,
and the need for DPAPI secrets in containers.

**Bridge LLM Proxy**:
- `POST /internal/llm/complete` — non-streaming completion
- `POST /internal/llm/stream` — streaming completion (SSE)
- Handles provider selection, fallback, token refresh transparently
- Rate limiting per tenant possible in the future

### 2. Bridge as SignalR Router

Currently: Bridge has one `HubClient` → one Agent Hub

Proposed: Bridge has a `TenantRouter` with multiple `HubClient` instances:
- `Dictionary<string, HubClient>` keyed by tenant ID
- Each `HubClient` connects to its agent container's SignalR hub
- Inbound messages are routed by tenant ID (derived from Discord user ID or session)

### 3. Web UI Multi-Tenancy

Currently: Single-tenant, one set of pages

Proposed: Admin-only web UI with tenant context:
- **Chat**: Shows admin tenant's conversations
- **History**: Shows admin tenant's history (no cross-tenant access)
- **Memories**: Shows admin tenant's memories
- **Settings**: 
  - "Global" tab: LLM providers, models, speech, memory defaults
  - "My Agent" tab: Admin's personality, wake word, tools
  - "Tenants" tab (future): List of auto-created Discord tenants, their status

### 4. Discord Channel Routing

Currently: All Discord messages → one Agent

Proposed: Discord user ID → tenant mapping:
- First message from a new Discord user → create new tenant (auto-provision container)
- Subsequent messages → route to existing tenant's container
- The Discord bot remains one bot, one token, but routes internally

### 5. Container Lifecycle

**Admin container**: Always running, started with docker-compose

**Tenant containers**: Options (to decide):
- **Pre-provisioned pool**: N containers ready, assigned to tenants on demand
- **Dynamic spin-up**: Container created on first message, stopped after idle timeout
- **Shared process**: Multiple tenant sessions in one container (simpler but less isolated)

For MVP: start with shared process (multiple tenants in one Agent Host, isolated by data directory).
Move to container-per-tenant when scaling.

## Implementation Phases

### Phase 1: Move Conversation History to Container
- Create `MessageStore` in Agent Host (SQLite, same schema as current Bridge MessageStore)
- Agent Host persists messages to its own `messages.db` in `/app/data/`
- Add hub methods for history queries (list conversations, search messages, paginate)
- Bridge history page calls Agent Hub instead of local SQLite
- Remove `MessageStore` from Bridge
- **This is the prerequisite** — without it, tenant data is split across host and container

### Phase 2: LLM Proxy
- Add `POST /internal/llm/complete` and `/internal/llm/stream` to Bridge
- Agent Host calls Bridge LLM proxy instead of Anthropic directly
- Remove credential management (API keys, OAuth tokens) from Agent Host
- Remove token refresh callbacks from Agent Hub
- **Result**: Containers have zero credentials, Bridge manages all LLM access

### Phase 3: Multi-Container Routing
- `TenantRouter` in Bridge — maps tenant ID → container endpoint
- `docker-compose.yml` supports multiple agent containers
- Per-tenant Docker volumes
- Tenant registry in Bridge config
- Discord user ID → tenant mapping
- Admin web UI routes to admin container

### Phase 4: Tenant Lifecycle
- Auto-create tenant container on first Discord message
- Idle timeout → stop container (preserve volume)
- Admin UI: list tenants, start/stop, view status
- Eval harness: create ephemeral tenant, run scenarios, destroy

### Phase 5: Eval via API
- Eval harness connects to Bridge as a client
- Create eval tenant (fresh container)
- Send backstory conversations via API
- Run A/B scenarios
- Read memories and results via API
- Clean up

## Data Ownership

### Bridge (host-side, shared)
The Bridge is **stateless** regarding tenant data. It only stores:

```
%LOCALAPPDATA%/Cortex/           (host)
├── cortex.yml                   # Global config (LLM providers, models, speech, ports)
├── secrets/
│   └── secrets.json             # DPAPI-encrypted API keys, OAuth tokens
└── models-cache.json            # Model catalog from models.dev
```

### Per-Tenant (container-side, isolated Docker volume per tenant)
ALL tenant data lives inside the container's data volume. If the container is destroyed,
the volume preserves the data. If the volume is deleted, the tenant starts fresh.

```
/app/data/                       (container volume)
├── tenant.yml                   # Personality, agent name, wake word, tool permissions
/app/state/                      (container volume)
├── messages.db                  # Conversation history
├── scheduler/
│   └── tasks.db                 # Scheduled tasks
├── maintenance/
│   └── maintenance.db           # Maintenance task state (last run dates)
└── memory/
    └── memory.db                # Memory store (encrypted SQLCipher + sqlite-vec)
```

### Migration: Conversation History Moves to Container
Currently `messages.db` is stored by the Bridge on the host. In multi-tenant, it moves
into the agent container so all per-tenant data is co-located. The Bridge retrieves
conversation history via the Agent Hub's read API instead of reading its own local
database.

**Impact:**
- Bridge's `MessageStore` is removed
- Bridge no longer persists messages in `OnAgentResponseComplete`
- Web UI history/memory pages query the Agent Hub instead of local SQLite
- Agent Host gains a `MessageStore` for persisting its own conversation history

### Docker Volume Structure (multi-tenant)

```
cortex-admin-data      → /app/data  (admin container)
cortex-tenant-A-data   → /app/data  (tenant A container)
cortex-tenant-B-data   → /app/data  (tenant B container)
cortex-eval-data       → /app/data  (eval container, ephemeral)
cortex-ollama-data     → /root/.ollama  (shared Ollama)
```

## Open Questions

1. **Shared vs isolated embedding models**: All tenants share the same Ollama instance.
   Embedding vectors are compatible across tenants. Is this acceptable?
   (Yes — embeddings are deterministic, no cross-tenant data leakage.)

2. **Memory isolation**: Each tenant has their own SQLite DB. Should some learnings
   be shared across tenants? (Probably not initially — each agent develops independently.)

3. **Model selection per tenant**: Should tenants be able to use different models?
   (Global setting for now. Per-tenant override is a future feature.)

4. **Cost tracking**: With multiple tenants sharing one API key, how to track
   per-tenant token usage? (Bridge LLM proxy can log per-tenant usage.)

5. **Container vs process isolation**: Full container isolation is more secure
   but heavier. Shared process with data isolation is simpler.
   (Start with shared process for MVP.)

---

*Created: 2026-03-16*
*Status: Design proposal*
