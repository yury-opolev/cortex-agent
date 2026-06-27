# LLM Providers & Routing

Found under **Global Settings → General**. This is where you tell Cortex which
language models to use and in what order.

## Providers

Each provider card shows:

- **API type** — `openai-completions`, `anthropic-messages`, `github-copilot-api`,
  `mock`, etc.
- **Endpoint** — the base URL (only shown for custom/local endpoints like Ollama).
- **API Key status** — whether a key is configured, and where it came from (DPAPI
  store, environment variable, or config). Keys are stored encrypted via DPAPI.
- **Models** — the models this provider exposes. For providers with a live model
  API (and a configured key), a **Refresh** button re-fetches the available list.

### Default Model

The model used for normal conversation. If a provider exposes two or more models,
you choose the default from a dropdown.

### Memory Model

An optional, usually **cheaper** model used for background memory work — fact
extraction and conversation compaction — instead of your default model. Leave it on
"Same as default" to use the default model for everything. Using a smaller model
here can noticeably reduce cost, since memory work runs in the background regularly.

## Provider Fallback Order

A drag-to-reorder list. When the primary provider fails (rate limit, outage, auth
error), Cortex automatically tries the next provider in the list. Put your preferred
provider first; add others below as backups.

## Sub-agent: Max tool rounds

A **safety-net** ceiling on how many tool-call rounds a sub-agent may run. The real
stopping conditions are: the model finishing, the context window filling up, or
doom-loop detection (three identical calls in a row). `0` means use the default
(200). You rarely need to change this.

## System info

The General tab also shows read-only status: the **Web UI** bind address/port and
whether the **Agent Hub** (the connection to the containerized Agent) is currently
connected.

> Provider, key, and model changes require a **Bridge restart** to take effect. The
> UI will offer to restart for you.
