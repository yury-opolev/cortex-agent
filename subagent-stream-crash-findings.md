# Sub-agent crash: "Stream transport fault: LLM stream produced no data for 120s."

**Status: root cause confirmed; all four recommended fixes have since been implemented,
reviewed, merged and deployed.** Sections 0-6 and 8 are the investigation as it stood *before*
any code changed, and all line numbers in them refer to the pre-fix tree at commit `2354b2f`
so they stay verifiable against the code that actually failed. Section 7 gives the ranked
recommendations; section 9 records what was built and how it differs from the proposal;
section 10 lists the residual risk that remains.

Investigation basis: `docker logs cortex-agent` (the agent runs in the `cortex-agent`
container; the host-side `%LOCALAPPDATA%\Cortex\logs\bridge-*.log` files do **not** contain
this error) plus the source at `C:\Users\yurio\Documents\github\cortex-agent`.

---

## 0. TL;DR

1. The string is emitted by an **inactivity watchdog in Cortex**, not by the provider.
   The stream did not die — the client killed it.
2. The watchdog that fired is the **between-chunks** budget (120s), which measures
   **idle time since the last chunk**, not time since request start. It does **not** run
   during tool execution.
3. It fired **122s after the last tool call finished** — twice, on the same task, ~10 min apart.
   The provider (`claude-opus-5`, Anthropic Messages) emitted an opening chunk quickly and then
   went silent for >120s while working through a very large accumulated context.
4. The damaging design bug: because the stall happened **after** the first chunk reached the
   caller, it is **structurally excluded** from both same-provider retry and provider failover
   (both are pre-content-only). One 120s stall = instant terminal failure of a 25-minute task.
5. The task was correctly recorded as **Failed** in the store — but
   `BuildCompletionTriggerText` is **state-blind** and hands the parent a
   `[Background task completed]` envelope with the error text pasted in as the "Result".
   That is why it looked like a success path.
6. The work was **not** lost: full message history is checkpointed every round, and
   `sub_agent_send` can resume a Failed task. Nothing in the system told the parent to do so.

---

## 1. Exact code locations

Line numbers as of the failing tree (`2354b2f`).

| What | File | Line |
|---|---|---|
| `"Stream transport fault: "` prefix | `src/Cortex.Contained.Agent.Host/Llm/Providers/LlmStreamFault.cs` | 25 |
| `"LLM stream produced no data for {n}s."` | `src/Cortex.Contained.Agent.Host/Llm/Providers/LlmStreamIdleGuard.cs` | 116 |
| `"...produced no first token within {n}s."` | same | 117 |
| Budget selection (`started ? BetweenChunks : FirstChunk`) | same | 77 |
| Defaults (`FirstChunk` 5 min, `BetweenChunks` 120s) | same | 19, 22 |
| Config binding | `src/Cortex.Contained.Agent.Host/Program.cs` | 116 |
| Config defaults `LlmFirstTokenTimeoutSeconds=300`, `LlmStreamIdleTimeoutSeconds=120` | `src/Cortex.Contained.Contracts/Config/AgentConfig.cs` | 29, 37 |
| Guard wired around every provider stream | `src/Cortex.Contained.Agent.Host/Llm/DirectLlmClient.cs` | 625-627 |
| Transient classification (`TimeoutException` ⇒ transient) | `LlmStreamFault.cs` | 41-44 |
| **Pre-content-only** same-provider retry | `DirectLlmClient.cs` | ~375-392 |
| **Pre-content-only** failover (`if (!emittedAny && …)`) | `DirectLlmClient.cs` | ~565-575 |
| Error chunk ⇒ `AgentLoopOutcome.Error`, loop returns immediately | `Agent/AgentLoop.cs` | 224-246 |
| `Error` ⇒ `SubagentTaskState.Failed` | `Agent/SubagentRunner.cs` | 168-175 |
| Error text becomes the task "result" | `Agent/SubagentRunner.cs` | 220-222 |
| **State-blind** parent notification | `Agent/SubagentExecutionCoordinator.cs` | 529-540 |
| Per-round history checkpoint | `Agent/SubagentCallbacks.cs` | 152, 208 |
| Resume of a terminal task | `Tools/BuiltIn/SubAgentSendTool.cs` | 89, 123-132 |

**The error path, end to end:**

```
AnthropicApiClient.ParseAnthropicSseAsync   (provider goes quiet; thinking emits no chunk)
  -> LlmStreamIdleGuard.Apply               (WaitAsync(120s) expires -> throws TimeoutException)
  -> LlmStreamFault.Guard                   (catches it; IsTransient(TimeoutException)=true
                                             -> terminal chunk "Stream transport fault: ...")
  -> DirectLlmClient.StreamWithRetryAsync   (retry SKIPPED: not a pre-content error)
  -> DirectLlmClient.StreamCompleteAsync    (failover SKIPPED: emittedAny already true)
  -> AgentLoop.ExecuteAsync                 (error chunk -> AgentLoopOutcome.Error, returns)
  -> SubagentRunner.ToTerminalState         (Error -> SubagentTaskState.Failed)
  -> SubagentSessionStore.TrySetTerminalResult   ("state changed to failed" - CORRECT)
  -> SubagentExecutionCoordinator.BuildCompletionTriggerText
                                            (state-blind -> "[Background task completed]")
  -> parent agent reads a success envelope containing an error string
```

---

## 2. What the 120s watchdog actually measures

`LlmStreamIdleGuard.Apply` wraps the provider's `IAsyncEnumerable<LlmStreamChunk>` and puts a
`WaitAsync(budget)` around **each individual `MoveNextAsync`**:

```csharp
var budget = started ? timeouts.BetweenChunks : timeouts.FirstChunk;   // line 77
...
pending = enumerator.MoveNextAsync().AsTask();
hasChunk = await pending.WaitAsync(budget, cancellationToken)...
```

So the clock is **per-gap, restarted on every chunk** — idle-since-last-chunk, *not*
since-request-start. Two budgets:

* `started == false` → **FirstChunk = 300s** ("produced no first token within 300s")
* `started == true` → **BetweenChunks = 120s** ("produced no data for 120s") ← **the one that fired**

**Does it run during tool calls? No.** The guard only exists for the lifetime of one
`StreamCompleteAsync` enumeration. Tool execution happens in `AgentLoop` *after* that
enumeration completes and before the next request is issued (`AgentLoop.cs` 215-260 then the
tool-round block). A 285-second `run_command` observed at 20:32 in the logs proves this —
it did not trip the guard.

It also exists because `HttpClient.Timeout` stops applying once response headers are read
(every streaming call uses `HttpCompletionOption.ResponseHeadersRead`), so without the guard a
silent provider would hang the turn forever. The guard was the *only* bound on a stream.

Configurable: yes, `LlmFirstTokenTimeoutSeconds` / `LlmStreamIdleTimeoutSeconds` in
`AgentConfig` (`[Range(0,3600)]`, 0 disables). Neither is overridden in
`%LOCALAPPDATA%\Cortex\cortex.yml`, so both defaults were in force.

**Hypothesis 4 in the brief ("watchdog measures from request start") is disproved.**
The bug is real but different: the budget is *re-armed too tight after content starts*.

---

## 3. Client-side or server-side?

**Client-side abort of a server that was still working.** Evidence:

* The message itself only exists in Cortex — no provider error, no HTTP status, no socket
  reset is involved in producing it. `TimeoutException` is manufactured at
  `LlmStreamIdleGuard.cs:116`, then re-labelled `Stream transport fault:` by
  `LlmStreamFault.IsTransient` (line 41: `TimeoutException` ⇒ transient).
* The guard then **cancels the provider read itself** (`linked.CancelAsync()`, line 105).
  The connection death is *caused by* the watchdog.
* Zero correlating provider signals in the container log around either failure: no
  `HTTP 4xx/5xx`, no `All providers failed`, no failover log line, no context-window error.

### Timeline (container log, UTC)

Run 1 — `sa-ffd6f6d65b9d461e91fdcca90e73693b`
```
21:03:05 WRN [subagent] error: Stream transport fault: LLM stream produced no data for 120s.
21:03:05 INF [subagent-store] Task sa-ffd6f6d... state changed to failed
```

Run 2 — `sa-a61a32ebde8c4ca4a9e5caaddd3e8617` (restart of the same research task)
```
21:11:24 INF executing run_command: ...
21:11:25 INF run_command completed: success=True        <-- last tool round ends
        (~2s to open the stream, first chunk arrives, then silence)
21:13:27 WRN error: Stream transport fault: LLM stream produced no data for 120s.
21:13:27 INF [subagent-store] Task sa-a61a32e... state changed to failed
21:13:27 INF [subagent-coordinator] Completion notification for sa-a61a32e... enqueued
21:14:00 INF [subagent-completion] Notification ... confirmed delivered with the parent turn's response
```

**21:11:25 → 21:13:27 = 122 seconds.** That is 120s of budget plus ~2s of request setup —
an exact, unambiguous match for a single armed `BetweenChunks` window.

Model in force: `agent.defaultModel: claude-opus-5` (`cortex.yml:15`), i.e. the Anthropic
Messages path (`AnthropicApiClient`).

---

## 4. Why it correlates with context size (confidence: high)

Note *which* branch fired: the **`started == true`** branch. So the provider **did** deliver at
least one real chunk. In `AnthropicApiClient.StreamSseAsync` (lines 452-505), the events that
actually `yield` a chunk are only:

* `content_block_start` **for `tool_use` blocks** (line 465)
* `content_block_delta` with `text_delta` (line 488) or `input_json_delta` (line 493)

`message_start` yields nothing (line 452-456 just records usage), and **`thinking_delta` is not
handled at all** — extended-thinking output is consumed and dropped, producing no chunk.

That gives a precise mechanism consistent with both failures:

> The model emitted a short opening text block (flipping `started` to `true` and arming the
> tight 120s budget), then entered a long silent phase — extended thinking, and/or the pause
> between finishing one content block and starting the next — while chewing through a context
> bloated by many full-page `--fetch` web results. Anthropic's `ping` keep-alives are not
> guaranteed within any 120s window and, even if they were, they are dropped by the reader
> before reaching the guard. The 120s window expired, the guard cancelled a perfectly healthy
> request, and the turn died.

Supporting circumstantial evidence:
* The pre-existing comment at `LlmStreamIdleGuard.cs:11` records coda measuring
  **135-183s time-to-first-token on large grounding prompts** — already well past 120s.
  The same physics apply to a mid-stream thinking pause on an even larger prompt.
* Both failures hit **only** the deep-research task (many full-page fetches → huge context),
  late in its run (round count high → context at its maximum). Concurrent sub-agents doing
  heavy fetching, chart rendering and email in the same window all finished cleanly —
  they had small per-turn contexts.
* Reproducible on a fresh restart at the same stage of the same workload. A network blip
  does not reproduce on cue; a deterministic budget does.

**Correlation with long tool calls: none.** The guard is not armed during tool execution
(see §2), and the longest observed tool call in the same session — 285s — passed unharmed.
Long tool calls are exonerated; large context is not.

**Residual uncertainty:** we cannot see the SSE wire, so "silent thinking" vs. "provider
genuinely stalled" is inferred, not proved. The remedy is the same either way, and the
telemetry in P3 settles it for next time.

---

## 5. Why a transport fault ended the task instead of being retried

Two independent defects compound.

### 5a. Retry and failover are pre-content-only — by design

`DirectLlmClient.StreamCompleteAsync` (~line 565):

```csharp
// Pre-content failover only: a terminal error before ANY chunk
// has reached the caller ...
if (!emittedAny && isTerminalError && more && IsErrorFailoverEligible(chunk.ErrorMessage))
```

and `StreamWithRetryAsync` likewise only retries a `preContentError`. The reasoning is sound
("we cannot un-stream what we already yielded"), and `LlmStreamFault.IsTransient` *does*
correctly classify this `TimeoutException` as transient — but that verdict is **never
consulted**, because `emittedAny` is already `true`. Result: the one failure mode the idle
guard exists to catch is the one failure mode the retry path cannot see. The guard's own
doc-comment (`LlmStreamIdleGuard.cs:51-53`) claims a stall "engages the same same-provider
retry and failover path as a dropped connection" — **that claim is false for any stall after
the first chunk**, which is the overwhelmingly common case.

### 5b. The parent is told "completed" regardless of terminal state

The store is correct — `SubagentRunner.ToTerminalState` maps `Error → Failed` (line 171) and
the log confirms `state changed to failed`. But the message the parent agent actually reads
is built by `SubagentExecutionCoordinator.BuildCompletionTriggerText` (line 529), which
**never looks at `task.State`**:

```csharp
return
    $"[Background task completed]\n" +
    $"Task: \"{task.Description}\" ({task.TaskId})\n\n" +
    $"Result:\n{result}\n\n" +
    $"Review the result and respond to the user. ...";
```

Combined with `SubagentRunner.cs:220-222`, which substitutes `result.ErrorMessage` as the
`responseText` for non-completed outcomes, the parent receives:

```
[Background task completed]
Task: "..." (sa-a61a32e...)

Result:
Stream transport fault: LLM stream produced no data for 120s.
```

A Failed, Cancelled and Completed task are **lexically indistinguishable** to the
orchestrator. That is the silent-data-loss-dressed-as-success the brief flagged, and it is
the highest-severity finding here: it is a one-line, zero-risk fix.

---

## 6. Was the work recoverable? Yes — and nobody knew

* `SubagentCallbacks` calls `store.UpdateMessages(taskId, messages, round)` after **every
  round** (line 152) and again on loop completion (line 208). The full conversation —
  including all fetched page text and the completed pass-1 findings — was durably persisted
  at the moment of death.
* `SubAgentSendTool` explicitly supports resuming a terminal task:
  `SubagentTaskState.Completed or Failed or Cancelled => …` (line 89) →
  `store.TryQueueResume(task.TaskId, message)` (line 124), which requeues as `RunMode.Resume`
  and clears the terminal markers.
* So a single `sub_agent_send(taskId, "continue from where you stopped")` would have resumed
  the 25-minute run instead of restarting it from zero — twice.
* The 13 KB `serv_research_pass1.md` and the 9 KB pass-2 partial in the agent data dir are
  likewise intact.

The gap is purely informational: the notification hides the failure, so neither the parent
agent nor the user was ever given the cue to resume.

**So no checkpointing work was needed** — the checkpointing already existed and worked. What
was missing was telling the orchestrator that resuming was possible.

---

## 7. Recommended fixes, ranked

### P0 — Make the completion notification state-aware (highest value, lowest risk)

Give Failed and Cancelled their own envelope, label the body as a failure rather than a
result, and tell the parent to resume by task id. Converts silent loss into an actionable,
resumable event. One file, no behavioural risk to the happy path.

### P1 — Let a post-content stall be retryable instead of terminal

The "cannot un-stream" objection does not apply to a **sub-agent**: nothing has been shown to
a user mid-stream; `AgentLoop` only accumulates into a `StringBuilder`.

* **(a) Retry-on-stall in `AgentLoop`** — on a terminal error chunk carrying
  `LlmStreamFault.TransientPrefix`, discard the dead attempt and re-issue the *same* request,
  bounded (2 attempts, 0.5s/2s backoff). Smallest correct change; mirrors the pre-content
  transport retry coda already ships.
* **(b) Auto-resume a Failed sub-agent once** in the coordinator, reusing `TryQueueResume`.
  Cheaper to write, but retries a whole round rather than one request.
* Do **not** widen `DirectLlmClient` failover to post-content: switching providers mid-stream
  would corrupt the message.

### P2 — Stop the guard from mistaking "thinking" for "dead"

* Emit a **no-op keep-alive chunk** for `ping`, `message_start` and `thinking_delta` so a
  thinking model can prove it is alive. The most surgical true fix: it removes the false
  positive without loosening real hang detection.
* Independently raise `LlmStreamIdleTimeoutSeconds` 120 → **300**. The 120s figure has no
  measured basis; 135-183s of legitimate quiet is already on record.
* Consider scaling the idle budget with prompt size, but only once telemetry justifies it.

### P3 — Telemetry to confirm and to catch regressions

* Log every breach at **Warning** with conversation/task id, provider, model, prompt size,
  elapsed idle, whether content had been emitted, and chunks received. Today a breach is
  indistinguishable from a network fault in the log.
* `AgentMetrics` counters split by phase (first-token vs between-chunks).
* A log line when a stall retry is attempted, so P1's effectiveness is measurable.

---

## 8. Answers to the seven questions, condensed

1. **Where** - `LlmStreamIdleGuard.cs:116` (message) + `LlmStreamFault.cs:25` (prefix), wired
   at `DirectLlmClient.cs:625`. A client-side watchdog, not the SSE reader, not the provider.
   Full call chain in section 1.
2. **The 120s watchdog** - idle time between consecutive stream chunks, re-armed per chunk;
   `AgentConfig.LlmStreamIdleTimeoutSeconds`, default 120; sibling first-token budget 300s.
   It exists because `HttpClient.Timeout` stops applying once headers are read.
3. **Client-side.** The guard fabricated the `TimeoutException` and cancelled the read itself.
   No provider error, HTTP status, reset or context-limit error in the log at either failure.
4. **Yes, context-correlated** - but the mechanism is a too-tight *between-chunks* budget after
   an early opening chunk, not a from-request-start measurement. Only the huge-context task
   failed; it failed twice, at the same stage; concurrent smaller-context sub-agents were fine.
5. **No correlation with long tool calls.** The guard does not run during tool execution - a
   285s `run_command` in the same session passed unharmed.
6. **Two bugs**: post-content faults are structurally excluded from retry/failover
   (`!emittedAny` gate), and `BuildCompletionTriggerText` ignores `task.State` so a Failed task
   is announced as `[Background task completed]`.
7. **Fully recoverable.** History is checkpointed every round and `sub_agent_send` resumes
   terminal tasks; the parent was simply never told the task had failed. No new checkpointing
   was needed - only a notification that admits failure.

---
## 9. What was actually implemented (post-investigation)

All four were built test-first, code-reviewed, merged via PR #43 (`afadf21` on `main`) and
deployed as agent image `0.2.328` / `sha256:52e2e77b3467`. **This changed production
behaviour** — the running `cortex-agent` container was rebuilt and recreated. Full suite:
4,904 tests passing, 0 failures; solution builds with 0 warnings.

| Commit | Fix |
|---|---|
| `bf94b9e` | P0 — `SubagentCompletionNotice` (new pure formatter); Failed/Cancelled envelopes; body labelled `Failure:`/`Outcome:`; parent told to resume via `sub_agent_send`; `sub_agent_start` description corrected |
| `a828107` | P1 — inner attempt loop in `AgentLoop`; `LlmStreamFault.IsTransientFaultMessage`; fresh request id per attempt; 0.5s/2s backoff |
| `e823a2c` | P2 — `LlmStreamChunk.IsKeepAlive`; Anthropic emits it for `message_start`/`ping`/`thinking_delta`/`signature_delta`; guard consumes it; budgets retuned; `MaxDuration` ceiling |
| `e59ea93` | P3 — `ILlmStreamStallObserver`, `LlmStreamContext`, `LlmStreamStallReport`, `LlmStreamStallPhase`, `LlmStreamStallTelemetry`, `AgentMetrics` counters |

### Where the implementation deliberately departs from the proposal

Three changes came out of code review and are **more conservative** than what §7 proposed:

1. **P1 defaults to OFF, not on.** §7 treated "sub-agents discard deltas" as a safe standing
   assumption. It is only safe *today*: `AgentLoop` is documented as the unified loop for the
   main agent too, and a user-facing consumer that re-issued after showing deltas would
   duplicate visible text. So `AgentLoopConfig.MaxTransientStreamRetries` defaults to `0` and
   `SubagentRunner` opts in explicitly. A future main-agent adoption inherits safety by
   saying nothing.

2. **P2 needed an absolute ceiling that §7 missed.** Once keep-alives re-arm the idle budget,
   *the idle budget stops bounding the stream at all*. A provider pinging every 30s, or a model
   stuck emitting thinking deltas, would hold the request, connection and sub-agent slot open
   **forever** — silent and non-terminating, strictly worse than the premature kill being
   fixed. `LlmStreamTimeouts.MaxDuration` (30 min, own stall phase, `LlmStreamMaxDurationSeconds`)
   is what now guarantees termination. **This must not be removed.**

3. **First-token budget raised too.** §7 proposed idle 120 → 300 "matching" the 300s
   first-token budget, which would have collapsed the deliberate two-budget design (TTFT must
   never be bounded more tightly than mid-stream silence). First-token went 300 → **600s** to
   preserve the ordering.

### Configuration now in force

| Setting | Was | Now |
|---|---|---|
| `LlmStreamIdleTimeoutSeconds` | 120 | **300** |
| `LlmFirstTokenTimeoutSeconds` | 300 | **600** |
| `LlmStreamMaxDurationSeconds` | — | **new**; 0 = built-in 30 min, negative = disabled |
| `SubagentTransientStreamRetries` | — | **new**; 2 |

---

## 10. Residual risk, stated deliberately

* **Worst-case dead air per round has grown**: 300s × (1 initial + 2 retries) + backoff ≈ 15.5
  minutes before a round gives up, bounded per attempt by the 30-minute ceiling. This is the
  intended trade against losing 25-minute runs to a budget with no measured basis, and it is
  documented in `AgentConfig`.
* **No per-sub-agent wall-clock deadline exists.** `MaxRounds` defaults to 200 with no total
  time cap, so a pathological task can still run a very long time. `MaxDuration` bounds each
  *stream*, not the task. Worth adding if this ever bites.
* **`PromptChars` ignores multimodal parts**, so the context-size field under-reports for
  attachment-heavy turns — precisely the largest contexts. Documented as a proxy.
* **The "silent thinking" mechanism is still inferred, not observed on the wire.** The P3
  telemetry (phase, idle elapsed, heartbeats seen, prompt size) will confirm or refute it the
  next time a breach happens — and heartbeats-seen > 0 on a breach would prove the model was
  alive and thinking.
