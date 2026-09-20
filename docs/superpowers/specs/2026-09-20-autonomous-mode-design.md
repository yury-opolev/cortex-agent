# Cortex autonomous mode — design

Status: **draft, for review** (branch `feat/coda-uptake-autonomy`)
Date: 2026-09-20

## Goal

Give Cortex a user-controlled **autonomous mode**: the agent is handed a goal, works until that goal
is genuinely met (or provably cannot be), and does not stop to ask the user things along the way.

The idea is borrowed from coda's `--goal` / autonomy supervisor
([coda #161](https://github.com/yury-opolev/coda-cli/pull/161), `coda-agent/src/autonomy/`), which
solves the same problem for a coding engine. This spec adapts it to Cortex's very different shape —
Cortex is not a coding agent, and most of coda's machinery turns out to be unnecessary here.

**Autonomous runs live in subagents, never in the main agent.** The main agent stays conversational
and responsive at all times; a long-running goal is always delegated. This is a deliberate decision
(see below) and it removes a large amount of the machinery an in-main-agent design would need.

## What Cortex actually needs (and what it does not)

An audit of every human-dependent seam in the Agent Host produced a short, surprising list.

**Cortex's main agent has exactly one blocking seam: it ends the turn.**
`AgentRuntime.RunTurnAsync` streams a response; if it contains no tool calls, the text is delivered
and the loop `break`s (`AgentRuntime.cs:~930-950`). Nothing resumes the agent until a new user
message arrives. An agent that says *"shall I proceed?"* and stops has blocked on a human without
ever calling an "ask" API.

Everything else one might expect is **absent**:

- There is **no** `ask_user` / `confirm` / `elicit` tool in the Agent Host.
- There is **no** permission or approval gate on Cortex's own dangerous tools — `RunCommandTool`,
  `FileWriteTool`, `FileEditTool`, `FileDeleteTool` all execute unconditionally.

This has a large consequence for scope: coda needed `permission.rs`, `plan.rs` and a
`RecoveryGuard` because coda has four blocking seams that wait on a human. **Cortex has one.** The
parts of coda's design worth porting are the ones that decide *"are we actually done?"* — the
completion judge, the budget, the stuck detector, the assumption ledger and the termination proof.
The permission machinery has nothing to attach to.

The corollary is worth stating plainly, because it inverts the usual intuition:

> Cortex's tools are already unattended. Autonomous mode does not make Cortex more dangerous by
> removing approvals — there are none to remove. What makes an autonomous run safe is that it is
> **bounded** (budget), **loop-aware** (stuck detector) and **auditable** (ledger). Those three are
> the safety story, not permissions.

## Non-goals

- No permission/approval system for Cortex's own tools. That is a separate, larger piece of work;
  this spec neither adds nor assumes one.
- No change to coda's own autonomy. Cortex already passes `--goal` to `coda serve`; a coda goal run
  supervises itself and is out of scope here.
- Autonomous mode is **not** the default for ordinary conversation, and never auto-enters.
- No new channel and no new transport. Progress flows to the parent through mechanisms that already
  exist (parent-visible todos, `sub_agent_read`, the terminal completion notice) — subagents
  deliberately cannot message the user directly.

## Why subagents only, and not the main agent

The main agent must stay available for conversation **while** a long-running goal is in flight. If
autonomy lived in `AgentRuntime`, an autonomous run would occupy the very turn loop the user talks
to: chatting mid-run would either interrupt the run or be queued behind it.

Delegating instead is a strictly better fit, and the machinery already exists:

- `SubAgentStartTool` persists the task and returns a `task_id` **immediately** — the parent never
  blocks. The main agent is free the instant it delegates.
- Subagents are already structurally unattended: `SubagentRunner.s_excludedTools` removes
  `send_message`, `sub_agent_*`, `schedule_task` and `session_timer`, so a subagent *cannot* stop
  and ask the user even if it wanted to. `SystemPromptDefaults.SubagentInstructions` already says
  *"Work autonomously — do not ask clarifying questions."*
- Each subagent already gets **its own channel** (`subagent-{taskId}`, `SubagentRunner.cs:213-218`),
  explicitly so that coda sessions started by concurrent subagents do not collide. Coda keys
  sessions by channel, so the *ownership* is already modelled correctly — though delivery of coda's
  requests into the running subagent is **not** yet wired (see the routing gap below).
- Long-run durability already exists: `SubagentTask` is SQLite-persisted, in-flight tasks are
  requeued on shutdown and crash recovery releases interrupted ones
  (`SubagentExecutionCoordinator`, `SubagentSessionStore`), with `RunMode.Resume` replaying stored
  messages. A multi-day run survives a container restart.
- Concurrency is already governed by `SubagentRunnerRegistry.MaxConcurrent`.

Consequences for this spec, all simplifications:

- **No hook in `AgentRuntime` at all.** The end-of-turn interception, the
  `AgentMessageSource.AutonomousContinuation` re-enqueue and the main-agent mode state are all
  dropped. The supervisor attaches to the shared `AgentLoop` / `SubagentRunner` only.
- **No barge-in semantics to decide.** The user is never competing with the run for the agent.
- **No per-conversation vs per-agent scoping question.** A run is scoped to a subagent task.
- The main agent's role becomes: *delegate, report, and answer questions about* the run — using the
  existing `sub_agent_read` / `sub_agent_send` / `sub_agent_stop` tools.

## Design

### The core loop

The supervisor wraps the point where a subagent's loop decides it is finished — `AgentLoop`
returning `Completed` because the model produced final text with no tool calls.

Today that is terminal. Under a goal:

```
loop completes with final text
  -> supervisor.EvaluateAsync(goal, transcript, ledger, budget)
       -> Done(report)      -> terminal, report to parent
       -> Blocked(report)   -> terminal, report to parent
       -> Continue(remaining) -> feed `remaining` back in, run again
```

The continuation channel already exists: `SubagentRunner.InjectMessage` enqueues onto the runner's
pending session and `SubagentCallbacks.DrainInjectedMessages` drains it each round. The judge's
`remaining` text is injected exactly the way a user follow-up would be, so no new message path is
introduced.

Terminology, because the two are easy to conflate and the budgets below depend on the distinction:

- **round** — one LLM call inside `AgentLoop` (bounded by `SubagentRunner.DefaultMaxRounds`)
- **continuation** — one judge decision to keep going, each of which runs a fresh bounded loop

### The completion judge

A single cheap LLM call, on the same model provider, replying in a fixed shape:

```
DONE
CONTINUE: <what is still missing>
```

Copying two hard-won properties from coda:

- **Fails open.** A judge error or unparseable reply means `Continue`, never `Done`. A broken judge
  must not silently declare success.
- **`remaining` is fed forward** into the continuation so the next turn is told what is missing,
  rather than re-deriving it.

### Budget — a backstop, not a control

The budget exists to guarantee termination if everything else fails. It is deliberately set
**absurdly high**, because a run that stops early because it ran out of turns is a worse failure
than one that runs long:

| Budget | Default | Override |
|---|---|---|
| wall-clock | **7 days** | longer at start, when the work is known to be bigger |
| continuations | **10000** | higher at start, when the work is known to be bigger |

The real controls are the completion judge and the stuck detector. If those are working, the budget
is never reached; if they fail, the budget is what stops a runaway. coda's 240h / 60000 defaults are
the same philosophy at a different scale.

Two notes on what this implies:

- Budget is measured as **consumed** and persisted on the task, so a container restart resumes a
  run's clock rather than resetting a 7-day budget to zero.
- A continuation runs a whole bounded loop, so the theoretical ceiling is
  `10000 × SubagentRunner.DefaultMaxRounds` LLM calls. That is an enormous number and is *meant* to
  be unreachable. The per-continuation round cap stays as-is and remains the thing that bounds any
  single continuation.
- `AgentLoopOutcome.MaxRoundsExceeded` currently maps to `SubagentTaskState.Failed`
  (`SubagentRunner.cs:191`). Under a goal this must instead feed the supervisor, which decides
  `Stalled` vs `Continue` — "one loop hit its round cap" is not the same as "the task failed".

### Stuck detection

Port coda's `stuck.rs` heuristics, which are the difference between "bounded" and "bounded only by
the budget":

- same action + same observation ≥ 4 times
- same action erroring > 3 times
- no tool calls and no progress for 3 consecutive turns (monologue)
- a repeating cycle of period 2–4 seen 3 times

Detection over **events**, not just actions, so `[A, think, A, think]` is caught. On detection, the
run is nudged once with an explicit description of the loop; if the loop persists, the run ends as
`Stalled` rather than burning the remaining budget.

### Termination proof

`Done` is not the only honest ending. Outcomes:

| Outcome | Meaning |
|---|---|
| `Met` | judge says the goal is satisfied |
| `GenuinelyBlocked` | every remaining item has a parked blocker with a non-empty `tried` list |
| `Stalled` | looping with nothing left to advance |
| `BudgetExhausted` | ran out of time or continuations |

Progress for the no-progress window is measured from **observable** signals — tool calls made, files
changed, distinct new ledger entries — never from the judge's prose, which will happily narrate
progress that did not happen.

### The assumption ledger

Every decision the agent makes *instead of asking the user* is recorded: `Assumption`,
`ParkedBlocker`, `Recovery`, `AnsweredForUser`. Two rules taken from coda's `ledger.rs`:

- **Exhaustion rule** — parking a blocker requires a non-empty `tried`. "I could not do it" is not a
  blocker until something was attempted.
- **Redaction on write** — free text goes through the existing secret redaction before storage.

The ledger is the final report. It is what makes an unattended run reviewable rather than a wall of
confident text, and it is the honest answer to "what did it decide while I was asleep?"

### Auto-answering coda — and a routing gap that must be fixed first

Driving coda is the main job of an autonomous run. Today coda's `request/permission`,
`request/question` and `request/planApproval` are relayed *to the human*:
`CodaJsonRpcConnection` → `CodaSession` → `CodingHubBinder` → `CodingAgentInjectionService`
(`OnPermissionRequest` / `OnQuestion` / `OnPlanApproval`).

**Blocker — envelopes cannot currently reach a subagent.** Since autonomy now lives in subagents,
this matters more than the prompt wording. Tracing the path:

- A subagent's coda session is keyed to its own channel, `subagent-{taskId}`
  (`SubagentRunner.cs:213-218`, `ChannelId = conversationId`).
- `CodingAgentInjectionService.Enqueue` (`:239-252`) builds
  `AgentMessage { ConversationId = channelId, ChannelId = channelId, Source = CodingAgentInjection }`
  and pushes it onto the **main `AgentMessageChannel`**.
- That queue is drained by `AgentRuntime`, which would treat `subagent-{taskId}` as an ordinary
  conversation.
- The only way into a running subagent is `SubagentRunner.InjectMessage`, and its **only** caller is
  `SubAgentSendTool`.

The two paths never meet. So a subagent that starts a coda session and hits a question today has its
envelope delivered to a phantom `AgentRuntime` conversation that nobody reads — the subagent never
sees it, and neither does the user. The pending request then sits until the Bridge's expiry
fallback resolves it (permission and plan **refused** by default, `CodaSession.cs:926-994`).

**Required fix:** `CodingAgentInjectionService` must route by channel — when the owning channel is a
`subagent-` channel with a live runner, deliver via `SubagentRunner.InjectMessage` instead of the
`AgentMessageChannel`; otherwise behave exactly as today. `TodoStoreResolver` already establishes
the `"subagent-"` prefix as a routing discriminator, so the convention exists.

This is worth fixing on its own merits, independent of autonomy: it is a live bug for any subagent
that drives coda.

**Then the prompt.** Unlike the main agent, the subagent template has **no relay guidance at all** —
`SystemPromptPlaceholders.Subagent` is `{personality, skill, instructions, skills,
bootstrap_context, recalled_memories}`, with no `coding_relay`, and
`SystemPromptDefaults.SubagentInstructions` never mentions coda envelopes or
`coding_session_respond`. So this is not a segment swap (as an earlier draft of this spec assumed);
it is **adding** relay guidance to the subagent prompt:

- add `coding_relay` to the Subagent placeholder set
- add `SystemPromptConfig.CodingRelayAutonomous`: recognise the envelope, answer it yourself via
  `coding_session_respond`, log an `AnsweredForUser` ledger entry with the choice and the reason,
  never fabricate an option

Adding a placeholder is backward-compatible — existing customised subagent templates simply do not
reference it and keep rendering exactly as before.

Two safety rules ported from coda's `answerer.rs`, both of which were bugs it had to fix:

- Option resolution is **exact, case-insensitive only**. Substring matching once read
  *"Do not delete"* as *"Delete"*.
- If no option can be resolved, **park the blocker** — never guess.

A cheaper complementary move, available **today** with no code change: have the main agent pass a
`goal` to `coding_session_start`, so coda supervises *itself* and never raises the question. That
does not replace the above — coda can still ask when its own answerer parks — but it removes most
of the traffic.

### The main agent's control surface

The main agent must be able to **start** a subagent in autonomous mode and **change that mode while
the run is in flight**. The precedent for the whole shape already exists in this codebase:
`coding_session_set_goal` does exactly this for coda sessions, and the main agent already knows how
to reason about it.

**Extend `sub_agent_start`** with the same optional fields:

```
sub_agent_start({ task, description, goal?, maxDuration?, maxContinuations? })
```

`goal` absent = today's behaviour exactly (a plain background subagent). `goal` present = an
autonomous run, judged to completion.

**Add `sub_agent_set_goal`**, mirroring `coding_session_set_goal` field-for-field:

```
sub_agent_set_goal({ taskId, goal?, maxDuration?, maxContinuations? })
```

- **set** a goal on a plain running subagent → it becomes autonomous from that point
- **replace** the goal or the budget on an autonomous run → it re-aims
- **clear** (empty or omitted `goal`) → it reverts to a plain subagent and ends at its next natural
  completion

Two rules copied deliberately from the coda tool, because they prevent real mistakes:

- **No merge.** Always send the full goal text when changing a budget. A partial update that
  silently dropped the goal while "just raising the budget" would turn an autonomous run into an
  interactive one without anyone noticing.
- **Effective at the next boundary**, not mid-round. coda's wording is *"takes effect from the next
  `coding_session_send`"*; here the equivalent is the next continuation boundary. Live state is held
  thread-safely on the runner and read at that boundary — the same pattern as
  `SubagentRunnerRegistry.SetMaxConcurrent`, which is already a live, no-restart mutation.

`sub_agent_read`, `sub_agent_send` and `sub_agent_stop` need no change: reading gives progress,
sending injects steering, stopping cancels. `sub_agent_read` should additionally surface goal state
(goal text, outcome so far, continuations used, elapsed, what remains) the way
`coding_session_status` surfaces `goalStatus`.

### Persistence

Goal, budget limits and **consumed** budget live on `SubagentTask` alongside the existing
`Rounds` counter, so they survive the requeue/`RunMode.Resume` path that already handles restarts.
Without persisting *consumed* budget, every restart would silently hand the run a fresh 7 days.

### Prompt guidance

The main agent needs to know when to reach for this. Unlike the coda relay — where goal mode is
described as *"off by default… use only when the user explicitly asks"* — a long-running task
delegated to a subagent is the **expected** use, since that is now the only place autonomy lives.
The main-agent prompt should say: when the user asks for something long-running or explicitly
autonomous, delegate it with a `goal`; report progress on request via `sub_agent_read`; and use
`sub_agent_set_goal` to re-aim or stand down a run.

Wiring the same supervisor into the shared `AgentLoop` gives subagents goal-checked completion and
turns "ran out of rounds" into a truthful `Stalled` / `BudgetExhausted` outcome with a ledger.

## The mid-loop gate (do not repeat coda's bug)

coda shipped this design with a latent hole worth calling out explicitly: the stop decision is only
reached **when a turn calls no tools**. An agent that calls a tool every single turn never reaches
it, so every budget and stuck check behind it is dead code.

A subagent is not unbounded — `SubagentRunner.DefaultMaxRounds` caps a loop — but budget and stuck
detection must still be evaluated **inside** `AgentLoop`, after each tool round, not only where the
loop completes. coda's fix was `AutonomySupervisor::check_mid_turn()`; Cortex needs the equivalent
call in `AgentLoop`'s round loop. Deliberately **not** the completion judge — that would be an LLM
call per tool round.

## Visibility

A 7-day run must not be silent, and subagents **cannot** message the user (`send_message` is in
`s_excludedTools`, deliberately — it is what makes them structurally unattended). Progress therefore
flows through the parent, not around it:

- `todos_write` already works and is explicitly *"visible to the main agent"*
  (`SystemPromptDefaults.SubagentInstructions`) — the cheapest progress signal, already built.
- `sub_agent_read` gives the parent the transcript on demand; it should additionally surface goal
  state (outcome so far, continuations used, elapsed, remaining).
- The existing terminal `[Background task completed]` notification delivers the final report.

Open question below: whether that is enough, or whether a long run should be able to push an
unprompted progress note to the parent conversation.

## Risks

| Risk | Mitigation |
|---|---|
| Judge declares success that did not happen | fails open; progress measured from observable signals, not prose |
| Cost of a long unattended run | judge is one cheap call per continuation; stuck detector ends loops early; budget is a backstop, not the control |
| Subagent never receives coda's question | **routing fix is a prerequisite** (see above); without it, requests expire and are refused by default |
| Agent answers a coda question wrongly | exact-match option resolution; park instead of guess; every answer ledgered |
| Run outlives its usefulness | stuck detector ends `Stalled` runs long before a 7-day budget drains |
| Container restart loses or resets a long run | goal, limits and **consumed** budget persisted on `SubagentTask`; existing requeue + `RunMode.Resume` |
| A long run is silent | todos visible to the parent; `sub_agent_read` on demand |
| `MaxRoundsExceeded` misreported as failure | under a goal it feeds the supervisor, not `SubagentTaskState.Failed` |

## Suggested phasing

1. **Routing fix** — deliver coda envelopes to the owning subagent runner. Independently valuable;
   a live bug today.
2. `AutonomySupervisor` + completion judge + backstop budget, wired into `AgentLoop` /
   `SubagentRunner`; `goal` on `sub_agent_start`. Smallest genuinely useful increment.
3. Ledger + termination proof + stuck detector + mid-loop gate.
4. `sub_agent_set_goal` (live re-aiming), goal state in `sub_agent_read`, persistence of consumed
   budget.
5. `coding_relay` placeholder + `CodingRelayAutonomous` for the subagent prompt.

## Open questions for review

1. **Progress cadence** — are parent-visible todos plus `sub_agent_read` enough, or should a long
   run be able to push an unprompted progress note to the parent conversation? This is the one
   place where the "subagents cannot message the user" rule is genuinely inconvenient.
2. **Nested delegation** — `sub_agent_start` is excluded from subagents, so an autonomous run cannot
   subdivide its own work. Is that acceptable for multi-day goals?
3. **Judge model** — same model as the run (simple, shares the provider), or a cheaper one, given it
   fires once per continuation?

## Resolved

**Concurrency: a subagent is a subagent.** An earlier draft asked whether autonomous runs should get
their own pool so multi-day runs cannot starve short-lived ones. **Rejected** — there is one pool and
one cap (`SubagentRunnerRegistry.MaxConcurrent`), and autonomy is a property of a run, not a class of
subagent.

The decisive argument is `sub_agent_set_goal`: it can turn a plain subagent into an autonomous one
(and back) *mid-run*, so a two-pool design would have to migrate a running task between pools on a
live mode change. That is a large amount of machinery — pool assignment, migration, priority,
starvation rules — to express something the existing cap already expresses. If long runs do crowd
out short ones, the answer is to raise the cap, which is already live-editable from the Bridge
settings page with no restart (`SubagentRunnerRegistry.SetMaxConcurrent`). The default was raised
from 5 to 10 for exactly this reason.
