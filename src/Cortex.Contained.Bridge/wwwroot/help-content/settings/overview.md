# Settings Overview

Cortex settings live in two places in this web UI:

- **Global Settings** (sidebar → SYSTEM → Global Settings) — system-wide
  configuration: LLM providers and routing, channels, speech engines, and memory
  behavior. Organized into four tabs: **General**, **Channels**, **Speech**,
  **Memory**.
- **Tenant Settings** (sidebar → a tenant → Settings) — per-tenant configuration:
  the tenant's enabled state, Discord linking, voice channel, API key, personality,
  self-notes, and voice identification.

## Restart-required vs. immediate

Some changes take effect immediately; others need the Bridge to restart.

| Change | Takes effect |
|--------|--------------|
| Memory settings (thresholds, compaction, idle) | Immediately |
| Personality / self-notes | On the next message |
| LLM provider keys, default/memory model | After a Bridge restart |
| Enabling/disabling a channel, channel settings | After a Bridge restart |
| Web UI port / bind address | After a Bridge restart |

When a change needs a restart, the UI shows a **"Restart Cortex"** prompt. Confirming
it restarts the Bridge for you: the Bridge exits with a special code and the Launcher
that supervises it respawns the process automatically. The page reconnects on its own
in about 10–15 seconds — you don't need to run any script by hand.

## Where settings are stored

Everything you change here is written to `cortex.yml` in `%LOCALAPPDATA%\Cortex`.
Secrets (API keys, bot tokens) are **not** stored in that file in plaintext — they
go into a DPAPI-encrypted store, and the YAML only keeps a reference. Cortex keeps
timestamped `.bak` copies of `cortex.yml` when it rewrites it, so prior versions are
recoverable.

See the per-area pages: [LLM Providers & Routing](help:llm-providers),
[Speech](help:speech-settings), [Memory Settings](help:memory-settings), and the
**Channels** section.
