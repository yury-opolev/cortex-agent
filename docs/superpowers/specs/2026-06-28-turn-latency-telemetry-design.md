# Design: Per-turn LLM latency telemetry (agent-side)

**Date:** 2026-06-28
**Status:** Approved (implementing)
**Target:** v0.2.291

## Goal

Always-on, low-overhead per-turn latency breakdown in the Agent Host so we can *see*
where time goes in a turn — especially **LLM time-to-first-token (TTFT)**, the known
bottleneck — and confirm whether prompt caching helps. Profile-first: measure before
optimizing. Bridge-side STT/TTS already log their own timing (`voice-in`/`voice-out`);
this covers the agent side and is joined to the Bridge logs by `correlationId` offline.

## Background — what already exists (don't duplicate)

- Per-tool timing: `Stopwatch` around each tool → `LogToolExecuted` (ms) + `NotifyToolCompleted(duration)`.
- Per-round tokens + cache: `[tokens] … promptTokens … cacheWrite … cacheRead` (`AgentRuntime`).
- LLM HTTP timing: HttpClient logs "Received HTTP response headers after Xms" (coarse).
- `AgentMetrics`: process-wide lock-free **counters** (queue depths, messages processed) via `/health` — but **no per-turn timing**.
- Bridge voice: `voice-in` (silenceMs, whisper ms), `voice-out` (per-sentence TTS ms).

The gap: no single per-turn latency breakdown, and no clean **TTFT** measurement.

## What we measure (per turn)

- **queueWaitMs** — message received → processing starts (dequeue).
- **ttftMs** — LLM request sent → first content/tool-call chunk (first round; gates the first word).
- **llmMs** — summed request→stream-end across all rounds.
- **toolMs** — summed tool durations.
- **e2eMs** — receive → response complete.
- Context tags: round count, prompt tokens, `cacheRead`/`cacheWrite`, isVoice, channel, correlationId.

## Components (isolated, testable)

### `TurnLatencyTracker` (new — Agent.Host/Agent)
Per-turn accumulator. Constructed with a `TimeProvider` (monotonic timestamps).
Methods: `MarkDequeued()`, `MarkLlmRequestStart()` / `MarkFirstToken()` / `MarkLlmRequestEnd()`
(per round — accumulates llm total; captures TTFT on the FIRST round only), `AddToolMs(long)`,
and `Build()` → `TurnLatencySnapshot` (record: `QueueWaitMs, TtftMs, LlmMs, ToolMs, E2eMs, Rounds`).
Pure logic; fully unit-testable with a fake/controlled clock.

### `LatencyStats` (new — Agent.Host/Agent)
Thread-safe rolling window (ring buffer, last N = 100) computing `Avg`, `P50`, `P95` for a
sample series. One instance per tracked metric (TTFT, e2e). Lock-free or lock-light; unit-testable.

### `AgentMetrics` (+ `AgentMetricsSnapshot`)
Add `LatencyStats` for TTFT and e2e; expose their avg/p50/p95 + sample count in the snapshot
(additive — older consumers ignore new fields). Surfaced via `/health`.

### `AgentRuntime` wiring
Create a `TurnLatencyTracker` per turn. Mark stages: dequeue at turn start; request-start /
first-chunk (TTFT) / request-end in the streaming loop; tool ms in the tool loop. On turn
completion: emit one structured `[latency]` log line and feed TTFT + e2e into `AgentMetrics`.

## Output (the profiling view)

A greppable per-turn log line, mirroring the `[tokens]` style:
```
[latency] discord-voice-default corr=ab12 voice=True rounds=2 queueWaitMs=40 ttftMs=4700 llmMs=6200 toolMs=120 e2eMs=11500 promptTokens=78343 cacheRead=0 cacheWrite=0
```
Plus rolling aggregates (avg/p50/p95 TTFT + e2e) in `/health` metrics for an at-a-glance
"are we getting faster?" check. Always-on: one log line per turn + lock-free counters → negligible overhead.

## Why this shape

TTFT and `cacheRead`/`cacheWrite` appear on the same line, so once we route through the
direct-Anthropic provider and extend the cache breakpoint to the message prefix, we'll *see*
TTFT drop while `cacheRead` rises on the same turn — evidence the fix worked, not a guess.

## Non-goals (this iteration)

- Cross-process mouth-to-ear correlation (Bridge STT/TTS stitched with agent LLM into one
  number) — deferred; we join by correlationId manually for now.
- Implementing caching itself — that's the *next* step, guided by this telemetry.
- Per-round latency lines — per-turn summary only (rounds count included).

## Testing

- `LatencyStatsTests` — avg/p50/p95 correctness, ring-buffer wrap, empty window.
- `TurnLatencyTrackerTests` — fake clock; mark stages across multiple rounds; assert the
  computed breakdown (TTFT = first round only; llm/tool summed).
- AgentRuntime wiring verified via the unit tests + manual post-deploy (`[latency]` lines
  appear with sane numbers; aggregates populate in `/health`).

## Files

| File | Change |
|------|--------|
| `src/Cortex.Contained.Agent.Host/Agent/TurnLatencyTracker.cs` | **new** |
| `src/Cortex.Contained.Agent.Host/Agent/TurnLatencySnapshot.cs` | **new** (record) |
| `src/Cortex.Contained.Agent.Host/Agent/LatencyStats.cs` | **new** |
| `src/Cortex.Contained.Agent.Host/Agent/AgentMetrics.cs` | add latency stats + snapshot fields |
| `src/Cortex.Contained.Contracts/Hub/AgentMetricsSnapshot.cs` | additive latency fields |
| `src/Cortex.Contained.Agent.Host/Agent/AgentRuntime.cs` | mark stages, emit `[latency]`, feed stats |
| `tests/**` | the suites above |
