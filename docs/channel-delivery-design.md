# Channel-correct, reliable outbound delivery

**Status:** Design proposal for review; no production changes applied.

**Repository:** `C:\Users\yurio\Documents\github\cortex-agent` (`https://github.com/yury-opolev/cortex-agent.git`). This is the repository containing the Agent Host tools, Bridge, and Discord/webchat adapters described as “assistant-bridge” in the incident report.

**Source baseline:** HEAD `b082e47` (`Merge pull request #57 from yury-opolev/feat/autonomous-subagents`). Line references below describe the working-tree source inspected during this investigation. `version.json` was already modified before investigation and was left alone.

## Decisions log

- **2026-10-03 — Later user decision: narrow the production fix to channel knowledge and origin metadata.** For the current implementation, preserve `send_message`'s explicit required channel and omission error, and preserve `schedule_task`'s existing optional channel handling. Do not implement origin-default tool arguments or schema changes. Instead, label Discord voice in the prompt, preserve `IsVoice`, persist scheduled origin separately from any explicit target, tell fired timer/task runs their origin, and remove the false webchat-fallback inspection text. This later scope decision supersedes the origin-default tool-contract proposal below for this PR; the broader outbox/registry/lifecycle design remains unimplemented follow-up work. See `docs/superpowers/plans/2026-10-03-voice-timer-origin-routing.md` for the scoped implementation and verification record.

- **2026-10-03 — User decision: cross-channel override is not gated.** The agent must always receive authoritative current-channel context, with no blank prompt labels or guessing. Timers and scheduled work capture the originating session channel at creation, persist it with the work, and expose it to whichever agent instance handles firing. Proactive delivery defaults to that origin: omitting `channel` is normal and correct. The agent may freely choose an explicit target as a judgement call, without proof of user consent or an authorization reference. Full destination validation still applies (tenant, audience, endpoint existence, outbound capability, and reachability), and invalid or unauthorized destinations fail loudly. The former consent gate is deliberately removed; origin-by-default, validation, and visible receipts are the safeguards, with the residual risk of a wrong-but-valid explicit choice acknowledged below.

## 1. Executive summary

The reported failure is consistent with **model-selected routing replacing available authoritative context**. It is not a timer-duration problem or an implemented fallback from Discord voice to webchat.

The source contains a specific contributing defect: `discord-voice` has `PromptLabel: null`. The prompt assembler consequently omits the “user is currently talking to you via …” sentence for that channel. Meanwhile, `send_message` requires the model to supply a channel, and `schedule_task` ignores its supplied `ToolExecutionContext` when creating a task. It stores the model's optional channel argument, not the originating channel. A valid but wrong value such as `webchat-default` is accepted and preserved on execution.

`session_timer` is different: it already captures both originating conversation and channel. Its fired intent returns to that conversation, but **that is not user delivery**. A focused model run must call `send_message`, again choosing a channel. Ordinary final text from this run is operational output, not speech. Thus the same channel-selection defect can recur after an otherwise correctly routed timer fires.

The proposed system makes delivery a runtime responsibility:

1. Capture an immutable, authenticated origin envelope once; distinguish channel, tenant, endpoint, conversation, and execution identifiers.
2. Inherit a structured delivery binding for all async work. The model normally supplies only content/intent; the destination defaults to origin. The agent may freely choose an explicit cross-channel override, subject to full destination validation but no consent gate.
3. Use a tenant-scoped registry with outbound capabilities, reachability, physical endpoint identity, and explicit alternate-route policy. Never infer an address from a session-name string.
4. Persist outbound requests and their outcomes. Separate “accepted”, “queued”, “transport accepted”, “playback completed”, and “user acknowledged”. Never claim the user heard a message merely because it entered a queue.
5. Retry transient failures within a freshness deadline, expose failures to the originating conversation, and make timer/schedule completion independent of enqueue success.
6. Define moves, ends, absence, interruption, and restart as explicit lifecycle transitions.

A prompt-label correction alone would help, but would not make this reliable. The migration below deliberately avoids treating it as the complete fix.

## 2. Investigation evidence and limits

### 2.1 Incident evidence supplied by the user

- Gym sessions on 2026-09-28, 2026-09-30, and 2026-10-03 were on `discord-voice`.
- Task `fe637c75`, description `90s rest cue`, had `channel = webchat-default` and instructions explicitly saying to send the cue on `webchat-default`.
- Earlier timer attempts and a subsequent `send_message` retry also failed to reach voice; the retry explicitly selected webchat.

Those records identify a wrong destination **at task creation**, before any scheduler isolation. This investigation did not independently retrieve that task from the running service's database.

### 2.2 Verification performed

Read the actual source, related tests, and repository identity; searched the available Coda diagnostic directory `C:\Users\yurio\.coda\logs\diagnostics\`. At the time of the incident search it contained one active 2026-10-03 JSONL log, not a three-session archive. No incident-specific task/cue record was found in that search. No app was started, no live cue sent, and no tests were run. The findings are a source-level diagnosis consistent with the supplied incident, not a claim that the deployed binary or all three historical prompts were verified.

To close that remaining evidentiary gap, obtain the deployed build ID, original tool arguments and results, rendered prompt, active-channel snapshot, persisted task row, and Bridge/adapter events for `fe637c75`. Correlate creation, firing, and delivery; do not assume a Coda tool log is a Discord playback receipt.

### 2.3 Exact source map: current channel and tool execution

All paths are relative to the repository root.

| Stage | File and lines | Finding |
|---|---|---|
| Discord voice ingress | `src/Cortex.Contained.Channels.Discord/DiscordVoiceHandler.cs:2663-2664`; `:2234` | Sets conversation to `discord-voice-{tenant}` and channel to `discord-voice`. They are different identities. |
| Bridge forwards ingress | `src/Cortex.Contained.Bridge/Channels/HubMessageDispatcher.cs:140-177` | Records the conversation/channel mapping and forwards the inbound channel to the Agent. |
| Agent accepts message | `src/Cortex.Contained.Agent.Host/Agent/AgentRuntime.cs:282-295` | Copies message channel, conversation, correlation, and voice flag into the queued message. |
| Main turn passes channel | `src/Cortex.Contained.Agent.Host/Agent/AgentRuntime.cs:691`; `:881-891`; `:1191` | Carries `message.ChannelId` into `ToolExecutionContext.ChannelId`, passes it to prompt assembly, and passes the context to tool execution. |
| Context contract | `src/Cortex.Contained.Agent.Host/Tools/IAgentTool.cs:27-44` | Channel is already a required runtime string; it is not absent from the tool invocation context. Tenant and physical endpoint are not part of this context. |
| Shared loop | `src/Cortex.Contained.Agent.Host/Agent/AgentLoop.cs:210-214` | Also constructs tool context from loop configuration; the meaning of that channel depends on the caller. |
| Model-visible channel | `src/Cortex.Contained.Agent.Host/Agent/PromptAssembler.cs:73-88`, `:100-112`, `:179-180` | Channel sentence exists only if catalog lookup produces a non-null prompt label. |
| Voice prompt omission | `src/Cortex.Contained.Agent.Host/Tools/ChannelCatalog.cs:33-42` | Discord voice explicitly has no label; webchat, Discord DM/guild, and local voice have labels. |
| Send target resolution | `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/SendMessageTool.cs:156-188`, `:212-220` | Resolves and validates the explicit argument. Uses current channel only to hint after an omitted channel. Does not inherit it. |
| Schedule creation | `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/ScheduleTaskTool.cs:106-134`, `:212-243` | Receives context but calls `HandleCreate(root)` without it. Stores explicit resolved channel, or null. |
| Timer creation | `src/Cortex.Contained.Agent.Host/Tools/BuiltIn/SessionTimerTool.cs:128-133` | Passes both `context.ConversationId` and `context.ChannelId` to timer service. |
| Channel/conversation conversion | `src/Cortex.Contained.Agent.Host/Tools/ChannelConversationResolver.cs:7-24` | Voice receives a tenant suffix; other values pass through. Not registry validation. |

### 2.4 Hypotheses: verdicts

**(a) Hardcoded/default `webchat-default` fallback: not the executable cause in the investigated proactive path.**

- `SendMessageTool.cs:178-188` explicitly rejects a missing channel.
- Bridge `HubMessageDispatcher.cs:357-374` rejects null or unknown destinations.
- `ScheduleTaskTool.cs:214-242` stores null if the argument is absent; the scheduler substitutes the synthetic `scheduled`, not webchat (`SchedulerService.cs:168-177`).
- There is a misleading literal **`(webchat fallback)`** in task inspection output (`ScheduleTaskTool.cs:301`). It is display text, not implemented routing, and can reinforce a false model assumption.
- The webchat constructor's default ID (`WebChatChannel.cs:17`) names the webchat adapter. It does not choose webchat for unrelated outbound messages.

**(b) Channel not plumbed into tool context / model forced to guess: partially correct, with an important distinction.**

The runtime *does* plumb the channel into tool context. What fails is its use: schedule creation discards it, send requires restatement, and the Discord voice prompt label is missing. This is the strongest source-level explanation for the model choosing the wrong valid destination. It does not prove the internal reason for each historical model choice.

The assertion that the system prompt lists webchat first is not established. `ChannelNameResolver.cs:58` has a generic tool-channel list beginning with `webchat`; `:35-37` and `:52` use it when active-channel information is empty or has no matching first-party alias. With matching active channels, `:44-49` uses catalog order, which puts Discord before webchat. This is a tool-description/error list, not proof of the rendered historical system prompt. The directly demonstrated prompt bug is **missing Discord voice identity**, not a universally webchat-first system prompt.

**(c) Fresh isolated scheduled session loses origin: true as a design weakness, but not where the provided wrong channel was introduced.**

`AgentRuntime.cs:544-555` creates an empty ephemeral session for scheduled tasks. `ScheduledTask.ChannelId` is nullable and describes a target, not a separately recorded origin (`Scheduler/ScheduledTask.cs:58-62`). No origin conversation is captured by the creation block. However, an explicitly stored channel survives: `SchedulerService.cs:168-177` copies it into the fired message, and `:274-276` adds `Target channel: ...` to the instructions. Therefore the provided task would retain and reinforce `webchat-default`. Isolation cannot repair the earlier wrong selection.

**(d) Voice invalid for sends and silently falls back: false for `discord-voice`; there are other voice fallback/acknowledgement issues.**

`discord-voice` is recognized and routable to TTS. Invalid/unavailable tool targets normally produce errors, not webchat fallback. There is an intentional lower-level voice-to-DM ring-timeout path, described below, and its actual outcome is not reported accurately upstream.

### 2.5 Are voice channels accepted targets? What are the errors?

- Both tool schemas expose `channel` as a string, not an enum; schedule's is optional (`ScheduleTaskTool.cs:91-100`), send's is required (`SendMessageTool.cs:88-99`).
- `ChannelCatalog.cs:37-38` recognizes `discord-voice`. `ChannelNameResolver.cs:17-25` delegates both tools to that catalog.
- Unknown names fail: send `:165-168`, schedule `:220-223`.
- Recognized but inactive names fail: send `:170-174`, schedule `:225-229`. **Exception:** an empty active-channel list permits all recognized IDs (`ChannelNameResolver.cs:78-87`); the Bridge may reject them later.
- `voice` resolves to `voice-default`, the **local PC speaker**, not Discord (`ChannelCatalog.cs:41-42`).
- Bridge registers the logical voice alias on the Discord adapter (`src/Cortex.Contained.Bridge/Hosting/ChannelLifecycleManager.cs:110-114`) and advertises it when configured (`:359`).
- Proactive Bridge routing explicitly rewrites the voice conversation to `discord-voice-{tenant}` (`HubMessageDispatcher.cs:383-394`). This compensates for that adapter's primary ID being `discord-dm`.
- `DiscordChannel.cs:469-496` selects the voice handler, rejects missing handlers and empty text, and invokes proactive voice delivery. Voice accepts **text as TTS input**; it does not need a text-channel fallback for a short cue.
- `DiscordVoiceHandler.cs:504-507` checks whether the linked user is in the configured voice channel. If absent, `ProactiveVoiceCoordinator.cs:83-156` queues the text and rings via DM; `:217-250` later sends queued text as DM audio on timeout. `DiscordVoiceHandler.cs:1453-1458` degrades that DM to plain text on synthesis failure.
- Crucially, `DiscordChannel.cs:492-495` treats queued/rang outcomes as success. Even the coordinator's `Spoken` outcome follows a call that merely enqueues into the voice pipeline (`DiscordVoiceHandler.cs:1278-1303`, `:1318-1329`). It is **not a completed-playback receipt**.

### 2.6 Where does a fired `session_timer` intent go?

1. `SessionTimerTool.cs:128-133` captures the originating channel and conversation.
2. `Reminders/SessionTimerService.cs:163-173` stores both in the in-memory entry; `:466-477` copies both into the fired `AgentMessage`. It is not redirected to whichever session most recently became active.
3. `Agent/MessageSourceBehavior.cs:43-63` selects a focused, proactively delivered timer run. `AgentRuntime.cs:617-628` creates the composer; `IntentComposer.cs:64-102` seeds a throwaway session from a bounded tail of the original conversation, retaining its conversation ID.
4. Timer and scheduled `AgentMessage` construction does not set `IsVoice` (`SessionTimerService.cs:466-477`; `SchedulerService.cs:168-177`); it defaults false (`AgentMessage.cs:62`). This loses voice-mode presentation context in addition to the missing voice channel label. It is not itself a rewrite to webchat.
5. `TurnResponseDelivery.cs:234-247` sends ordinary final output only to the scheduled-task operational sink. **Only a successful `send_message` creates user-facing delivery in this path.**
6. Successful proactive results are inserted into the target conversation's history (`AgentRuntime.cs:965-999`). A timer run without delivery to its own conversation adds an internal “nothing was delivered” trace (`:806-829`). This is useful but incomplete: the wording incorrectly says no `send_message` was called even if it failed or sent to another channel.
7. Timer state is changed to fired **before enqueue** (`SessionTimerService.cs:455-482`). A full queue produces only a log and no retry; the entry still looks fired. Fired entries expire from the in-memory listing after two minutes (`:86`, `:302-317`).

**Answer:** timer intents are correctly addressed to the originating conversation/channel in this checkout, but intent delivery to a model is not cue delivery to the user. The model must still choose a target and issue a send; neither successful enqueue nor a final generated sentence proves playback.

### 2.7 Additional reliability findings

- Scheduler execution count/completion advances after queue acceptance, not after model work or outbound delivery (`SchedulerService.cs:179-228`, `:292-297`). There is retry for enqueue failure, not end-to-end cue delivery.
- Proactive dispatcher sends first, then records history (`Tools/ProactiveMessageDispatcher.cs:72-125`). There is no durable outbound ID/journal here to reconcile a timeout, a crash after sending, or a failure persisting history. A retry can duplicate an already accepted send.
- Bridge discards channel-specific receipt detail into a boolean and conversation ID (`HubMessageDispatcher.cs:410-435`), and blanket-labels both internal and external failures “retrying will not help”, even for transient failures.
- Webchat returns success even with no outbound event subscriber (`WebChatChannel.cs:86-95`); streaming/finalization also no-op without handlers (`:110-126`). A SignalR push is not a browser render acknowledgement.
- Local voice returns success for empty text and before background synthesis/playback; errors are logged later (`src/Cortex.Contained.Channels.Voice/VoiceChannel.cs:213-262`).
- Discord proactive queue overflow drops the oldest item after it may have been reported accepted (`ProactiveVoiceCoordinator.cs:98-118`); ring failure clears the queue (`:132-143`); drain failures only log (`:176-193`, `:234-250`). Missing linked DM users log and return successfully from callbacks (`DiscordVoiceHandler.cs:1420-1440`).
- Subagents already have useful durable notification claims, retries and parent channel fields (`Tools/BuiltIn/SubAgentStartTool.cs:196-197`; `Agent/SubagentExecutionCoordinator.cs:600-660`; `AgentRuntime.cs:507-528`). These are **agent-notification** guarantees, not end-user receipts. Nested-parent escalation changes conversation but still uses `task.ParentChannel` (`SubagentExecutionCoordinator.cs:559-589`, `:610-616`), risking a synthetic channel paired with a human conversation.

## 3. Goals, non-goals, and invariants

### Goals

- A rest cue scheduled on Discord voice reaches that voice endpoint, or has an explicit, inspectable undelivered outcome. Merely opening webchat must never redirect it.
- Every outbound operation has a validated destination, provenance, lifecycle policy, stable ID, and truthful status.
- Model prompts and argument choices are not the source of routing truth.
- No invalid route, queue overflow, missing listener, or failed playback vanishes without an agent-visible record.
- Preserve legitimate user-requested cross-channel sends and the ability to suppress stale intent-based cues.

### Non-goals

- Guaranteeing that a person actually hears or reads a message without their acknowledgement.
- Exactly-once external delivery when a provider cannot deduplicate or report the result of an ambiguous attempt.
- Automatically following a user's most recently active device across unrelated conversations.
- Rewriting all adapters or replacing SignalR/SQLite in one release.

### Invariants

1. **Origin is immutable.** Authenticated tenant, recipient, originating endpoint, originating turn, and conversation lineage are captured at ingress. Neither model-authored text nor a synthetic session ID can overwrite them.
2. **Origin and destination are distinct.** Destination normally inherits origin. An agent-chosen explicit override that passes destination validation, or an explicit follow-conversation policy, can change the effective destination without changing provenance. No separate consent gate applies to an override.
3. **Execution ID is not an address.** `scheduled-...` and `subagent-...` identify work, not deliverable channels.
4. **Missing origin is not default origin.** Tools may omit a destination because inheritance is their documented API; if the required inherited binding is absent/invalid, reject with `MissingOrigin`. Do not fill it with webchat, a tenant named `default`, or a guessed session name.
5. **Validation is end-to-end.** Validate at work creation, outbox insertion, and actual send. Configuration changes do not silently retarget already accepted work.
6. **Accepted is not delivered.** A request is accepted only after durable storage; terminal status reflects the strongest actual evidence available.
7. **Each promised cue resolves.** It becomes delivered at its stated acknowledgement level, explicitly suppressed/cancelled/expired, failed, or delivery-unknown. No indefinite unowned “fired” state.
8. **Tenant and audience isolation precede liveness.** Do not broadcast or move to a public room to avoid losing a private message.

## 4. Authoritative identity and routing model

### 4.1 Capture at ingress and at turn start

The Bridge authenticates tenant/user and resolves an inbound endpoint from adapter metadata, not message text. Carry this envelope across the Hub. At the start of processing a conversation turn, freeze a validated snapshot of that envelope into `TurnContext` and pass the same immutable value through the entire turn. Do not re-read a process-global “active channel” during tool execution.

A logical channel such as `discord-voice` is insufficient alone: it needs tenant, recipient/audience, and an endpoint identity (configured Discord guild/room, DM peer, browser conversation group, or local playback device). Keep physical routing details behind an opaque registry `EndpointId`; the model need not see snowflakes or device credentials. Configuration revisions prevent reusing a channel alias for a different room and accidentally sending old work there.

Introduce a stable `ConversationId` independent of channel and an explicit conversation generation. During migration, preserve current conversation strings as legacy session keys and add a stable conversation-lineage ID; do not rewrite all history keys immediately. Distinguish a compaction (same generation) from ending/resetting the interaction (new generation).

### 4.2 Tool behavior

- `send_message(text, attachments?, channel = "origin")`: omission and explicit `"origin"` both inherit the origin-based delivery binding. Omitting `channel` is the documented normal path, never a missing-argument error. An async run inherits its persisted delivery binding, including any previously selected `ExplicitTarget` or follow-conversation policy; it never substitutes its synthetic execution channel for the captured origin. If the inherited context is missing or invalid, fail for that context defect, not for argument omission.
- `schedule_task(...)`: captures origin and delivery binding automatically and persists them with the task; removes “include the channel in the message text”. Background-only jobs must select `NotifyPolicy.None` explicitly, rather than relying on a null channel.
- `session_timer(...)`: captures the same envelope plus a live-session lease, freshness deadline, and binding mode. It continues to store an intent, not necessarily frozen text.
- Subagents inherit immutable origin and permitted delivery binding, while retaining their own execution/session IDs. They still cannot directly message the user by default. Their result targets a parent-agent inbox; the parent may create an outbound request.
- Cross-channel sends are freely available to the agent as a judgement call: specifying an explicit `channel` selects `ExplicitTarget`, with no proof of user consent, confirmation flag, or authorization reference required. Resolve the target through the registry and apply the same tenant, audience, endpoint, outbound-capability, and reachability validation as for inherited delivery. An invalid or unauthorized destination still fails loudly. Preserve the immutable origin and record the explicit choice and actual delivery target in receipts; do not confuse destination access control with a consent gate on choosing another valid channel.
- Retain the canonical channel ID in diagnostic prompt context, including Discord voice, but treat this as explanatory rather than an enforcement mechanism. Supply a non-optional runtime context section outside customizable personality templates. Async context says “originated on X; delivery bound to Y”, not falsely “the user is currently on Y”. Modality comes from binding/capabilities and current reachability, not an omitted `IsVoice` flag.

An explicit same-channel legacy argument can normalize to inheritance. A legacy cross-channel argument selects `ExplicitTarget` and succeeds if the destination passes registry validation, without any consent or authorization reference. Do not silently ignore it in favor of origin. Reject invalid or unauthorized destinations with the relevant structured validation error; there is no separate override-permission error. Channel names in task prose remain content, never routing authority.

### 4.3 C# contract sketches

Illustrative proposed contracts, not production code. Constructors/factories and deserialization validation must reject blank IDs; `required` alone does not validate runtime input.

The former override-authorization field is removed from `DeliveryBinding`, rather than retained as nullable metadata, so it cannot become an accidental admission precondition. `Mode = ExplicitTarget`, immutable origin, selected target, and delivery events provide the audit trail. An optional agent rationale may be logged as non-authoritative metadata; its presence or content must never gate an otherwise valid override.

```csharp
public sealed record ChannelAddress(
    string TenantId,
    string ChannelId,       // canonical logical ID; never a session ID
    string EndpointId,      // opaque physical destination in the registry
    string AudienceId);     // linked user or explicitly authorized room audience

public sealed record TurnOrigin(
    string TurnId,
    string ConversationId, // stable lineage, not a scheduled/subagent execution ID
    long ConversationGeneration,
    ChannelAddress Channel,
    long EndpointRevision,
    DateTimeOffset CapturedAtUtc);

public enum BindingMode { PinnedOrigin, FollowConversation, ExplicitTarget }
public enum NotifyPolicy { Required, Optional, None }
public enum AbsencePolicy { WaitUntilDeadline, Fail, UseApprovedAlternate }

public sealed record DeliveryBinding(
    BindingMode Mode,
    ChannelAddress InitialTarget,
    long EndpointRevision,
    string? AlternatePolicyId);

public sealed record ExecutionContext(
    string ExecutionId,
    string? ParentExecutionId,
    TurnOrigin Origin,
    DeliveryBinding Delivery,
    string CorrelationId);

public sealed record CuePolicy(
    DateTimeOffset DueAtUtc,
    DateTimeOffset ExpiresAtUtc,
    NotifyPolicy Notify,
    AbsencePolicy OnAbsence,
    string? LiveSessionLeaseId);

public sealed record DeferredWorkRecord(
    string WorkId,
    string OccurrenceId,    // unique per scheduled occurrence
    ExecutionContext Context,
    CuePolicy Policy,
    string Intent);

public enum DeliveryState
{
    Pending, WaitingForPresence, InFlight, AcceptedByAdapter,
    RetryScheduled, Succeeded, Failed, DeliveryUnknown,
    Expired, Cancelled, Interrupted
}

public enum AckLevel
{
    None, DurableAcceptance, TransportAccepted,
    ClientRendered, PlaybackCompleted, UserAcknowledged
}

public sealed record DeliveryFailure(
    string Code, string SafeMessage, bool IsTransient,
    TimeSpan? RetryAfter = null);

public sealed record OutboundRequest(
    Guid DeliveryId,
    string IdempotencyKey,  // tenant/work occurrence/output slot, not content hash alone
    ExecutionContext Context,
    CuePolicy Policy,
    string Text,
    AckLevel RequiredAcknowledgement);

public sealed record DeliveryReceipt(
    Guid DeliveryId,
    long Sequence,
    DeliveryState State,
    AckLevel AchievedAcknowledgement,
    ChannelAddress? ActualTarget, // null until a dispatch route has been resolved
    string? AppliedAlternatePolicyId,
    string? ProviderMessageId,
    DateTimeOffset AtUtc,
    DeliveryFailure? Failure);

public interface IOutboundDeliveryService
{
    // Return after durable commit, not a claim of playback.
    Task<DeliveryReceipt> EnqueueAsync(OutboundRequest request, CancellationToken ct);
    Task<DeliveryReceipt?> GetAsync(Guid deliveryId, CancellationToken ct);
    Task CancelAsync(Guid deliveryId, string reason, CancellationToken ct);
}
```

Use `Cortex.Contained.Contracts` for the cross-process records; keep authority checks and construction in Bridge/Agent services. Production payloads also need attachment references, content integrity metadata, retry budgets, and per-attempt route-resolution snapshots. Use a name distinct from `System.Threading.ExecutionContext` when implementing (for example `AgentExecutionContext`). Internal maintenance jobs without a human origin should use a separate service-context type and may not notify unless configured with an authorized explicit delivery binding.

## 5. Validated registry and explicit voice policy

Replace the split static alias catalog / active string list / adapter lookup with a Bridge-owned, tenant-scoped registry. The Agent gets a versioned, authenticated snapshot for schemas and early validation; Bridge remains authoritative at dispatch. Preserve aliases as input conveniences, not delivery identities.

```csharp
[Flags]
public enum OutboundCapability
{
    None = 0, Text = 1, SpeechFromText = 2, Images = 4,
    Files = 8, StreamingText = 16
}

public enum Reachability { Unknown, Ready, Offline, RecipientAbsent, Disabled }

public sealed record RegisteredChannel(
    ChannelAddress Address,
    long Revision,
    bool CanReceiveInbound,
    OutboundCapability Outbound,
    Reachability Reachability,
    IReadOnlySet<AckLevel> SupportedAcknowledgements,
    string? ApprovedPairedTextPolicyId);
```

Registry validation applies equally to inherited origins and agent-chosen explicit targets. It checks tenant/audience authority, endpoint existence/revision, outbound capability for the entire payload, reachability under the selected wait/failure policy, requested acknowledgement support, and freshness. These are destination access and delivery checks, not proof-of-consent checks: an explicit override to a valid target needs no authorization reference. Configured automatic alternate-route policies remain subject to their declared conditions and destination access controls. Read-only channels have `OutboundCapability.None`. A syntactically valid `plugin:...` ID is not proof that a connector is attached or allowed to send. The existing `ChannelCapabilities` has feature flags but no explicit outbound permission or acknowledgement contract (`src/Cortex.Contained.Contracts/Channels/ChannelTypes.cs:26-45`); add these rather than assuming inbound implies outbound.

| Channel kind | Proposed outbound meaning |
|---|---|
| `discord-voice` | Deliverable: text is synthesized and played into the tenant's registered Discord voice room. Presence and TTS/playback health are required for live cues. Not a text-rendering surface. |
| `voice-default` | Deliverable: text-to-speech to an explicit local device. Not an alias for Discord voice. |
| `discord-dm` | Text/files to the authorized linked peer, with provider message ID; never a globally cached “last DM user”. |
| `discord-guild` | Text/files only if a particular tenant-authorized room endpoint exists. A catalog name alone is insufficient; current legacy routing can return a config error (`DiscordChannel.cs:498-503`). |
| `webchat-default` | Text/media to the tenant/conversation group, with optional browser render acknowledgement. Zero connected clients is not evidence of live delivery. |
| Plugin channels | Use negotiated capabilities and authenticated attachment; declare read-only explicitly when appropriate. |
| `scheduled`, `scheduled-*`, `subagent-*`, operational history streams | Execution/observation identities, not user delivery targets. Show as non-deliverable/read-only in inspection APIs if exposed at all. |

### Explicit paired-text rule

**Default: inherit origin, with no automatic cross-channel fallback.** Omitting the tool's `channel` argument is normal and correct: resolve the inherited origin-based binding. A missing inherited context or an explicitly blank, unknown, unauthorized, or otherwise invalid destination fails loudly; it never triggers an alternate route. The agent may instead freely choose a valid explicit target without a consent gate.

For a *valid* voice target that cannot represent the content (for example, images or a long code listing), an operator/user may configure an explicit paired-text policy containing the target endpoint, allowed audience, supported content transformations, conditions, consent provenance, and revision. Automatic paired-text fallback requires the request to select a configured policy whose conditions and destination access controls permit it; otherwise the tool returns `UnsupportedPayload` with the available option. This governs automatic fallback, not the agent's freedom to make a separate explicit send to a valid text target without consent proof or an authorization reference. No rule chooses “the first active text channel”. The current prompt-only instruction about paired text (`SystemPromptDefaults.cs:151-155`) is not such a registry policy.

Short gym cues are speech and require no paired text route. They use `WaitUntilDeadline`, **no ring and no DM fallback by default**. General notifications may explicitly opt into a named `RingThenDm` policy. If used, receipt history must show each attempt and the actual DM target/media, not claim delivery in Discord voice. Synthesis failure may fall back from DM audio to DM text only when that policy authorizes it. Expiry, invalid routes, authorization failure, and user cancellation must never activate fallback.

## 6. Current fallback/default inventory and replacements

This inventory covers routing/context defaults and hidden delivery-loss paths found in the inspected Agent/Bridge/channel flow, including adjacent transfer and coding-result routing. It is not a claim that unrelated configuration defaults elsewhere in the repository have been audited.

For compact references in this table: **H** = `src/Cortex.Contained.Agent.Host/`, **B** = `src/Cortex.Contained.Bridge/`, **D** = `src/Cortex.Contained.Channels.Discord/`, **W** = `src/Cortex.Contained.Channels.WebChat/`, **V** = `src/Cortex.Contained.Channels.Voice/`.

| Current path and reference | Replacement |
|---|---|
| `W/WebChatChannel.cs:17`; `B/Hosting/ChannelLifecycleManager.cs:338`: default webchat adapter identity / advertised ID | Keep as explicit configuration if desired, never as outbound inheritance. Registry keys include tenant and endpoint. This is not the incident's delivery fallback. |
| `H/Tools/ChannelCatalog.cs:37-38`; `H/Agent/PromptAssembler.cs:84-88`: missing voice label produces no channel context | Always render trusted origin/delivery context; missing registered metadata is a diagnostic error, not omitted identity. |
| `H/Tools/ChannelNameResolver.cs:35-58`: empty/no-matching active list displays generic names, starting with webchat | Separate `RegistryUnavailable` from a known empty registry. Advertise only authorized registered capabilities, including dynamic plugins. |
| `H/Tools/ChannelNameResolver.cs:78-87`: empty active list permits any recognized target | Fail closed for unresolved registry; known offline endpoints can be queued only under an explicit bounded wait policy. |
| `H/Tools/BuiltIn/ScheduleTaskTool.cs:212-243`; `:301`; `H/Scheduler/SchedulerService.cs:171`: null channel, `(webchat fallback)` display, synthetic `scheduled` context | Capture origin/binding. Require an explicit no-notification policy for internal work. Legacy unbound notifications become `NeedsRoutingReview`, never inferred webchat. |
| `H/Tools/BuiltIn/SendMessageTool.cs:178-188`: omission errors although context is available | Document omitted `channel` (or `"origin"`) as `inherit origin`, the normal correct path, never a missing-argument error. Resolve the inherited binding; a missing/invalid binding is a context failure. Explicit overrides are freely available subject to destination validation, not a consent gate. No implicit global default. |
| `H/Tools/ChannelConversationResolver.cs:7-24`; `H/Agent/AgentRuntime.cs:974-979`; `H/Tools/BuiltIn/TransferSessionTool.cs:290-296`: nonvoice session passthrough, tenant parsed from voice string or defaulted to `default` | Carry authenticated tenant and stable conversation binding explicitly. Do not parse session keys to recover authority. Legacy single-tenant adapters may use an explicitly configured tenant, not an unrecorded fallback. |
| `H/Tools/BuiltIn/TransferSessionTool.cs:197-221`: unknown target kept raw; empty active list bypasses rejection | Registry validation before transfer mutation; unknown destination always errors. |
| `B/Channels/HubMessageDispatcher.cs:218-227`, `:260-270`, `:317-324`: missing map falls back to conversation ID; chunk/error lookup failures return silently | Carry a delivery binding on response events. Missing mapping/binding yields a persisted route failure and agent-visible status. Never interpret conversation as channel. |
| `B/Channels/HubMessageDispatcher.cs:378-418`: new GUID for absent conversation, voice special case, logical target collapsed to primary adapter ID in outgoing message/map/logs | Separate execution, conversation, requested logical target, adapter, and actual physical endpoint. Return all route evidence; do not manufacture a conversation as a routing substitute. |
| `B/Channels/HubMessageDispatcher.cs:454-474`: unknown user/channel mapping uses default tenant | Explicit configured inbound binding only. Otherwise reject/quarantine before a user turn; never silently select another tenant. |
| `D/DiscordChannel.cs:505-537`, `:546`: DM cached/default recipient resolver; numeric conversation-ID backward compatibility; channel-or-conversation effective key | Resolve an explicit tenant/recipient endpoint. Restrict legacy translation to a validated boundary adapter; no arbitrary snowflake parsing in the send path. |
| `D/DiscordChannel.cs:557-569`: DM voice reply silently degrades to text on TTS failure | Same-recipient modality fallback must be declared and recorded in receipt policy. |
| `D/ProactiveVoiceCoordinator.cs:83-156`, `:217-250`; `D/DiscordVoiceHandler.cs:1453-1458`: ring, timeout to DM audio, synthesis failure to DM text | Named opt-in alternate-route policy with per-message status, actual target, deadline, and authorization. Disabled for live gym cues. |
| `D/ProactiveVoiceCoordinator.cs:98-118`, `:132-143`, `:176-193`, `:234-250`: evict/clear/drain-and-log loses accepted messages | Durable delivery IDs; reject new admission on overload or terminally fail explicitly affected items. Never discard accepted work without receipts. |
| `D/DiscordVoiceHandler.cs:1420-1440`: missing DM user logs and returns; `:1332-1337`: last-message replay after reconnect | Missing recipient is a typed failure. Replace ad-hoc replay with delivery-ID-aware bounded retry so watchdog and outbox cannot both replay it. |
| `D/DiscordVoiceHandler.cs:474-480`: failed REST presence check uses cache | Preserve as a clearly timestamped observation, not routing authority. Stale/unknown presence invokes explicit absence policy; never means “user definitely heard it”. |
| `W/WebChatChannel.cs:86-95`, `:110-126`; `V/VoiceChannel.cs:213-262`; `D/DiscordChannel.cs:492-495` | Distinguish admission from completion. Missing subscriber/empty unsupported payload/late TTS failure becomes a typed status, not success. |
| `H/Reminders/SessionTimerService.cs:455-492`: mark fired before best-effort enqueue and only log failure | Durable occurrence claim + awaited/bounded enqueue; queue failure remains retryable until deadline, then explicit failure/expiry. |
| `H/Agent/SubagentRunner.cs:172`, `:242-248`; `H/Agent/SubagentCallbacks.cs:239-240`: synthetic execution ID used as channel | Preserve execution isolation but pass inherited origin separately. Internal inbox address is not an outbound destination. |
| `H/Agent/SubagentExecutionCoordinator.cs:559-589`, `:610-616`: ancestor retargeting can leave old/synthetic parent channel; unresolved chain returns original parent | Resolve complete parent inbox envelope atomically. If no authorized live ancestor exists, store a pending failure/result for the owning conversation lineage; never invent a route. |
| `B/Coding/CodingHubBinder.cs:159-179`: unresolved owning tenant broadcasts coding events to all wired clients | Quarantine and alert on unresolved ownership; target only the authenticated owner. This adjacent isolation risk is not evidence for the gym incident. |

Existing good behavior to retain: explicit unknown/inactive send errors; missing proactive Bridge handler returns an error (`B/Hub/HubClient.cs:824-827`); proactive Bridge rejects missing/unknown channels. Make these failures structured, logged with provenance, and durable rather than weakening them into fallback.

## 7. Delivery guarantees, outbox, and observability

### 7.1 Adopt a durable outbox and receipt journal

An outbox is warranted: scheduling, model execution, Bridge transport, TTS, and playback are separate failure domains. More prompt instructions cannot make a lost queue item or post-send crash observable.

**Agent-owned outbox:** persist `OutboundRequest`, origin, selected binding, payload reference/hash, due/expiry, required ack, status, and idempotency key before reporting acceptance to a tool. In the same local transaction, resolve the work's notification obligation to that delivery ID. Separate scheduled occurrence status from delivery status.

**Bridge-owned inbox/attempt journal:** accept requests with the same delivery ID idempotently, validate tenant/route, and durably retain acceptance and later receipts. Bridge/adapter owns retries after this handoff; Agent only retries the handoff or polls/reconciles status. This avoids two layers independently sending the same cue. Receipt events have monotonic sequence numbers and can be fetched after reconnect; Agent applies them idempotently.

Use SQLite initially, fitting the existing persistence architecture. No distributed transaction is needed: Agent retries its durable handoff until the Bridge records it; Bridge deduplicates by `(tenantId, deliveryId)`. Retain tombstones beyond the maximum request lifetime and retry window. A repeated key with different payload/binding is an error, not a second send.

A stable `(work occurrence, output slot)` idempotency key prevents a replayed model run from creating the same promised cue twice under fresh tool-call IDs. Distinct deliberate messages use distinct slots. Do not deduplicate solely by text: identical wording can legitimately occur in successive workout sets.

Suggested stores:

- `work_occurrences`: origin/binding/policy, occurrence lease, execution outcome, notification obligation and delivery IDs;
- `outbound_deliveries`: immutable request plus current status, next attempt/deadline, resolution snapshot;
- `delivery_attempts`: exact endpoint, policy/revision, attempt number, provider IDs, timings, ack level, classified error;
- `delivery_events`: append-only status transitions, including reroutes/suppression/expiry;
- `conversation_notices`: durable per-origin/lineage notices and consumption cursors.

Keep large payloads in bounded encrypted/protected storage with references; avoid placing message bodies, invites, or secrets in diagnostic logs. Apply tenant isolation, retention limits, and indexes on due/retry time and origin conversation. Store status/history projection updates atomically where possible; external send and local storage cannot be one atomic operation.

### 7.2 Acknowledgements and honest guarantees

| Evidence | Meaning |
|---|---|
| Durable acceptance | Request safely stored; no assertion about delivery. Tool says “queued”, with ID and destination. |
| Adapter acceptance | Accepted into an adapter/pipeline; still not playback or browser display. |
| Transport accepted | Discord returned a message ID / connector acknowledged its frame. Not proof of reading. |
| Client rendered | An authenticated webchat client reported rendering this delivery ID. Not proof of reading. |
| Playback completed | Voice pipeline reports final audio frame completion for this delivery ID and intended endpoint, with presence observations. Not proof of human hearing. |
| User acknowledged | Explicit user acknowledgement associated with the delivery. Optional; not required for ordinary cues. |

For a live gym cue, success requires `PlaybackCompleted`, not merely `AcceptedByAdapter`. Persist interruption/partial-playback evidence. For Discord text, use `TransportAccepted` as the honest terminal guarantee. Webchat can distinguish stored-for-later from rendered-live. Reject an unsupported required acknowledgement instead of silently downgrading it.

Voice pipeline markers must carry delivery IDs through synthesis, playback, end markers, interruption epochs, and reconnects. An interruption is not full delivery. Queue admission, synthesis failure, zero audio, device failure, and endpoint disposal all need receipts. Do not wait in a tool call for an arbitrarily long voice ring; return acceptance and update asynchronously.

### 7.3 Retry and failure classification

- Retry known transient pre-delivery failures (temporary disconnect, rate limit with retry-after, transient network/TTS outage) using bounded exponential backoff and jitter. A live-cue profile can start at roughly 0.5s, 1s, 2s, 4s, subject to its deadline and adapter limits.
- Fail permanent errors (unknown/missing channel, unauthorized audience, unsupported content, deleted endpoint, invalid config) immediately. Surface error to the invoking agent and log/record it even if the outbox itself cannot be written.
- Queue pressure is explicit backpressure/rejection; it is not permission to evict an accepted item invisibly.
- Revalidate expiry/presence/session generation before each attempt and immediately before playback. Do not begin stale audio just because it spent time in a queue.
- If the provider may already have accepted a send when the response is lost, query/reconcile by ID where supported. Otherwise use `DeliveryUnknown`; do not assert failure or retry blindly. For short spoken gym cues, prefer an honest unknown outcome over duplicate stale “go” cues. Other notification classes may explicitly opt into at-least-once external retries.
- No exactly-once promise: deduplication provides effectively-once handoff and per-adapter admission where implemented; crashes between provider effect and journaling remain ambiguous without provider support.
- Apply per-endpoint ordering and bounded priority so a fresh live cue is not stuck behind a long notification. Do not interrupt the user's speech just to meet a timer; expire or defer according to the cue's policy.

### 7.4 Agent-visible outcome and operational telemetry

A failure must survive the throwaway scheduler/composer session. Record a structured notice against the immutable origin and its conversation lineage. On the next applicable user turn, inject unconsumed status notices before generation; expose `delivery_status`/`delivery_list` tools and show status alongside timer/task inspection. A transfer may move where the notice is surfaced, but never its provenance or audience authority. If that conversation ended, retain it in the user's authorized activity/history view and summarize when relevant, without resurrecting the ended session.

Example notice:

> Timer 7e... became due at 14:03:10. Delivery 91... was bound to Discord voice / workout conversation. Playback was not confirmed; expired at 14:03:30 because the user was absent. No webchat or DM message was sent. Do not announce this as a fresh rest-over cue.

Record actual wrong-target delivery too: “sent to webchat, not voice” is different from “no send called”. Replace the current blanket timer trace accordingly. Record successful receipts in both the origin activity log and actual target history without inventing a user-visible assistant utterance in the wrong conversation.

Useful events: `work.created`, `work.due`, `work.claimed`, `delivery.accepted`, `route.resolved`, `delivery.attempt`, `playback.started`, `playback.completed`, `delivery.retry_scheduled`, `delivery.failed`, `delivery.expired`, `delivery.unknown`, `notice.consumed`. Include tenant, origin turn/conversation/generation, execution/work/occurrence/delivery IDs, requested/resolved/actual target, policy and endpoint revision, attempt, ack level, deadline, and safe failure code. Do not collapse `discord-voice` to adapter ID `discord-dm` in route logs.

Metrics: due-to-first-audio latency, cue completion before deadline, expired/unknown/failed count by reason, unexplained origin/target mismatch without an explicit override, transfer, or selected alternate policy (must be zero), explicit override rate and actual destinations for review, queued-without-terminal-receipt age, queue rejection, and unconsumed failure notices. Alert on broken invariants and stuck deliveries, not merely on exceptions.

## 8. Lifecycle of timers, scheduled work, and subagents

### 8.1 Separate work from delivery

Suggested work lifecycle:

`Pending -> Due -> Claimed -> Composing -> AwaitingDelivery -> Completed`

Alternative terminal states: `Suppressed(reason)`, `Cancelled(reason)`, `Expired`, `ExecutionFailed`, `DeliveryFailed`, `DeliveryUnknown`. “Fired” means due/triggered only. A scheduled occurrence is not completed merely because it was enqueued. Recurrence advancement creates a distinct next occurrence without losing the previous occurrence's delivery status.

Retain intent-based timers: the composer can determine that a cue is obsolete. Require a structured resolution: issue an outbound request, or explicitly suppress with a reason, or complete internal work if `NotifyPolicy.None`. A required-notification run that exits with only final text is `MissingOutboundAction`, visible to the agent; it must not silently count as success. Allow bounded recovery within deadline, but do not automatically replay arbitrary side-effecting tools from the whole run. Retry composition before side effects or use a recovery run with the previous action ledger. Delivery retry only resends the committed payload.

For callers needing exact, deterministic cues, a separate explicit fixed-message timer mode can bypass model composition. It should not silently replace today's intent semantics; fixed text also expires and obeys the same presence/lifecycle policy.

### 8.2 Recommended policy profiles

- **Live rest cue:** `NotifyPolicy.Required`, `FollowConversation` only within the same confirmed workout conversation lineage, live-session lease required, no alternate route/ring, wait briefly for presence, expire shortly after due. Initial proposed grace: 20 seconds after due, configurable and subject to product review; it is not 20 seconds after each retry.
- **Ordinary scheduled reminder:** pinned originating/explicit endpoint by default, persists across restarts, configurable longer deadline and optional approved alternate. It does not silently follow an unrelated active chat. Each recurrence has its own expiry.
- **Background subagent result:** inherits origin, reports to parent inbox, follows explicit parent transfer policy; no direct user-send privilege. Result retention may outlive a live session, but notification must remain authorized and correctly addressed.

### 8.3 Event policy

| Event | Expected behavior |
|---|---|
| User opens another channel but does not transfer conversation | No retargeting. Two simultaneous conversations keep independent bindings. |
| Explicit conversation transfer | Atomically update the lineage binding/revision. `FollowConversation` work resolves to the new authorized endpoint; pinned work remains pinned. Origin never changes. Surface pending work in the transfer result. |
| Transfer races with firing | Serialize the binding revision decision with dispatch admission; persist the chosen revision. If delivery has started, do not also send on the new channel. A later transfer cannot retract audio already played. |
| Conversation/workout ends or resets | Revoke its lease/generation and cancel pending live cues. Mark accepted but not-started cue deliveries cancelled; attempt adapter cancellation. Retain audit/failure records. General reminders survive only under their separate policy. |
| History compaction or idle summarization | Preserve lineage/generation and binding. Compaction must not reset delivery identity or erase pending status. |
| User leaves voice temporarily | Do not ring/DM a live rest cue. Wait until its fixed deadline, then expire with a recorded notice. Presence unknown is not equivalent to present. |
| User returns after deadline | Do not play a stale cue. On the next turn explain its expired/undelivered status if relevant. |
| Channel removed, endpoint reconfigured, credentials revoked | Revalidate; fail or hold for explicit routing review. Do not resolve the old alias to a newly assigned room/recipient. |
| Playback interrupted by user | Record partial/interrupted status; do not automatically repeat the cue over the user. Agent can decide on the next turn whether a new cue is useful. |
| Process restart | Scheduled records/outbox recover by stable ID. Persist live timer origin and status too; default live-session lease is invalid after restart unless explicitly revalidated, so do not replay old gym cues. Mark them interrupted/cancelled with a notice. |
| Full internal queue or unavailable model | Keep durable occurrence pending/retryable within deadline; failure/expiry is explicit. Never report user delivery. |
| Parent subagent stops | Resolve a live authorized ancestor inbox or retain the result for origin. Carry the whole envelope, not a new conversation plus the old synthetic channel. Cycles/missing ancestry produce a diagnostic failure. |

Persisting live timers does **not** mean blindly resurrecting them. The design preserves their origin and truthful outcome across restart while keeping their useful lifetime session-scoped. This is a deliberate change from the current “entirely disappears on restart” contract and needs updated tool documentation.

## 9. Incremental migration and affected call sites

Implement only after design approval. Use additive contracts, protocol-version negotiation, feature flags per tenant, and a single active delivery path per request. Shadow routing compares decisions but must never shadow-send.

### Phase 0 — Characterization and incident trace

- Add regression fixtures for a voice-origin rest cue and the supplied wrong-channel task shape. Capture current behavior without blessing it as correct.
- Add route/ack telemetry and version fingerprints. Correct misleading `(webchat fallback)` inspection language and the missing voice label as part of the context work, not as the final solution.
- Test suites to extend: `ChannelCatalogTests`, `ChannelNameResolverTests`, `SendMessageToolTests`, `SchedulerToolTests`, `SchedulerServiceTests`, `SessionTimerServiceTests`, `TimerComposerRuntimeTests`, `ProactiveDeliveryTraceTests`, `ProactiveVoiceCoordinatorTests`.

**Buys:** reproducibility and a measurable baseline before semantic changes.

### Phase 1 — Add authoritative envelopes and registry snapshots

- Add contracts in `Cortex.Contained.Contracts` and additive fields to Hub inbound/outbound messages and Agent work records.
- Change `DiscordVoiceHandler` ingress, `HubMessageDispatcher.OnChannelMessageReceivedAsync`, `AgentRuntime` turn construction, `AgentLoop`, `ToolExecutionContext`, and `PromptAssembler` to preserve the same envelope.
- Extend `ChannelLifecycleManager`, `ChannelManager`, `ActiveChannelStore`, `ChannelCatalog`/`ChannelNameResolver` to registry-backed validation. Keep legacy aliases behind one compatibility adapter.
- Propagate origin through `SubAgentStartTool`, `SubagentTask`/`SubagentSessionStore`, `SubagentRunner`, `SubagentCallbacks`, `SubagentExecutionCoordinator`, and `SubagentMessageRouter`. Preserve existing subagent tool restrictions.
- Shadow-compare structured resolution against old mappings; surface unknown registry/tenant states rather than approving guessed destinations.

**Buys:** authoritative identity without first changing delivery scheduling. Cross-tenant/default routing defects become visible.

### Phase 2 — Make tools inherit binding and persist async origin

- Change `SendMessageTool`, `ScheduleTaskTool.HandleCreate`, `ScheduledTask`, `SqliteTaskStore`, `SchedulerService`, `SessionTimerTool`, `SessionTimerService`, and `IntentComposer` to use/persist origin and binding.
- Add live timer occurrence persistence and lease/expiry fields. Stop depending on free-text channel instructions.
- Update schemas/descriptions and protocol consumers; refresh dynamic tool definitions (`ToolRegistry`) so stale schemas do not keep requiring channel restatement.
- Normalize same-target legacy arguments. Accept agent-chosen explicit overrides to valid destinations without consent proof or an authorization reference; apply full destination validation and record the choice. Do not silently replace an explicit valid target with origin.
- Remove the synthetic-channel routing default for notifying work. Internal maintenance schedules, including `Memory/NightlyTaskScheduler.cs`, must explicitly select no notification or an authorized destination.

**Buys:** eliminates the demonstrated “voice context -> guessed webchat task” class without relying on model compliance.

### Phase 3 — Durable outbound acceptance and truthful statuses

- Introduce Agent outbox and Bridge inbox/receipt journal; extend `IProactiveMessageDispatcher`/`ProactiveMessageDispatcher`, Hub proactive contracts/handlers, and `SendResult` replacement/extension.
- Convert legacy adapter success into its real conservative level (at most adapter/transport acceptance), not playback completed. Integrate `TurnResponseDelivery` final messages too; streamed previews remain non-durable previews, while final delivery has a stable ID.
- Change scheduled completion and timer listing to report work and delivery separately. Add durable conversation notices and status tools; replace misleading `RecordComposedOutcome` text.
- Keep at most one transport-send owner under the feature flag. Never invoke old immediate send and outbox send for the same request.

**Buys:** no invisible post-enqueue failures; safe handoff retry and crash reconciliation even before every adapter has a strong acknowledgement.

### Phase 4 — Adapter completion, retries, and alternate policies

- Extend `DiscordChannel`, `DiscordVoiceHandler`, `ProactiveVoiceCoordinator`, and the voice-out pipeline to carry delivery IDs, expiry, playback completion/interruption/failure receipts. Replace last-text watchdog replay with ID-based retry ownership.
- Extend `VoiceChannel`, `WebChatChannel`/`WebChatHub` and browser client acknowledgements, plus `PluginChannel`/`ConnectorSession` negotiated receipts. Reject unsupported receipt requirements until implemented.
- Remove hidden evictions and log-only losses; add bounded retry classification, presence-aware queueing, explicit paired-text/ring policies, and per-recipient endpoint routing.
- Replace blanket “retrying will not help” messages and primary-adapter-ID logs with structured errors and actual-target evidence.

**Buys:** trustworthy live-cue completion and controlled retries instead of optimistic “sent”.

### Phase 5 — Conversation lifecycle and retirement of compatibility routes

- Add stable lineage bindings, session generation/leases, and atomic transfer admission. Update `TransferSessionTool`, runtime reset/end paths, scheduler/timer cancellation, subagent parent repointing, and delivery-notice routing.
- Replace conversation-string and default-tenant fallbacks, numeric-snowflake routing, and coding-event unknown-tenant broadcast. Keep any necessary legacy ID translation explicit and validated at a single boundary.
- Audit remaining direct adapter sends (transfer greetings, error replies, slash-command/enrollment prompts, coding notifications). Route user-facing output through the same admission/receipt model, or document narrowly scoped deterministic adapter-internal events with equivalent receipts. Transfer's existing dispatcher greeting is one known call site (`TransferSessionTool.cs:355`).

**Buys:** predictable behavior when the conversation moves/ends and removal of competing definitions of “current channel”.

### Data migration and rollout safety

1. Add nullable versioned origin/binding columns first; new writers always populate them. Back up stores and validate row counts/statuses before enabling new readers.
2. Existing scheduled `ChannelId` is a **legacy destination**, not proof of origin. Preserve it, mark provenance `LegacyExplicitTarget`, validate endpoint/audience, and review before enabling strict routing. Do not infer origin from task prose or the currently open UI.
3. Rows with null/invalid destinations become `NeedsRoutingReview` for user-notifying jobs. Known internal jobs are explicitly migrated to `NotifyPolicy.None`. Do not silently assign webchat.
4. An existing valid-but-wrong `webchat-default` task like the reported one cannot be auto-corrected safely from that row alone. Review it against authenticated creation evidence/user intent. Do not replay historical gym cues during migration.
5. Add new serialized states compatibly; old binaries may not understand new enum strings or fields. Require capability negotiation and disable new durable dispatch for incompatible peers. Rollback must first stop admissions/drain or park new-format work; an old binary must not double-send it.
6. Canary one tenant with synthetic recipients, then opt-in live voice, then general schedules. Gate rollout on zero unauthorized destination admissions and zero unexplained origin-target mismatches, bounded receipt age, correct failure notices, and cue latency. A validated, recorded explicit override is an expected route choice, not a mismatch violation. Revert the feature flag without deleting the outbox or losing receipts.

## 10. Acceptance tests

Use unit tests with `TimeProvider`, fake transport/voice adapters, SQLite integration tests, and cross-process contract tests. No live messages are needed for most cases.

1. Voice ingress -> `send_message` without channel -> registered Discord voice endpoint; never webchat. Same test for timer fire and persisted scheduled task after restart.
2. Model includes `webchat-default` in task prose: ignored as routing authority. Omitted `channel` and explicit `"origin"` use the captured origin by default. An agent-supplied explicit override to a valid target succeeds without proof of user consent or any authorization reference, retains the immutable voice origin, and records the selected/actual target. An explicit override to an invalid or unauthorized destination fails loudly with the relevant validation error and no send.
3. Prompt always exposes origin and binding for voice, including custom templates and async runs, while removing/reordering the prompt channel list cannot affect routing.
4. Missing inherited origin/binding, explicitly blank/unknown/read-only/unauthorized destination, synthetic execution ID, empty/unavailable registry, stale endpoint revision, and cross-tenant endpoint all fail with agent-visible/logged structured errors. No adapter call occurs. Omitting `channel` with a valid inherited binding is not an error and must succeed.
5. Voice text synthesizes; image-only voice fails or uses an explicitly authorized paired-text route. No configured pairing means no fallback. Receipt records the actual route.
6. Queue full, disconnect, TTS failure, missing DM peer, no webchat listener, playback device failure, and connector detach produce correct non-success states; accepted items never disappear on overflow.
7. Retry transient failures only within deadline; rate-limit delay honored. No retry for permanent errors. A lost response after possible send produces reconciled status or `DeliveryUnknown`, not an uncontrolled duplicate.
8. Crash before Agent handoff, after Bridge acceptance, after provider effect, before history save, and during receipt delivery: reconstruct by stable ID, avoid duplicate admission, preserve ambiguity where necessary.
9. Timer composer exits with text only: required notification fails visibly. Explicit suppression records reason. Sending to an agent-chosen valid different channel is reported with its explicit override and actual-target receipt, not “no send called”.
10. Confirmed transfer before due follows policy; unrelated activity never retargets. Race transfer/cancel/end with playback admission and assert one chosen revision, no double send, no stale cue after expiry.
11. User absent/returns before grace, returns after expiry, presence unknown, barge-in, and bot reconnect: assert correct waits, expiry, partial/unknown receipts, and no unsolicited ring/DM for live cues.
12. Nested subagent parent terminates/transfers: result reaches an authorized ancestor with a complete envelope, not a human conversation plus synthetic channel. Child delivery to an agent inbox is never labeled user delivery.
13. Concurrent tenants with identical logical IDs cannot share endpoints, history projections, or notices. Unknown coding-session owner never broadcasts to all tenants.
14. Next relevant user turn explains every failed/expired cue from durable status even after compaction/restart; consumed notices are not endlessly reinjected.

Manual final validation should use a consenting test user in Discord voice: start a 90-second cue, verify a correlated playback-completion record at the correct endpoint, then repeat with absence, reconnect, and transfer. Confirm the user experience separately; telemetry alone cannot establish audibility.

## 11. Tradeoffs and review decisions

- **Origin-by-default with freely available explicit overrides (user decision, 2026-10-03):** authoritative current-channel context and captured async origin remove the need to guess or restate the destination. The agent may nevertheless choose another valid target as a judgement call, without a consent gate or authorization reference. Full destination validation still rejects invalid or unauthorized targets. Residual risk is deliberate: an agent can still choose a wrong-but-valid destination. The mitigation is origin-by-default, visible origin/target receipts, and failure/outcome observability—not a gate proving user intent. This preserves useful autonomous cross-channel behavior but does not guarantee the semantic correctness of every explicit target choice.
- **Durable timers vs short-lived intent:** persisting origin/status increases storage complexity but prevents disappearance. Lease invalidation and expiry prevent inappropriate replay after restart.
- **Two journals vs one:** Agent outbox plus Bridge inbox adds protocol and cleanup work, but is necessary to bridge independent crashes without pretending send and history writes are atomic. Start with conservative ack levels, not a distributed transaction.
- **Retries vs duplicated gym instructions:** choose conservative handling of ambiguous voice delivery. At-least-once external retries are not universally safer than an explicit unknown status.
- **Pinned vs follow-conversation:** scheduled reminders default pinned; live cues can follow only a confirmed move of their same active conversation. A global “latest channel” is explicitly rejected.
- **Strict registry vs startup convenience:** unknown registry means unavailable, not all channels active. This can temporarily block sends during startup; durable bounded queueing for previously validated endpoints mitigates that without guessing.
- **Intent suppression vs guaranteed notification:** a required cue needs a delivery or an explicit recorded suppression decision. Reliability does not mean forcing stale speech regardless of what the user did meanwhile.

Product decisions to confirm before implementation: live-cue grace duration (proposed 20s), default transfer behavior for a workout lineage, which user actions constitute ending/resetting a live lease, receipt/history retention, and whether any general reminders should opt into `RingThenDm`. None of these should be left to accidental adapter fallback.
