# Personality Architecture

How the agent's personality (system prompt) is stored, edited, and delivered.

## Overview

Personality is stored **only in the agent container** as `/app/data/personality.md` on the Docker volume. The Bridge reads and writes it via SignalR — no config copy, no divergence. The agent can also self-modify this file via its file tools.

Default personality: `PersonalityDefaults.DefaultPersonality` in `Cortex.Contained.Contracts` — shared by both Agent and Bridge.

## Data flow

```
  Bridge (tenant settings UI)
         │
         │ SignalR: GetPersonality / SetPersonality
         ▼
  Agent Container
  ┌─────────────────────┐
  │  personality.md      │ ◄── also self-modifiable by agent
  │  (read every prompt) │     via file_write tool
  └─────────────────────┘
         │
         ▼
  BuildPrompt() → system message
```

### Prompt composition (`AgentRuntime.BuildPrompt`)

```
personality (from personality.md)
+ "\n\n## Context bootstrap\n" + bootstrap context
+ SystemInstructions (personality.md self-modify guide, memory instructions, sub-agent usage)
+ channel context ("user is talking via {channel}")
+ voice mode instructions (if voice channel)
+ memory nudge
+ recalled memories (semantic search results)
```

## Key components

### Shared

| Component | Location | Role |
|-----------|----------|------|
| `PersonalityDefaults.DefaultPersonality` | `Contracts/PersonalityDefaults.cs` | Shared default: "You are a helpful personal AI assistant. Be concise and direct." |

### Agent Host

| Component | Location | Role |
|-----------|----------|------|
| `DefaultPersonality` | `AgentRuntime.cs` | References `PersonalityDefaults.DefaultPersonality` |
| `LoadPersonality()` | `AgentRuntime.cs` | Reads `personality.md`, returns default if missing |
| `WritePersonality()` | `AgentRuntime.cs` | Writes `personality.md`, creates dirs if needed |
| `GetPersonalityAsync()` | `AgentRuntime.cs` | SignalR hub method — returns `LoadPersonality()` |
| `SetPersonalityAsync()` | `AgentRuntime.cs` | SignalR hub method — calls `WritePersonality()` |

### Bridge

| Component | Location | Role |
|-----------|----------|------|
| `GET /api/tenants/{id}/personality` | `TenantEndpoints.cs` | Returns live personality from tenant's agent |
| `PUT /api/tenants/{id}/personality` | `TenantEndpoints.cs` | Sets personality on agent via SignalR |
| `DELETE /api/tenants/{id}/personality` | `TenantEndpoints.cs` | Resets personality to default on agent |

### SignalR contracts

| Method | Interface | Direction |
|--------|-----------|-----------|
| `GetPersonality()` | `IAgentHub` | Bridge → Agent |
| `SetPersonality(string)` | `IAgentHub` | Bridge → Agent |

### Web UI

Personality is edited exclusively through the per-tenant settings page.

| Component | Location | Role |
|-----------|----------|------|
| Personality card | `app.html` | Textarea + save/reset buttons in tenant settings |
| `loadPersonality()` | `tenant-settings.js` | Loads live value via `GET /api/tenants/{id}/personality` |
| `savePersonality()` | `tenant-settings.js` | Saves via `PUT /api/tenants/{id}/personality` |
| `resetPersonality()` | `tenant-settings.js` | Resets via `DELETE /api/tenants/{id}/personality` |

## System prompt customization

Beyond the personality text, the rest of the system prompt's *shape* — the section order,
the operational-state blocks, and a handful of long prose segments — is also user-editable,
independent of `personality.md`.

### Template model

Two placeholder templates plus three authorable prose segments make up the config
(`SystemPromptConfig` in `Cortex.Contained.Contracts.SystemPrompt`):

| Field | Used for |
|-------|----------|
| `MainTemplate` | Main-agent (and scheduled/task-run) system prompt |
| `SubagentTemplate` | Subagent (`sub_agent_start`) system prompt |
| `VoiceMode` | Authorable prose injected into the main prompt only on voice channels |
| `CodingRelay` | Authorable prose injected into the main prompt describing the `coding_*` tool relay |
| `SubagentInstructions` | Authorable fixed instructions injected into the subagent prompt |

Templates use `{{placeholder}}` tokens. **Computed placeholders** (personality, self-notes,
skills, channel label, active tasks/plans, recalled memories, bootstrap context, etc.) are
filled in at render time from live state; the three segments above are **authorable prose**
— free text the user edits directly, then referenced from a template via their own
placeholder (`{{voice_mode}}`, `{{coding_relay}}`, `{{instructions}}`).

### Placeholder catalog

| Template | Allowed placeholders |
|----------|----------------------|
| Main | `personality`, `self_notes`, `skills`, `channel`, `voice_mode`, `active_tasks`, `active_plans`, `coding_relay` |
| Subagent | `personality`, `skill`, `instructions`, `skills`, `bootstrap_context`, `recalled_memories` |

`SystemPromptPlaceholders` (Contracts) is the single source of truth for both the allowed
sets and the "recommended" subsets (`MainRecommended`, `SubagentRecommended`) used for
validation warnings. Rendering (`SystemPromptRenderer.Render`) is a pure `{{name}}` →
value substitution; any token whose name isn't supplied is left untouched.

### Validation

`SystemPromptValidator.Validate` runs before every save (`SystemPromptStore.Write`):

- **Errors** (block the save): a template references a placeholder not in its allowed set,
  a template exceeds `SystemPromptPlaceholders.TemplateMaxChars` (8000 chars), or a segment
  exceeds `SegmentMaxChars` (12000 chars).
- **Warnings** (save proceeds): a template omits one of its recommended placeholders (e.g.
  a main template missing `{{coding_relay}}`) — surfaced to the caller so the UI/API can
  flag it without blocking.

### Storage

Config is persisted at `<sandbox>/system-prompt.json` inside the agent container —
one file per container, so it's per-tenant like everything else on the data volume.
`SystemPromptStore` caches the parsed config keyed on the file's last-write time, falls
back to `SystemPromptDefaults.Create()` on a missing/corrupt file, and writes atomically
(temp file + rename). `SystemPromptDefaults` reassembles the historical hardcoded prompt
byte-for-byte, so an unedited install behaves identically to before this feature shipped
(locked in by `SystemPromptCharacterizationTests`).

### Runtime API

| Method | Direction | Role |
|--------|-----------|------|
| `GetSystemPromptConfig()` | `IAgentHub` | Bridge → Agent — read active config |
| `SetSystemPromptConfig(SystemPromptConfig)` | `IAgentHub` | Bridge → Agent — validate + persist; returns `SystemPromptValidationResult` |
| `ResetSystemPromptConfig()` | `IAgentHub` | Bridge → Agent — reset to defaults, return them |
| `GetSystemPromptPreview(channelId, isVoice)` | `IAgentHub` | Bridge → Agent — render the exact live prompt (real self-notes/skills/operational state) for a given channel/voice combination, without sending a message |

### Bridge REST endpoints

| Endpoint | Role |
|----------|------|
| `GET /api/tenants/{id}/system-prompt` | Returns the active config |
| `PUT /api/tenants/{id}/system-prompt` | Validates and persists a new config; `400` with `{errors}` if invalid |
| `DELETE /api/tenants/{id}/system-prompt` | Resets to defaults |
| `GET /api/tenants/{id}/system-prompt/preview?channel=&voice=` | Returns the rendered live preview |

### Telemetry

Every accepted `SetSystemPromptConfig` call (and reset) emits an audit line —
`[system-prompt] config updated: changed={fields} {oldFingerprint}->{newFingerprint} warnings={count}`
— naming the changed fields plus before/after fingerprints. `SystemPromptStore.Fingerprint()`
is an 8-hex-char SHA-256 digest over all five config fields, used to correlate log lines
without printing prompt content. At debug level, `PromptAssembler` also logs the fingerprint
of the config used for each rendered prompt.

### Web UI

Edited exclusively through the per-tenant settings page, in a "System Prompt" card next to
Personality: template/segment textareas, a placeholder-chip legend, inline validation
errors/warnings, Save/Reset buttons, and a live preview pane (`tenant-settings.js`:
`loadSystemPrompt()` / `saveSystemPrompt()` / `resetSystemPrompt()` / preview loader).
