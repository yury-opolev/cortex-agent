# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build entire solution
dotnet build cortex-contained.sln

# Build single project
dotnet build src/Cortex.Contained.Agent.Host/Cortex.Contained.Agent.Host.csproj

# Full dev startup (builds Docker image + starts Agent container + Bridge)
.\scripts\Start-Cortex.ps1

# Bridge-only restart (skip container rebuild)
.\scripts\Start-Cortex.ps1 -BridgeOnly

# Rebuild Docker image only
.\scripts\Build-AgentImage.ps1
```

## Tests

```bash
# All tests
dotnet test cortex-contained.sln

# Single test project
dotnet test tests/Cortex.Contained.Agent.Host.Tests

# Filter by class
dotnet test --filter "ClassName=BuiltInToolTests"

# Filter by test name
dotnet test --filter "Name~FileRead_ExistingFile_ReturnsContent"
```

xUnit + NSubstitute. Global usings for `Xunit` and `NSubstitute` are set in test .csproj files. Test methods use underscore naming (`Method_Condition_Expected`); CA1707 is suppressed in test projects via `Directory.Build.props`.

## Architecture

Two-process system communicating over SignalR:

- **Agent Host** (`src/Cortex.Contained.Agent.Host`) — runs in Docker. AI runtime, tool execution (sandboxed to `/app/data`), memory system (SQLite + sqlite-vec), task scheduler. No stored secrets — credentials pushed from Bridge at startup and held in memory only.
- **Bridge** (`src/Cortex.Contained.Bridge`) — runs on Windows host. Web UI (Kestrel on port 5080), channel management (WebChat/Discord/Voice), DPAPI secret storage, message history.
- **Contracts** (`src/Cortex.Contained.Contracts`) — shared library with typed SignalR hub contracts (`IAgentHub`/`IAgentHubClient`), DTOs, `IChannel` interface, YAML config provider.

### Key subsystems in Agent Host

| Subsystem | Role |
|-----------|------|
| `AgentRuntime` | Core loop: prompt building, LLM calls, tool execution, session management. Per-channel message serialization via `AgentMessageChannel`. |
| `DirectLlmClient` | Calls LLM APIs directly (OpenAI, Anthropic, GitHub Copilot, Ollama). Multi-provider with fallback. |
| `ToolRegistry` | Built-in agent tools (file/grep/run/memory/self-notes/scheduler/subagents/history). FrozenDictionary lookup, cached tool definitions, output truncation. |
| `MemoryExtractionService` | Background `Channel<T>` queue. Extracts facts from conversations, then consolidates via LLM (ADD/UPDATE/DELETE/NOOP). |
| `MemoryCompactionService` | Periodic sweep merging near-duplicate memories. |
| `MemoryConsolidationService` | Shared LLM-based dedup logic with `SemaphoreSlim` lock serializing all memory writes. |
| `SchedulerService` | SQLite-backed one-shot and cron recurring tasks. 10s tick interval. |
| `ModelProvider` | Shared model name provider (`IModelProvider`). Breaks circular DI between AgentRuntime and memory services. |

### Memory system flow

1. After each response: user+assistant messages appended to extraction buffer on `AgentSession`
2. Buffer accumulates throughout the conversation — no extraction while context is active
3. On compaction (65% context window, idle, manual, emergency): buffer flushed → `MemoryExtractionService` → LLM extracts facts → `MemoryConsolidationService` decides ADD/UPDATE/DELETE/NOOP
4. All memory writes serialized through consolidation lock (shared by extraction, ingest tool, compaction)

### Conversation compaction

`AgentRuntime.CompactConversationAsync` replaces history with an LLM summary when context reaches 65% of window. Triggered at 5 sites: idle compaction, regular (tool loop), emergency (context overflow), manual `/compact`, seeded history. Extraction buffer is always flushed before compaction so pending messages are extracted to long-term memory before the history is summarized.

### Cross-channel session transfer

`transfer_session` and `revert_transfer` tools let the agent move a conversation's context across channels (`"let's continue in voice"`). Uses an `ITopicSlicer` LLM call to find the topic boundary, then seeds the target session via `IAgentRuntime.TransferSessionAsync`. After seeding, emits a proactive greeting via the shared `IProactiveMessageDispatcher` — so voice channels with an absent user get the Bridge-side ring/invite flow automatically (see `docs/architecture.md` for the full picture).

## Key patterns

- **Logging**: Source-generated `[LoggerMessage]` on `partial` classes. Structured, zero-allocation.
- **DI**: Explicit constructor injection in `Program.cs` (not attribute-based). Memory services are nullable on `AgentRuntime` for backward compatibility.
- **Tools**: Implement `IAgentTool` with `Name`, `Description`, `ParametersSchema` (JSON Schema string), `ExecuteAsync`. Registered as singletons.
- **Channels**: Implement `IChannel` plus optional capability interfaces (`IChannelWithStreaming`, `IChannelWithPairing`). Consumers check via pattern matching.
- **Config**: YAML with `${ENV_VAR:-default}` substitution. Runtime-mutable memory settings via `IOptionsMonitor` + `MemorySettingsStore`.
- **LLM JSON parsing**: `PropertyNameCaseInsensitive = true` with camelCase JSON in prompts. Use `MemoryConsolidationService.StripToJson()` to strip markdown fences.
- **Thread safety**: `AgentSession.syncLock` for history and extraction buffer. Per-channel `SemaphoreSlim` in `AgentRuntime` for message serialization.

## Project layout

```
src/
  Cortex.Contained.Contracts/        Shared interfaces, DTOs, SignalR contracts
  Cortex.Contained.Agent.Host/       AI agent (Docker) — runtime, tools, memory
  Cortex.Contained.Bridge/           Windows service — web UI, channels, secrets
  Cortex.Contained.Channels.*/       Channel implementations (WebChat, Discord, Voice)
  Cortex.Contained.Speech/           STT/TTS engines (Whisper, Kokoro, Silero)
  Cortex.Contained.Common/           Shared Windows utilities
lib/
  memory-mcp/                        Semantic memory library (git submodule)
tests/
  *.Tests/                           Unit tests (xUnit + NSubstitute)
  Cortex.Contained.Evals/            LLM evaluation tests
scripts/
  Start-Cortex.ps1                   Dev startup (build + run all)
  Build-AgentImage.ps1               Docker image build with versioning
```

## Reference docs

- [Architecture overview](docs/architecture.md) — system components, data flow, persistence, Docker setup
- [Multi-tenant architecture](docs/multi-tenant-architecture.md) — tenant isolation, routing, provisioning
- [Personality architecture](docs/personality-architecture.md) — persona/personality storage, delivery, and editing
- [API reference](docs/api-reference.md) — REST API endpoints
- [Security](docs/security.md) — threat model, DPAPI secrets, sandbox, auth
- [MCP plugin system](docs/mcp-plugin-system.md) — host-side MCP servers (stdio + HTTP) as native agent tools; how the catalog/invocations ride the Bridge↔agent SignalR hub; host-side auth (DPAPI / OAuth 2.1)
- [Design reference](docs/design-reference.md) — design patterns and conventions
- [Setup guide](docs/setup-guide.md) — installation and configuration

## Build configuration

- **SDK**: .NET 10, `<LangVersion>latest</LangVersion>`
- **TreatWarningsAsErrors**: enabled globally
- **AnalysisLevel**: `latest-recommended`
- **Central package management**: `Directory.Packages.props`
- **Windows-specific projects**: Bridge, Voice, Common target `net10.0-windows`
- **Docker**: Multi-stage Dockerfile, `cortex-agent:latest` image, Ollama sidecar for embeddings
