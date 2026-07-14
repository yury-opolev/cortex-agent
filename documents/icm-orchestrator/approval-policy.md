# Approval Policy

This file defines which incident operations the coordinator and workers may perform on their own
judgment, and which require a human decision before anything leaves the Bridge. It has two
layers:

1. **The behavioral layer** — the classification below, which tells the coordinator and workers
   which operations they should even attempt without asking. This is instruction-based: it shapes
   what the agent proposes, not what the system permits.
2. **The enforcement layer** — the real mutation-approval mechanism shipped in the Agent Host and
   Bridge (Tasks 1-11 of the reliability implementation plan). This is a genuine security
   boundary: a tool an administrator has *not* classified as mutating in that server's config runs
   directly with no gate at all, and a tool that *is* classified never dispatches without an
   exact-argument human approval — regardless of what this file says.

Configure the two layers to agree: every operation this file marks "require explicit approval by
default" should also appear in the relevant server's `mutationToolAllowList` in `cortex.yml`
(Phase 0's tool-allowlist inventory is where the exact tool names get pinned — see
[`README.md`](README.md#mcp-topology-the-six-servers)).

## Autonomous operations

The coordinator and workers may perform these without asking:

- search/read IcM;
- run bounded Kusto queries (see
  [`skills/kusto-evidence/SKILL.md`](skills/kusto-evidence/SKILL.md));
- search/read EngHub;
- search/read Teams and Mail;
- search BC MemoryMcp;
- create local reports, drafts, and recommendations (workspace files only — nothing leaves the
  workspace);
- post local dashboard state (writes to `dashboard/data.json`, not to any remote system).

These map to the read-only tools of `agency-icm`, `agency-teams`, `agency-mail`,
`agency-enghub`, `bc-memory`, and `kusto` — none of which should appear in any server's
`mutationToolAllowList`.

## Require explicit approval by default

- acknowledge an incident;
- post an IcM discussion entry or insight;
- change severity;
- request assistance;
- transfer ownership;
- mitigate, resolve, or reactivate;
- send/reply/forward email;
- send Teams messages or modify chats/channels;
- write or update shared BC MemoryMcp knowledge.

Put the corresponding tool names in each server's `mutationToolAllowList` (`agency-icm`,
`agency-teams`, `agency-mail`, `bc-memory`) once the live tool catalog has been inspected. A tool
in that list is classified `RequiresApproval = true`, and classification is rechecked immediately
before every dispatch — never trusted from what the agent saw earlier in the conversation.

The approval request must show the exact target, action, content or field changes, reason, and
expected effect — this is exactly what the shipped flow provides: the canonicalized,
sorted-key argument JSON is what a human reviews at `GET /api/mcp/actions/{actionId}` before
approving. **The worker records approval before action and verifies after action** — concretely:
record the returned `actionId` in the incident workspace before doing anything else, and re-read
the remote target after the action reaches `succeeded` to confirm the effect actually happened.

## How approval actually works (shipped mechanism)

1. A worker calls a mutation-classified tool, e.g. `mcp__agency-icm__post_discussion_entry(...)`.
   The call does not dispatch. The Bridge canonicalizes the arguments (sorted object keys,
   rejected duplicate keys, preserved array order and numeric literal form, SHA-256 hash), records
   a `Proposed` action, and returns:

   > *"Awaiting exact-argument approval. Do not repeat this mutation."*

   along with an `actionId` and `argumentsHash`. The worker records both in the incident's
   `checkpoint.json.pendingApprovals` and `actions.jsonl`
   (see [`incident-state-schema.md`](incident-state-schema.md)).
2. The engineer reviews the exact canonical arguments at
   `GET /api/mcp/actions/{actionId}` and decides:
   - `POST /api/mcp/actions/{actionId}/approve` — body `{ argumentsHash, reason?, expiresAtUtc? }`
     (default approval TTL 1 hour). Must pass the *current* `argumentsHash`; a stale hash returns
     `409` and approves nothing.
   - `POST /api/mcp/actions/{actionId}/reject` — body `{ argumentsHash, reason? }`.
3. Once approved, the Bridge's outbox dispatcher performs the one remote call. The action moves
   `approved -> dispatching -> succeeded | failed | outcome_unknown`.
4. The worker (or coordinator) follows up with the native tool `mcp_action_status(action_id)` —
   never by repeating the original call. `mcp_action_cancel(action_id, arguments_hash)` can cancel
   a still-`proposed`/`approved` action outright; cancelling a `dispatching` one only requests
   cancellation and may still resolve to `outcome_unknown` if the remote call had already begun.
5. **`outcome_unknown` is never auto-retried by Cortex.** It means the request left the Bridge and
   may have executed — a call timeout, a cancellation after dispatch started, or a transport
   failure mid-call. The worker must read back the remote target for evidence, not guess. A human
   resolves the ambiguity via `POST /api/mcp/actions/{actionId}/reconcile` — body
   `{ argumentsHash, outcome: "succeeded"|"failed", evidence, remoteReference? }` — moving the
   action to `reconciled_succeeded` or `reconciled_failed`.
6. If the Bridge restarts mid-dispatch, the interrupted action is recovered as `outcome_unknown`
   on startup, never blindly redispatched.

Full lifecycle and API surface:
[`docs/mcp-plugin-system.md`](../../docs/mcp-plugin-system.md#approval-gated-mutations-invocation-identity-and-reliability-guarantees).

### Action state progression

```
proposed -> approved -> dispatching -> succeeded
                             |-> failed
                             |-> outcome_unknown -> reconciled_succeeded | reconciled_failed
proposed -> rejected
proposed | approved -> cancelled
proposed | approved -> expired
```

This is the real, shipped state machine (`McpActionState`), not a proposal to be built —
supersedes the file-based "action record" sketch that motivated it (an incident worker does not
hand-maintain the ledger; it only mirrors the resulting `actionId`/status into its own
`actions.jsonl` for incident-level narrative, since the ledger itself is tenant-scoped and keyed
by `workerId`/`conversationId`/`channelId`, not by incident ID).

## Optional pre-authorization

After the pilot, narrowly pre-authorize low-risk actions, for example:

- acknowledge a new incident owned by a configured team;
- post a templated progress note with no customer content;
- send a message to a fixed internal channel;
- ingest a sanitized private memory rather than shared/common knowledge.

Pre-authorization must be explicit in `config/policy.md` (part of the incident workspace, see
[`incident-state-schema.md`](incident-state-schema.md#configuration-files)), bounded by
team/action/severity/content, and reversible by the operator. Pre-authorization changes what the
coordinator/worker will *propose autonomously*; it does not remove the underlying
`mutationToolAllowList` gate — a tool an administrator has classified as mutating still requires a
human `approve` call through the REST API. True unattended execution of a pre-authorized action
requires either removing that tool from `mutationToolAllowList` (accepting it as a fully
autonomous operation) or standing up an automated approver that applies the pre-authorization
rules and calls the approve endpoint on the operator's behalf — this package does not ship such an
approver; treat pre-authorization as a documented policy for a human approver to apply quickly,
not as automatic bypass.

## Important limitation

Prompt/file classification (the two lists above) is a behavioral control: it shapes what the
coordinator proposes. It is not, by itself, a security boundary — any operation that is *not*
also placed in a `mutationToolAllowList` runs immediately, with no gate, regardless of what this
file says. Keep every operation in "require explicit approval by default" mirrored into the
corresponding server's `mutationToolAllowList`, and audit that mirroring whenever a server's tool
catalog changes.

The reverse also holds: exact-argument approval proves a human approved specific arguments, and
`outcome_unknown` handling prevents Cortex from *knowingly* redispatching an ambiguous mutation —
but true exactly-once remote effects still depend on the target system (IcM, Teams, Mail,
BC MemoryMcp) being idempotent. Cortex's guarantee is that it never blindly retries; it is not a
guarantee that the provider deduplicates a retry a human later issues by hand.
