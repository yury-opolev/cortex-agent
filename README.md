# cortex-agent

A personal AI assistant that runs locally on your Windows machine. The AI agent lives inside a Docker container for isolation; a companion Windows service (the Bridge) connects it to the outside world — web UI, Discord, voice, and more.

## How it works

```
Browser / Discord / Voice
        |
  Cortex.Contained.Bridge  (Windows host — manages channels, credentials, web UI)
        |
    SignalR (token-authenticated)
        |
  Cortex.Contained.Agent.Host  (Docker container — AI runtime, tools, memory)
        |
    LLM APIs  (OpenAI, Anthropic, GitHub Copilot, Ollama)
```

- **Agent Host** runs in Docker with no stored secrets. LLM credentials are pushed from the Bridge at startup and held in memory only.
- **Bridge** runs on the Windows host as a service (or console app in dev). It manages channel connections, serves the web UI, and stores all secrets via DPAPI encryption.
- **Ollama sidecar** provides local embeddings for the semantic memory system.

## Features

- Multi-provider LLM support with automatic fallback (OpenAI, Anthropic, GitHub Copilot, Ollama)
- Semantic long-term memory with automatic extraction, deduplication, and compaction
- Channels: web chat, Discord (text + voice messages), local voice (wake word + push-to-talk)
- Sandboxed file system and shell access inside the container
- Task scheduling (one-shot and recurring)
- Speech: Whisper STT, Kokoro/Silero/Windows SAPI TTS
- Web-based settings UI with runtime-configurable memory thresholds

## Tech stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 10, ASP.NET Core |
| Real-time | SignalR (typed hub contracts) |
| Container | Docker + Docker Compose |
| Memory store | SQLite + sqlite-vec (vector search) |
| Embeddings | Ollama (qwen3-embedding, 1024d) |
| Secret storage | Windows DPAPI |
| Speech | Whisper.net, Kokoro ONNX, Silero ONNX |
| Discord | Discord.Net |
| Testing | xUnit, NSubstitute |
| Logging | Serilog (with sensitive-data redaction) |

## Requirements

- Windows 10/11
- .NET 10 SDK
- Docker Desktop with Compose v2

## Quick start

```powershell
# 1. Start the agent container + Ollama
docker-compose up --build

# 2. In another terminal, start the Bridge
dotnet run --project src/Cortex.Contained.Bridge
```

The web UI will be available at `http://localhost:5080`. On first run, a setup wizard walks you through LLM provider configuration.

See [docs/setup-guide.md](docs/setup-guide.md) for full instructions.

## Project structure

```
src/
  Cortex.Contained.Contracts/           Shared interfaces and DTOs
  Cortex.Contained.Agent.Host/          AI agent (runs in Docker)
  Cortex.Contained.Bridge/              Windows service + web UI
  Cortex.Contained.Channels.WebChat/    Web chat channel
  Cortex.Contained.Channels.Discord/    Discord channel
  Cortex.Contained.Channels.Voice/      Voice I/O channel
  Cortex.Speech/              STT/TTS engines
lib/
  memory-mcp/                Semantic memory library (git submodule)
tests/
  Cortex.*.Tests/             Unit and integration tests
  Cortex.Contained.Evals/               LLM evaluation tests
```

## Documentation

| Document | Description |
|----------|-------------|
| [Architecture](docs/architecture.md) | System design, components, data flow, memory system, data persistence |
| [API Reference](docs/api-reference.md) | SignalR hub methods, REST endpoints, built-in tools |
| [Security](docs/security.md) | Threat model, authentication, secret storage, content security |
| [Setup Guide](docs/setup-guide.md) | Prerequisites, configuration, running, testing |
| [Design Reference](docs/design-reference.md) | Architecture decision records and design rationale |

## License

See [LICENSE](LICENSE).
