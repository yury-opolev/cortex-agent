# Design: Enable/disable toggles for voice subsystems + built-in memory

**Date:** 2026-06-27
**Status:** Design approved — awaiting spec review before planning
**Supersedes:** the "Enable/disable toggles" entry in `BACKLOG.md`

## Context & motivation

Not every Cortex deployment needs speech or the built-in memory stack. TTS, STT, and
voice-id each pull a dedicated Docker sidecar plus model weights (VRAM/disk) that some
users never use. Likewise, some users want to bring their own external memory backend and
have no use for the built-in SQLite + sqlite-vec + Ollama-embeddings stack. Today there is
no clean way to opt out of any of these — the sidecars start regardless.

This design adds **enable/disable toggles** for four optional subsystems — **TTS, STT,
voice-id, and built-in memory** — surfaced as live switches in the web UI, with the
corresponding Docker sidecars started/stopped to match.

## Goals

- A master "speech" switch plus three independent sub-toggles (TTS, STT, voice-id).
- A built-in-memory on/off toggle.
- Toggling a subsystem **starts/stops its sidecar container immediately** where possible.
- **A subsystem disabled at startup never starts its sidecar** (true resource savings).
- All defaults are `true` → existing configs behave identically (zero migration).
- Settings persist to `cortex.yml` so they survive restarts.

## Non-goals (explicitly deferred)

- **Pluggable external memory backend.** "Memory off" means the agent runs with *no*
  long-term memory; wiring in an alternative backend behind the same interface is a
  separate future backlog item.
- Per-tenant overrides of these toggles (global only for now).
- Deleting existing memories when memory is disabled — they are left on disk untouched so
  re-enabling restores them.

## Decisions (resolved during brainstorming)

1. **Scope:** on/off toggles only. Pluggable external memory deferred.
2. **Config surface:** live toggles in the web UI (not static-yaml-only).
3. **Off semantics:** per-subsystem. In-process gating *and* sidecar start/stop.
4. **Voice granularity:** master switch + three independent sub-toggles.
5. **Voice sub-toggles are fully independent** — any combination is valid (e.g. STT-on +
   TTS-off = talk via Discord, receive a text reply). No "incoherent combo" validation.
6. **Voice-id depth:** fully live, uniform with the other three subsystems.
7. **Container handling:** start/stop the corresponding container immediately on toggle.
   **Fall back to "restart required" only when a live operation is genuinely not possible**
   (e.g. the `docker compose` call fails) — surfaced in the UI, never a silent failure.

## Effective-state rule

```
speech.enabled (master)
  ├─ stt.enabled
  ├─ tts.enabled
  └─ voiceId.enabled

effective(sub) = speech.enabled && sub.enabled
```

Master off ⇒ all three voice subsystems are off regardless of sub-flags. Memory has its own
independent `memory.enabled` flag (not under the speech master).

## Config schema (`Cortex.Contained.Contracts/Config/BridgeConfig.cs`)

Add `Enabled` flags following the existing `WebUiConfig.Enabled` / `ChannelConfig.Enabled`
precedent. All default `true`.

```csharp
public sealed class SpeechConfig
{
    public bool Enabled { get; set; } = true;          // NEW — master voice switch
    public SttConfig Stt { get; set; } = new();
    public TtsConfig Tts { get; set; } = new();
    public VoiceIdConfig VoiceId { get; set; } = new(); // NEW sub-section
}

public sealed class SttConfig
{
    public bool Enabled { get; set; } = true;          // NEW
    // ...existing fields...
}

public sealed class TtsConfig
{
    public bool Enabled { get; set; } = true;          // NEW
    // ...existing fields...
}

public sealed class VoiceIdConfig                       // NEW
{
    public bool Enabled { get; set; } = true;
    // Existing SpeakerId:* env settings (Backend, RemoteEndpoint, ModelId,
    // EmbeddingDim) remain as-is; this only adds the enable flag.
}

public sealed class MemorySettingsConfig
{
    public bool Enabled { get; set; } = true;          // NEW — built-in memory master
    // ...existing fields...
}
```

## Behavior per subsystem

All four subsystems follow **one uniform pattern**: a compose-profile-gated sidecar managed
by a Bridge lifecycle (start-if-down on enable, stop-if-up on disable) plus an in-process
*usage gate* so the running code respects the flag even before/without a container change.

| Subsystem | Sidecar (container)      | Sidecar change                         | Usage gate |
|-----------|--------------------------|----------------------------------------|------------|
| TTS       | `uni-voices`             | already `profiles:[tts]`; add stop     | Bridge TTS path |
| STT       | `stt`                    | already `profiles:[voice]`; add stop   | Bridge STT path |
| Voice-id  | `voice-id`               | **add** `profiles:[voiceid]` + stop    | Agent enrollment/verify gate |
| Memory    | `embeddings` (Ollama)    | **add** `profiles:[memory]` + stop     | Agent memory gate (tools/extraction/compaction) |

### Sidecar lifecycle (Bridge)

- **STT** — `SttSidecarLifecycle` exists (start-if-down). Add a **stop** path
  (`docker compose stop stt`) used when STT is disabled.
- **TTS** — `DanishTtsLifecycle` exists (start-if-down). Add a stop path
  (`docker compose stop uni-voices`).
- **Voice-id** — **new** `VoiceIdSidecarLifecycle` mirroring the above; `voice-id` gains
  `profiles:[voiceid]` so the default `up` no longer starts it.
- **Embeddings** — **new** `EmbeddingsSidecarLifecycle`; `embeddings` gains
  `profiles:[memory]` so the default `up` no longer starts it.

Extend the compose-runner seams (`ISttComposeRunner`, `IComposeCommandRunner`, and two new
analogues) with `Stop*Async` methods. Implemented by the single `DockerComposeCommandRunner`.

### `docker-compose.yml` changes

- `voice-id`: add `profiles: [voiceid]`.
- `embeddings`: add `profiles: [memory]`.
- **Relax `cortex-agent.depends_on`**: remove the hard `service_started` conditions on
  `embeddings` and `voice-id` (they are now optional, Bridge-managed sidecars). The agent
  uses both lazily — Ollama embeddings on `memory_search`/`ingest`, voice-id only during
  enrollment/verification — so a missing sidecar at boot is tolerated.
  **Verification item for the plan:** confirm the agent boots cleanly with memory off and
  voice-id off (no eager connection at startup).

### Agent-side usage gates

- **Memory** — add `MemoryEnabled` to `MemorySettingsStore`. When false the
  `AgentRuntime` / tool registry:
  1. hides the 5 memory tools from the LLM tool list,
  2. skips the extraction-buffer flush + `MemoryExtractionService`,
  3. skips the `MemoryCompactionService` sweep,
  4. omits memory guidance from the system prompt.
  Toggling the flag fires the existing `IOptionsMonitor` change-token, so the gate flips
  live with no restart.
- **Voice-id** — register the enrollment tools + orchestrator on **model presence**
  (unchanged from today's `embedderRegistered` gate), but gate their *usage* on a pushed
  `VoiceIdEnabled` flag so they can be ungated live without re-running DI.

## Runtime flow (UI save → apply)

1. User flips a toggle in the web UI Settings page and saves.
2. Bridge writes the flag(s) to `cortex.yml` (durable, survives restart).
3. Bridge applies immediately:
   - **STT/TTS:** run the lifecycle reconcile — enabled ⇒ start-if-down, disabled ⇒
     stop-if-up.
   - **Voice-id:** push `VoiceIdEnabled` to the agent (gates usage) **and** run the
     `VoiceIdSidecarLifecycle` (start/stop the `voice-id` container).
   - **Memory:** push `MemoryEnabled` to the agent via the existing `CredentialsPusher`
     memory-settings RPC → `MemorySettingsStore.Update(...)` (gates usage) **and** run the
     `EmbeddingsSidecarLifecycle` (start/stop the `embeddings` container).
4. If a `docker compose` start/stop op fails or is not possible, the UI shows the affected
   subsystem as **"restart required to apply"** rather than reporting success.

## Cross-process propagation

- **Speech/STT/TTS** live entirely on the Bridge (Windows host) — engines + lifecycles are
  Bridge-side, so no agent round-trip is needed.
- **Memory** lives on the Agent Host. The flag rides the **existing** Bridge→Agent
  memory-settings push (`CredentialsPusher`, covered by
  `CredentialsPusherMemorySettingsPushTests`) into `MemorySettingsStore`.
- **Voice-id** spans both: the Bridge manages the sidecar; the agent receives the
  `VoiceIdEnabled` flag via the same settings-push channel to gate enrollment/verify usage.

## Web UI

Extend the existing Settings page (`wwwroot/js/pages/global-settings.js`), where memory
settings already live, with a **"Features"** group:

- **Voice** master toggle, with three indented sub-toggles: TTS, STT, Voice-id.
- **Built-in memory** toggle.

Each toggle applies instantly; if the live container op fails, the row shows an inline
"restart required to apply" badge. Disabled sub-toggles are visually subordinate to the
master (master off greys out the three).

## Edge cases / UX semantics

- **Master speech off:** Voice channel + Discord voice advertise as unavailable; no
  STT/TTS/voice-id sidecars run.
- **STT on, TTS off:** voice input transcribes; replies are not spoken — the agent falls
  back to text in that channel. (First-class combo.)
- **TTS on, STT off:** allowed (e.g. text/push input, spoken output). No blocking.
- **Memory off:** agent runs with no long-term memory; existing memories remain on disk and
  are restored on re-enable. The `embeddings` container is stopped (or never started).
- **Disabled-at-startup:** profile-gated sidecars are simply never started by the Bridge
  reconcile, so they consume no resources.

## Error handling

- Lifecycle reconciles already swallow+log failures (fire-and-forget safe). Extend this:
  a failed start/stop surfaces a "restart required" state to the UI, never a silent success.
- Agent-side gates are pure in-memory checks — no failure mode beyond the push itself, which
  reuses the proven `CredentialsPusher` path.

## Testing strategy

- **Unit**
  - Effective-state logic (`master && sub`) for the three voice subsystems.
  - `MemorySettingsStore.MemoryEnabled` gate + change-token fires.
  - New `Stop*Async` compose-runner methods (mock the process seam).
  - New `VoiceIdSidecarLifecycle` / `EmbeddingsSidecarLifecycle` reconcile honoring the flag
    (extend the existing `SttSidecarLifecycleTests` / `DanishTtsLifecycleTests` patterns).
- **Behavioral**
  - Memory off ⇒ memory tools hidden from the LLM list + extraction skipped.
  - Voice-id off ⇒ enrollment/verify usage gated while tools stay registered.
  - Boot with memory off + voice-id off ⇒ agent starts cleanly (depends_on relaxation).
- **Config**
  - Defaults all `true`; round-trip of the new flags through `cortex.yml`.

## Implementation surface (for the plan)

- `Contracts/Config/BridgeConfig.cs` — new `Enabled` flags + `VoiceIdConfig`.
- `docker-compose.yml` — profiles on `voice-id` + `embeddings`; relax `cortex-agent.depends_on`.
- Bridge `Speech/` — `Stop*Async` on runner seams; new voice-id + embeddings lifecycles;
  reconcile wiring on settings-save.
- Bridge `Hosting/CredentialsPusher.cs` — carry `MemoryEnabled` + `VoiceIdEnabled`.
- Agent `Memory/MemorySettingsStore.cs` + `AgentRuntime`/`ToolRegistry` — memory usage gate.
- Agent `SpeakerId` registration + `EnrollmentOrchestrator` — voice-id usage gate.
- Bridge `wwwroot/js/pages/global-settings.js` + endpoints — the toggle UI.

## Open items to resolve in the plan

1. Confirm exact agent-startup orchestration (who runs the initial `docker compose up`, and
   that relaxing `depends_on` doesn't strand the agent waiting on a now-profiled sidecar).
2. Confirm the embeddings/voice-id sidecars can be started/stopped by the same
   `DockerComposeCommandRunner` shell-out the Bridge already uses for stt/tts.
3. Decide the precise UI copy + state machine for the "restart required" fallback badge.
