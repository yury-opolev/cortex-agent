# Backlog

Parking lot for ideas captured mid-brainstorm. Each item records enough state to resume
without re-deriving context. Items here are **not yet approved designs** — they still owe
the brainstorming → spec → plan flow before any implementation.

---

## Enable/disable toggles: voice models + built-in memory

**Status:** ✅ Promoted — design approved 2026-06-27.
See [`docs/specs/2026-06-27-enable-disable-toggles-voice-memory-design.md`](docs/specs/2026-06-27-enable-disable-toggles-voice-memory-design.md).
The notes below are retained for history; the spec is now the source of truth.

**Captured:** 2026-06-27

### The ask (verbatim)
> Is it possible to add ability to enable/disable voice models: TTS, STT, voice id? Some
> users will not need it. Also — can we add ability to enable/disable built-in memory?
> Some users will prefer to have their own external memory.

### Why
- Not every deployment needs speech. TTS/STT/voice-id each pull a sidecar container and
  model weights that some users will never use — they want to opt out cleanly.
- Some users want to bring their own external memory backend instead of the built-in
  SQLite + sqlite-vec + Ollama-embeddings stack, so they need a way to turn the built-in
  one off.

### Architecture findings (from exploration — verify before relying on line numbers)

**Bridge `src/Cortex.Contained.Bridge/Program.cs`**
- STT registration (~lines 201–371): `RemoteSpeechToText` + `WhisperStreamingSpeechToText`
  + `ITurnDetector`.
- TTS: `IReadOnlyList<ITtsProvider>` (kokoro / roest-da / silero-v5-russian via
  `RemoteTtsProvider`) + `CompositeTtsEngine` (`ITextToSpeech`) + `ILanguageDetector`.

**Agent Host `src/Cortex.Contained.Agent.Host/Program.cs`**
- Line ~137: `AddMemoryMcpCore(builder.Configuration)`.
- Lines ~140–148: `MemorySettingsStore` + post-configures (runtime-mutable memory settings).
- Lines ~241–327: SpeakerId DI (Backend Local/Remote, `voice-id:5200` sidecar). **Line ~297
  already has a "Voice-id disabled" path** when no ONNX model is present — a precedent for a
  clean disabled branch.
- Line ~390: `MemoryConsolidationService`. Line ~400: memory tools registration (5 tools).

**Contracts `src/Cortex.Contained.Contracts/Config/BridgeConfig.cs`**
- Existing enable precedents: `WebUiConfig.Enabled = true`, `ChannelConfig.Enabled`.
- `MemorySettingsConfig`: DuplicateThreshold, CompactionSimilarityThreshold,
  CompactionEnabled, IdleCompactionEnabled, IdleResetMinutes,
  CompactionPreserveRecentTurns, EmbeddingEndpoint.
- `SpeechConfig` → `SttConfig` (Engine="whisper", WhisperModelPath, Language) +
  `TtsConfig` (Engine="kokoro", voices, OutputGain, Languages).
- **No top-level enable flags for Speech / VoiceID / Memory yet** — these are what we'd add.

### Open clarifying questions (resume here)
1. **Config surface** (the question that was pending when we paused): static in `cortex.yml`
   at startup, runtime toggle in the web UI, or hybrid (static config that the UI reflects)?
2. **Voice granularity:** one master voice on/off, or three independent toggles for
   TTS / STT / voice-id?
3. **"External memory" meaning:** does "enable/disable built-in memory" mean *just* turn the
   built-in stack off (agent runs with no long-term memory), or also wire in an external
   memory backend behind the same interface? Scope decision — likely two separate items.

### Candidate approaches (preliminary — NOT yet presented or approved)
- **A. Config-only toggles in `cortex.yml`:** add `enabled` flags under `speech.stt`,
  `speech.tts`, voice-id, and a `memory.enabled`. DI skips registration when off. Mirrors the
  existing `ChannelConfig.Enabled` / voice-id-disabled precedent. Lowest blast radius.
- **B. Config + web-UI runtime toggle:** same flags surfaced in Settings, flippable live via
  the `IOptionsMonitor` / `MemorySettingsStore` pattern already used for memory settings.
  More work; sidecars can't be started/stopped live so "off→on" may still need a restart.
- **C. Hybrid:** static `cortex.yml` is source of truth; UI shows current state read-only
  (or schedules a restart to apply). Compromise between A and B.

Recommendation leaning A or C — confirm with the config-surface answer first.
