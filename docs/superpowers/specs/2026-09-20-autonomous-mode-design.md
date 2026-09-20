# Cortex autonomous mode — design

Status: **draft, for review** (branch `feat/coda-uptake-autonomy`)
Date: 2026-09-20

## Goal

Give Cortex a user-controlled **autonomous mode**: the agent is handed a goal, works until that goal
is genuinely met (or provably cannot be), and does not stop to ask the user things along the way.
The user turns it on, can watch it, and can turn it off.

The idea is borrowed from coda's `--goal` / autonomy supervisor
([coda #161](https://github.com/yury-opolev/coda-cli/pull/161), `coda-agent/src/autonomy/`), which
solves the same problem for a coding engine. This spec adapts it to Cortex's very different shape —
Cortex is not a coding agent, and most of coda's machinery turns out to be unnecessary here.

Two homes, **one supervisor**:

1. **Main agent** — a mode the user enters and exits. Wired first.
2. **Subagents** — already structurally unattended; they gain the same judge and budget.

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
- No new channel, no new transport. Progress uses the existing proactive-message path.

## Design

### The core loop: one hook, at the one seam

A new `AutonomySupervisor` (Agent Host, `Agent/Autonomy/`) wraps the end-of-turn decision.

Today:

```
final text with no tool calls  ->  deliver  ->  break  ->  wait for a human
```

Under an active run:

```
final text with no tool calls
  -> supervisor.EvaluateTurnEndAsync(goal, transcript, ledger, budget)
       -> Done(report)      -> deliver the report, exit the mode
       -> Blocked(report)   -> deliver the report, exit the mode
       -> Continue(remaining) -> enqueue a continuation, do NOT return to the user
```

A **continuation** is an `AgentMessage` re-enqueued onto the existing `AgentMessageChannel` with a
new `AgentMessageSource.AutonomousContinuation`, carrying the judge's `remaining` text. This
deliberately reuses the mechanism `SchedulerService` already uses to drive the agent with nobody
watching (`AgentMessage { ConversationId, ChannelId, Source = ScheduledTask }`) — a scheduled task
is already an unattended run, so the lane, serialization and session handling are proven.

Re-enqueueing rather than looping in place matters: it keeps per-channel message serialization,
barge-in, compaction and cancellation working exactly as they do today, instead of introducing a
second inner loop with its own half-correct copy of those semantics.

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

### Budget

Wall-clock + continuation count, both user-settable at entry, both with generous defaults
(proposed: **4 hours / 200 continuations** — Cortex turns are far heavier than coda's, so coda's
240h/60000 is the wrong scale). Exhaustion ends the run and reports; it never silently keeps going.

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

### Auto-answering coda (the biggest immediate win)

Cortex's main job in autonomous runs is driving coda. Today coda's `request/permission`,
`request/question` and `request/planApproval` are relayed *to the human*:
`CodaJsonRpcConnection` → `CodaSession` → `CodingHubBinder` → `CodingAgentInjectionService`
(`OnPermissionRequest` / `OnQuestion` / `OnPlanApproval`), which enqueues a synthetic user message.
The `coding_relay` system-prompt segment then instructs the agent to *"Ask the user to
allow_once / allow_always / deny"*.

**No plumbing change is needed.** The answer channel already exists — `coding_session_respond`
(`CodingSessionRespondTool`) — and the agent already receives the request. The change is which
prompt segment fills the existing `{{coding_relay}}` placeholder:

- attended → `SystemPromptConfig.CodingRelay` (unchanged, today's text)
- autonomous → new `SystemPromptConfig.CodingRelayAutonomous`: answer it yourself, log an
  `AnsweredForUser` ledger entry with the choice and the reason, never fabricate an option.

Because the placeholder set is unchanged, **every user-customized template keeps working** — this
respects the byte-identity guarantee the customizable-system-prompt feature is built on.

Two safety rules ported from coda's `answerer.rs`, both of which were bugs it had to fix:

- Option resolution is **exact, case-insensitive only**. Substring matching once read
  *"Do not delete"* as *"Delete"*.
- If no option can be resolved, **park the blocker** — never guess.

### User control: entering and exiting

Entry (all three set the same state):

- tool `autonomous_start(goal, maxDurationMinutes?, maxContinuations?)`
- slash command `/auto <goal>` and `/auto stop`
- Bridge web UI control on the session

Exit:

- `autonomous_stop`, `/auto stop`, or the UI
- the supervisor reaching any terminal outcome
- **barge-in**: a real user message (`AgentMessageSource.User`) arriving mid-run. Default is
  **pause and ask**, not silent cancel — if the user is talking to it, the run should yield.

### Visibility

Silent multi-hour runs are unacceptable in practice. The run emits progress through the existing
`IProactiveMessageDispatcher` at a throttled interval (proposed: on outcome change, on nudge, and at
most every N continuations), and a final report containing the outcome, the elapsed budget and the
full ledger.

### State and persistence

`AgentSession` is in-memory and keyed by `ConversationId`; a long run represented only there dies
with the container. Autonomous run state (goal, budget consumed, ledger, outcome) is persisted in
SQLite alongside the existing scheduler/subagent stores, so a restart can resume or at minimum
report honestly. This mirrors `SubagentTask` + `SubagentSessionStore`, which already solve exactly
this for background work.

### Subagents

Subagents are already most of the way there: they cannot message the user at all (`send_message`,
`sub_agent_*`, `schedule_task` and `session_timer` are in `SubagentRunner.s_excludedTools`), and
`SystemPromptDefaults.SubagentInstructions` already says *"Work autonomously — do not ask clarifying
questions."* What they lack is a judge and a budget: the only bound is
`SubagentRunner.DefaultMaxRounds = 200`, and `AgentLoopOutcome.MaxRoundsExceeded` maps to
**`SubagentTaskState.Failed`** (`SubagentRunner.cs:191`) — indistinguishable from a real error.

Wiring the same supervisor into the shared `AgentLoop` gives subagents goal-checked completion and
turns "ran out of rounds" into a truthful `Stalled` / `BudgetExhausted` outcome with a ledger.

## The mid-turn gate (do not repeat coda's bug)

coda shipped this design with a latent hole worth calling out explicitly, because Cortex's structure
invites the same mistake: the stop decision is only reached **when a turn calls no tools**. An agent
that calls a tool every single turn never reaches it, so every budget and stuck check behind it is
dead code.

Cortex is not unbounded — `MaxToolRounds = 200` caps a turn — but budget and stuck detection must
still be evaluated **inside** the tool loop, after each tool round, not only at the end-of-turn hook.
coda's fix was `AutonomySupervisor::check_mid_turn()`; Cortex needs the equivalent call in
`AgentRuntime`'s round loop and in `AgentLoop`. Deliberately **not** the completion judge — that
would be an LLM call per tool round.

## Risks

| Risk | Mitigation |
|---|---|
| Judge declares success that did not happen | fails open; progress measured from observable signals, not prose |
| Cost of a long unattended run | budget defaults sized for Cortex; progress reporting; judge is one cheap call per turn end |
| Agent answers a coda question wrongly | exact-match option resolution; park instead of guess; every answer ledgered |
| Run outlives its usefulness | stuck detector ends `Stalled` runs before the budget drains |
| Container restart loses a long run | run state persisted in SQLite |
| Silent divergence from user intent | barge-in pauses; throttled progress; ledger reviewable after the fact |

## Suggested phasing

1. `AutonomySupervisor` + completion judge + budget + continuation re-enqueue, main agent, tool-only
   entry/exit. Smallest thing that is genuinely useful.
2. Ledger + termination proof + stuck detector + mid-turn gate.
3. `CodingRelayAutonomous` segment (auto-answering coda).
4. Subagent wiring; persistence; slash command and Bridge UI.

## Open questions for review

1. **Budget defaults** — is 4h / 200 continuations the right scale?
2. **Barge-in semantics** — pause-and-ask (proposed) or hard cancel?
3. **Should autonomous mode be per-conversation or per-agent?** Per-conversation is proposed, since
   `AgentSession` is already keyed that way and it lets one channel run autonomously while another
   stays interactive.
4. **Progress cadence** — every N continuations, time-based, or outcome-change only?
