# Sub-agent crash: "Stream transport fault: LLM stream produced no data for 120s."

Investigation only — **no production behaviour was changed.** All code below is quoted as-is;
the "Proposed fixes" section contains patches that are *not* applied.

Repo: `C:\Users\yurio\Documents\github\cortex-agent` · Logs: `docker logs cortex-agent`
(the agent runs in the `cortex-agent` container; the host-side `%LOCALAPPDATA%\Cortex\logs\bridge-*.log`
files do NOT contain this error).

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

**Residual uncertainty:** we cannot see the SSE wire, so "silent thinking" vs. "provider
genuinely stalled" is inferred, not proved. The remedy is the same either way, and
§7 lists the telemetry that would settle it.

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

---

## 7. Proposed fixes — ranked, NOT APPLIED

### P0 — Make the completion notification state-aware (1 file, ~10 lines, zero risk)

`SubagentExecutionCoordinator.BuildCompletionTriggerText`:

```csharp
var header = task.State switch
{
    SubagentTaskState.Completed => "[Background task completed]",
    SubagentTaskState.Failed    => "[Background task FAILED]",
    SubagentTaskState.Cancelled => "[Background task cancelled]",
    _                           => "[Background task finished]",
};
var guidance = task.State == SubagentTaskState.Failed
    ? $"The task did NOT finish. Its work up to the failure is preserved. "
    + $"Use sub_agent_send('{task.TaskId}', '<continue instruction>') to RESUME it "
    + $"rather than starting a new task."
    : "Review the result and respond to the user. ...";
```

This alone converts silent loss into an actionable, resumable event.

### P1 — Let a post-content stall be retryable instead of terminal

The "cannot un-stream" objection does not apply to a **sub-agent**: nothing has been shown to
a user mid-stream; `AgentLoop` only accumulates into a `StringBuilder`. Options, cheapest first:

* **(a) Resume-on-stall in `AgentLoop`.** On a terminal error chunk whose message starts with
  `LlmStreamFault.TransientPrefix`, and where the round produced no complete tool call, discard
  the partial `fullResponse` and re-issue the *same* request (bounded, e.g. 2 attempts with
  0.5s/2s backoff — mirroring `DirectLlmClient.RetryBackoff`). This is exactly the pre-content
  transport retry that coda already ships (see the coda `AgentLoop` mid-stream retry work) and
  is the smallest correct change.
* **(b) Auto-resume a Failed sub-agent once**, in the coordinator, when the failure message is
  transient-prefixed — reusing the existing `TryQueueResume` machinery. Cheap, but retries a
  whole round rather than a request.
* Do **not** widen `DirectLlmClient` failover to post-content: switching providers mid-stream
  would corrupt the message.

### P2 — Stop the guard from mistaking "thinking" for "dead"

* Feed Anthropic's liveness events to the guard. Today `message_start`, `ping`, and
  `thinking_delta` all yield nothing, so a thinking model looks identical to a hung socket.
  Emit a **no-op keep-alive chunk** (no content, no tool delta — `AgentLoop` already ignores
  such a chunk: it only acts on `ContentDelta`, `ToolCallDeltas`, `IsComplete`, `ErrorMessage`)
  for `ping` and `thinking_delta`. This is the most surgical true fix: it removes the false
  positive without loosening any real hang detection.
* Independently, raise `LlmStreamIdleTimeoutSeconds` from 120 → **300**, matching
  `LlmFirstTokenTimeoutSeconds`. The 120s figure has no measured basis; the file's own comment
  documents 135-183s legitimate quiet periods. This is a one-line config change and could be
  applied immediately as mitigation while P2's keep-alive work is done.
* Consider scaling the idle budget with prompt size (the failure correlates with context), but
  only after telemetry justifies it.

### P3 — Telemetry to confirm and to catch regressions

* Log every guard breach at **Warning** with: conversation/task id, provider, model, round,
  estimated prompt tokens, elapsed since request start, chunks received so far, bytes received.
  Right now the breach is indistinguishable from a network fault in the log.
* Counter in `AgentMetrics` for guard breaches split by `first-token` vs `between-chunks`.
* A log line when a stall retry is attempted/succeeds, so P1's effectiveness is measurable.

---

## 8. Answers to the seven questions, condensed

1. **Where** — `LlmStreamIdleGuard.cs:116` (message) + `LlmStreamFault.cs:25` (prefix), wired
   at `DirectLlmClient.cs:625`. A client-side watchdog, not the SSE reader, not the provider.
2. **The 120s watchdog** — idle time between consecutive stream chunks, re-armed per chunk;
   `AgentConfig.LlmStreamIdleTimeoutSeconds`, default 120; sibling first-token budget 300s.
3. **Client-side.** The guard fabricated the `TimeoutException` and cancelled the read itself.
   No provider error, HTTP status, reset or context-limit error in the log at either failure.
4. **Yes, context-correlated** — but the mechanism is a too-tight *between-chunks* budget after
   an early opening chunk, not a from-request-start measurement. Only the huge-context task
   failed; it failed twice, at the same stage.
5. **No.** The guard does not run during tool execution — a 285s `run_command` in the same
   session passed unharmed.
6. **Two bugs**: post-content faults are structurally excluded from retry/failover
   (`!emittedAny` gate), and `BuildCompletionTriggerText` ignores `task.State` so a Failed task
   is announced as `[Background task completed]`.
7. **Fully recoverable.** History is checkpointed every round and `sub_agent_send` resumes
   terminal tasks; the parent was simply never told the task had failed.
