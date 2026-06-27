# Setup Guide

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/) (10.0.101+)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) with Compose v2
- Windows 10/11 (Bridge uses DPAPI for secret storage)

```powershell
dotnet --version    # 10.0.101 or later
docker --version    # 20.10+ with Compose v2
```

## Quick start (no API keys)

### 1. Create configuration

```powershell
copy cortex.example.yml cortex.yml
```

### 2. Start the Agent Host + Ollama

```powershell
docker-compose up --build
```

This starts the agent container and the Ollama sidecar. The dev override (`docker-compose.override.yml`) is applied automatically:
- `ASPNETCORE_ENVIRONMENT=Development`
- Dev hub token (`dev-token-change-me`)
- Hot-reload via `dotnet watch`
- Source code bind-mounted for live changes

The agent auto-pulls the `qwen3-embedding:0.6b` model on first startup.

### 3. Verify

```powershell
curl http://localhost:5100/health
```

### 4. Start the Bridge

```powershell
dotnet run --project src/Cortex.Contained.Bridge
```

Open `http://localhost:5080` in your browser. On first run, the setup wizard walks through LLM provider configuration.

## Full setup (with LLM providers)

### Environment variables

```powershell
$env:CORTEX_HUB_TOKEN = "your-secure-token-here"
```

The Bridge and Agent Host must share a hub token. Set `CORTEX_HUB_TOKEN` before running `docker-compose up`.

### Provider configuration

Edit `cortex.yml` or use the web UI setup wizard. Example:

```yaml
llmProviders:
  - name: openai
    api: openai-completions
    apiKey: ${OPENAI_API_KEY}
    models: [gpt-4o, gpt-4o-mini]

  - name: anthropic
    api: anthropic-messages
    apiKey: ${ANTHROPIC_API_KEY}
    models: [claude-sonnet-4-20250514]

  - name: github-copilot-api
    api: github-copilot-api
    models: [claude-sonnet-4, gpt-4o]

  - name: ollama
    api: openai-completions
    baseUrl: http://localhost:11434/v1
    models: [llama3.2]

llmProxy:
  fallbackOrder: [openai, anthropic, github-copilot-api, ollama]
```

API keys are encrypted via DPAPI on the Bridge side, then pushed to the agent at runtime.

### Provider types

| `api` value | Provider |
|-------------|----------|
| `openai-completions` | OpenAI / OpenAI-compatible (Ollama, etc.) |
| `anthropic-messages` | Anthropic Claude |
| `github-copilot-api` | GitHub Copilot API (OAuth device flow) |
| `mock` | Mock provider (for dev/testing) |

## Configuration reference

All configuration lives in `cortex.yml`. The YAML provider supports environment variable substitution:

```yaml
apiKey: ${OPENAI_API_KEY}           # required -- fails if unset
apiKey: ${OPENAI_API_KEY:-}         # optional -- empty string if unset
port: ${WEB_PORT:-5080}             # with default value
```

### Key settings

| Setting | Default | Description |
|---------|---------|-------------|
| `agentHubUrl` | `http://localhost:5100/hub/agent` | Agent Hub SignalR endpoint |
| `webUi.enabled` | `true` | Enable web chat UI |
| `webUi.port` | `5080` | Web UI port |
| `webUi.bindAddress` | `127.0.0.1` | Web UI bind address |
| `llmProviders[].api` | -- | Provider type (see table above) |
| `llmProviders[].apiKey` | -- | API key (DPAPI-encrypted at rest) |
| `llmProxy.fallbackOrder` | -- | Provider failover priority |
| `speech.sttEngine` | `whisper` | STT engine |
| `speech.ttsEngine` | `kokoro` | TTS engine |
| `channels.discord.botToken` | -- | Discord bot token |

### Agent Host settings (`appsettings.json` inside container)

| Setting | Default | Description |
|---------|---------|-------------|
| `MemoryMcp.DataDirectory` | `/app/state/memory` | Memory storage path |
| `MemoryMcp.DuplicateThreshold` | `0.90` | Duplicate guard similarity threshold |
| `MemoryMcp.Ollama.Endpoint` | `http://ollama:11434` | Ollama endpoint |
| `MemoryMcp.Ollama.Model` | `qwen3-embedding:0.6b` | Embedding model |
| `MemoryMcp.Ollama.Dimensions` | `1024` | Embedding dimensions |
| `MemoryCompaction.Enabled` | `true` | Enable periodic compaction |
| `MemoryCompaction.SimilarityThreshold` | `0.70` | Compaction merge threshold |

Memory thresholds can also be changed at runtime via the web UI settings page.

## Running tests

```powershell
# All unit tests (excludes integration and evals)
dotnet test --filter "FullyQualifiedName!~Integration&FullyQualifiedName!~Evals"

# Specific project
dotnet test tests/Cortex.Contained.Agent.Host.Tests
dotnet test tests/Cortex.Contained.Bridge.Tests
dotnet test tests/Cortex.Contained.Contracts.Tests
dotnet test lib/memory-mcp/tests/MemoryMcp.Core.Tests

# Integration tests (requires Agent Host running)
dotnet test tests/Cortex.Contained.Integration.Tests

# Eval tests (requires Ollama + LLM -- manual/on-demand)
dotnet test tests/Cortex.Contained.Evals
```

The build enforces zero warnings (`TreatWarningsAsErrors`) with `AnalysisLevel=latest-recommended`.

## Upgrading a local MSIX install

The Launcher owns the full lifecycle — containers included. Do **not** stop
processes or run `docker compose` manually; letting the Launcher recreate the
containers on start is part of verifying the release.

```powershell
# 1. Build everything: bumps version, builds Docker images + signed MSIX
.\scripts\Build-All.ps1 -CertThumbprint <thumbprint>

# 2. Install in place — stops the running Launcher/Bridge automatically
Add-AppxPackage -Path artifacts\CortexLauncher-<version>.msix -ForceApplicationShutdown

# 3. Relaunch via AUMID
explorer.exe "shell:appsFolder\Cortex.Contained.Launcher_hnfrhv5dkzjbe!CortexLauncher"

# 4. Verify: new version + healthy:true (metrics non-null = agent connected)
Invoke-RestMethod http://localhost:5080/health
```

On start the Launcher copies the bundled `docker-compose.yml` to
`%LOCALAPPDATA%\Cortex\`, detects that the running `cortex-agent` container is on
an outdated image, and runs `docker compose up -d` itself — recreating every
container whose image or config changed.

## Bridge as Windows Service

```powershell
# Install
.\scripts\Install-JamesBridge.ps1

# Uninstall
.\scripts\Uninstall-JamesBridge.ps1
```

## Troubleshooting

**Container won't start**: Check `CORTEX_HUB_TOKEN` is set. The Agent Host requires it.

**Bridge can't connect**: Ensure the Agent Hub is running and the hub token matches. Default URL: `http://localhost:5100/hub/agent`.

**Hot-reload not working**: The dev override uses `DOTNET_USE_POLLING_FILE_WATCHER=1`. Changes under `src/Cortex.Contained.Agent.Host/`, `src/Cortex.Contained.Contracts/`, and `lib/memory-mcp/src/MemoryMcp.Core/` trigger rebuilds.

**Build warnings as errors**: By design. Fix all warnings before committing.
