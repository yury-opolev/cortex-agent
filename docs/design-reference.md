# Design Reference

Architecture decision records and design rationale.

## ADR-001: SignalR over raw WebSocket

**Status**: Accepted

SignalR provides automatic reconnection, typed hub contracts (compile-time safety), streaming support, built-in auth middleware, and MessagePack binary protocol option. No custom protocol or frame parsing needed.

## ADR-002: Separate Bridge (host) from Agent (container)

**Status**: Accepted

Two-process split for security isolation:
- **Agent Host** in Docker — contains LLM logic, tools, memory. If compromised, attacker is contained.
- **Bridge** on Windows — manages channels, credentials, web UI. Runs as Windows Service for auto-start.

Channel credentials and API keys stay on the host. The agent receives only what it needs via SignalR at runtime.

## ADR-003: Channel interface segregation

**Status**: Accepted

Base `IChannel` interface plus optional capability interfaces (`IChannelWithPairing`, etc.). Consumers check `if (channel is IChannelWithX x)` — clean pattern, compile-time safety, each channel implements only what it supports.

## ADR-004: Hash sender IDs before forwarding to container

**Status**: Accepted

Bridge hashes sender IDs (Discord user IDs, etc.) before sending to the agent. Reduces PII in the container. Bridge maintains the hash-to-real-ID mapping for outbound replies.

## ADR-005: YAML configuration with env var substitution

**Status**: Accepted

YAML for human-readability. Custom `YamlConfigurationProvider` supports `${VAR}` / `${VAR:-default}` substitution. Secrets reference env vars and are encrypted via DPAPI on the Bridge side.

## ADR-006: Windows Service for the Bridge

**Status**: Accepted

.NET Worker Service with `UseWindowsService()`. Auto-starts on boot, built-in restart-on-failure, Windows Event Log integration. Runs as console app in development.

## ADR-007: Direct LLM calls from Agent (supersedes proxy architecture)

**Status**: Accepted (supersedes original proxy design)

Original design had all LLM calls proxied through the Bridge. Changed to: Bridge pushes credentials to agent via SignalR, agent calls LLM APIs directly.

**Why**: Eliminates extra SignalR hop per LLM request, simplifies streaming, reduces Bridge complexity. Credentials are still stored on the host (DPAPI) and held in agent memory only.

## ADR-008: Embedding-based duplicate guard in MemoryService

**Status**: Accepted

`IMemoryService.IngestAsync` performs an embedding similarity check before storing. Configurable threshold (default 0.90), returns `IngestResult` with rejection reason and similar memories. Can be bypassed with `force: true` (used by the consolidation pipeline which does its own LLM-based dedup).

## ADR-009: IOptionsMonitor for runtime-mutable memory settings

**Status**: Accepted

`MemoryService` uses `IOptionsMonitor<MemoryMcpOptions>` instead of `IOptions<T>` so `DuplicateThreshold` can be updated at runtime without restart. A `MemorySettingsStore` singleton holds overrides, with `IPostConfigureOptions` applying them and `IOptionsChangeTokenSource` invalidating the monitor cache.

## ADR-010: IModelProvider to avoid circular DI

**Status**: Accepted

Memory services (compaction, consolidation, extraction) need the configured LLM model name, but `IAgentRuntime` depends on those same services. `IModelProvider` is a simple interface returning the default model, injected into memory services to break the circular dependency.

## Design origins

The architecture was informed by investigating [OpenClaw](https://github.com/nicholasgriffintn/openclaw), a TypeScript multi-channel AI gateway. Key patterns adopted:
- Gateway-centric architecture (single control plane)
- Channel adapter pattern with standard interfaces
- Session routing with bindings
- Security defaults (loopback binding, token auth, rate limiting)
- Content security (prompt injection markers, homoglyph folding)

Key adaptations for Cortex:
- SignalR instead of raw WebSocket
- ASP.NET Core instead of Node.js
- Docker isolation instead of Node sandbox containers
- DPAPI for secrets instead of config files
- Simpler initial channel scope (WebChat + Discord + Voice)
