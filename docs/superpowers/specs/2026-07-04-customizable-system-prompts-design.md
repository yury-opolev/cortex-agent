# Customizable System Prompts — Design

**Date:** 2026-07-04
**Status:** Approved (brainstorming complete)
**Branch:** `feat/customizable-system-prompts`

## Problem

Today only the **personality** block of the agent's system prompt is user-editable
(`personality.md`, via the Bridge web UI / REST / the agent's self-edit tool). Everything
else is compiled-in C#:

- **Main agent** (`PromptAssembler.BuildPromptAsync`) concatenates: personality → self-notes
  → skills → channel label → voice-mode block → active tasks → active plans → coding-relay
  block. Scheduled/task-triggered runs use this **same** path (ephemeral session; task text
  arrives as the user message).
- **Subagents** (`SubAgentStartTool.BuildSubagentSystemPrompt`) use a **separate** hardcoded
  template: optional skill content → fixed instructions → skills list → bootstrap context →
  recalled memories. Persona is **not** included.

We want the template layout **and** the authorable prose blocks to be editable and
manageable in the web UI, without letting a bad edit silently break tool behavior.

Out of scope for this spec (deferred to a follow-up): **utility prompts** (memory
extraction/consolidation, compaction summarizer, topic slicer, image describer, subagent
context-query/filter, voice-gender classifier). Also out of scope: the **2-level subagent**
rework (its own later spec — this spec is intentionally sequenced first so the subagent
template already exists when that work lands).

## Approved decisions

1. **Placeholder-template model** with **protected placeholders** (not a free-text blob, not
   toggles-only). Runtime-computed segments stay as validated `{{placeholders}}`; authorable
   prose is editable.
2. **Utility prompts deferred** to a follow-up spec.
3. **Per-container file storage** (the current container-per-tenant model *is* per-tenant;
   the runtime has no `tenantId` threading and we are not adding it here).
4. **Subagent persona out by default** — optional `{{personality}}` placeholder, empty
   unless the user opts in.
5. Defaults are extracted **verbatim** from today's hardcoded strings → a fresh install
   renders **byte-identical** prompts. The feature is inert until edited.

## The editable model

### Main-agent template (also used by scheduled/task runs)

| Placeholder | Kind | Source |
|---|---|---|
| `{{personality}}` | authorable | existing `personality.md` (existing editor/endpoint unchanged) |
| `{{self_notes}}` | computed | `SelfNotesStore` |
| `{{skills}}` | computed | `SkillRegistry.FormatForSystemPrompt()` |
| `{{channel}}` | computed, conditional | channel label line |
| `{{voice_mode}}` | authorable, conditional | new segment text, injected only when `isVoice` |
| `{{active_tasks}}` | computed, conditional | `SubagentSessionStore.GetActive()` |
| `{{active_plans}}` | computed, conditional | `TodoStoreResolver` summaries |
| `{{coding_relay}}` | authorable | new segment text |

### Subagent template

| Placeholder | Kind | Source |
|---|---|---|
| `{{personality}}` | authorable, **empty by default** | optional persona-in-subagent |
| `{{skill}}` | computed, conditional | requested skill content |
| `{{instructions}}` | authorable | the "Complete the task… be thorough…" block |
| `{{skills}}` | computed | skills list |
| `{{bootstrap_context}}` | computed, conditional | `context-bootstrap.md` |
| `{{recalled_memories}}` | computed, conditional | memory retrieval |

**Conditional placeholders** render to empty string when not applicable, with **blank-line
collapse**, preserving today's "only-when-present" spacing.

**Authorable segment texts** stored separately from templates: `voice_mode`, `coding_relay`,
`subagent_instructions`. Persona keeps its own existing storage (`personality.md`) and its
own existing endpoint — this feature references it via `{{personality}}` but does not move it.

## Components (loosely coupled, independently testable)

- **`SystemPromptDefaults`** — `Cortex.Contained.Contracts`, static. Verbatim default
  templates + segment texts, shared by Agent and Bridge (mirrors `PersonalityDefaults`). The
  behavior-preservation anchor.
- **`SystemPromptConfig`** — `Cortex.Contained.Contracts` DTO (record/class):
  `{ MainTemplate, SubagentTemplate, VoiceMode, CodingRelay, SubagentInstructions }`.
  camelCase JSON, named props (per the ValueTuple-serialization lesson — add a shape test).
- **`SystemPromptStore`** — `Cortex.Contained.Agent.Host`. Reads/writes one JSON file in the
  container data volume (`/app/data/system-prompt.json`), missing → defaults, cache with
  file-write-time invalidation, **atomic write** (temp + rename). Mirrors the personality
  read/write pattern. Fixed sandbox-relative path (no traversal from user input).
- **`SystemPromptRenderer`** — pure. `Render(template, IReadOnlyDictionary<string,string>
  values)` → string. No I/O. Handles substitution, conditional-empty, blank-line collapse.
- **`SystemPromptValidator`** — pure. **Hard errors** (block save): unknown placeholders
  (typo protection) and per-field char-cap violations. **Non-blocking warnings**: missing
  recommended placeholders — main: `{{personality}}`, `{{self_notes}}`, `{{skills}}`,
  `{{coding_relay}}`; subagent: `{{instructions}}`, `{{skills}}`. (Removing a recommended
  block is allowed but flagged, since it may weaken the agent or break tool guidance.)
  Returns a structured result (`{ IsValid, Errors[], Warnings[] }`).
- **`PromptAssembler` / `SubAgentStartTool`** — refactored to resolve each placeholder's
  value into a dictionary (compute dynamic ones; pull authorable ones from the store), then
  call the renderer. The large hardcoded concatenation is replaced by a declarative
  placeholder-resolver map, not a giant switch.

Char caps: main/subagent template ≈ 8 KB each; segment texts ≈ 4 KB each.

## Bridge API

**Hub contract** (`IAgentHub`, agent-side, mirrors the personality methods):

- `GetSystemPromptConfigAsync()` → `SystemPromptConfig`
- `SetSystemPromptConfigAsync(SystemPromptConfig)` → validation result (errors + warnings)
- `ResetSystemPromptConfigAsync()` → defaults
- `GetSystemPromptPreviewAsync(channelId, isVoice)` → fully-assembled string using **live
  computed values** + sample values for conditional blocks

**REST** (`TenantEndpoints`, mirrors personality endpoints, same admin auth):

- `GET/PUT/DELETE /api/tenants/{tenantId}/system-prompt`
- `GET /api/tenants/{tenantId}/system-prompt/preview?channel=&voice=`

PUT returns `{ ok, warnings[] }`: save-with-warnings allowed; hard errors (unknown
placeholder, cap exceeded) block with 400 + error list.

## Web UI (`tenant-settings.js`)

New "System Prompt" card under the existing personality editor:

- Main-template + subagent-template textareas, each with a **placeholder chip list**
  (click-to-insert) and **Reset to default**.
- Three segment-text editors: voice-mode, coding-relay, subagent-instructions — each with reset.
- **Live preview pane** (debounced) via the preview endpoint — makes the protected-placeholder
  model legible; the user sees exactly what the model receives.
- Inline validation: hard errors block save (red); warnings are dismissible (amber).

## Telemetry (audit + investigation)

Source-generated `[LoggerMessage]`, structured:

- **Prompt-build fingerprint:** on every build, log a stable hash of the active template +
  segment texts alongside the existing `[context]` line — any bad response traces to the
  exact prompt version in effect.
- **Save audit line:** which fields changed, old→new fingerprints, char counts, warnings.
  The "what changed and when" trail.
- **Validation failures:** rejected placeholders / exceeded caps.
- **Render debug:** resolved placeholder names + whether each conditional was present/empty
  (debugging "why didn't block X appear").
- Metrics via existing `AgentMetrics`: render count, validation-rejection count.

## Security

- Hard **char caps** per field prevent context-window exhaustion / cost blow-up.
- **Unknown-placeholder rejection** keeps the surface closed and prevents literal
  `{{…}}` tokens leaking into prompts.
- Store path fixed & sandbox-relative; **atomic writes** so a crash mid-write can't corrupt
  the active prompt.
- Reuses **existing Bridge admin auth**; no new unauthenticated surface. Preview endpoint is
  admin-gated (it can surface live self-notes/skills). No secrets exist in the container, so
  templates carry no exfiltration risk.

## Testing (red/green TDD)

- **Golden / behavior-preservation** (safety net): assemble with defaults, assert
  byte-identical to today's output across representative states — main, voice, with active
  tasks + plans, subagent (with/without skill). Locks "no behavior change until edited."
- `SystemPromptRenderer`: substitution, conditional-empty + blank-line collapse, unknown-token
  handling.
- `SystemPromptValidator`: unknown placeholder rejected, caps enforced, warnings surfaced.
- `SystemPromptStore`: read/write/reset, cache invalidation on file change, missing-file →
  defaults, atomic write.
- **Serialization-shape** regression: `SystemPromptConfig` round-trips camelCase named props
  (no `Item1`).
- Bridge endpoint round-trip: PUT-then-GET equality; PUT with bad placeholder → 400 + errors.
- Preview endpoint returns a rendered string containing live values.

Each plan task runs red → green → commit, with a code-review pass (spec-compliance +
quality) between tasks.

## Behavior-preservation contract

The default `SystemPromptDefaults` templates **must** reassemble the current hardcoded strings
in the current order. Specifically the main template encodes:

```
{{personality}}

## Self-notes
{{self_notes}}
{{skills}}{{channel}}{{voice_mode}}{{active_tasks}}{{active_plans}}{{coding_relay}}
```

and the subagent template encodes today's `BuildSubagentSystemPrompt` order. Golden tests are
the acceptance gate for this contract. Prompt caching is unaffected (templates change only on
edit, not per turn).
