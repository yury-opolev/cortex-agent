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
