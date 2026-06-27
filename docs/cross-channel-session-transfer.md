# Cross-channel session transfer

A user-facing reference for the `transfer_session` and `revert_transfer` agent tools.

## What it does

Lets you ask the agent to continue an in-flight conversation in a different channel without losing context.

> *"Hey, let's continue this in voice."*

The agent picks up the request via natural language, runs an internal LLM call to identify the most recent topic in your current conversation, summarizes everything before it, and seeds the target channel's agent session with the summary + the verbatim recent exchange. Your next utterance in the target channel lands on an agent that already knows what you were just discussing.

## How to invoke it

There's no slash command or UI button. Just ask the agent in plain language:

- *"Let's continue in voice."*
- *"Move this conversation to Discord."*
- *"Pick this up in webchat instead."*
- *"Switch to voice for the rest of this."*

The agent decides when to call `transfer_session`. If it's ambiguous (e.g., "let's talk about this in voice tomorrow" — not actually a transfer request, just a future-tense reference), the agent shouldn't act on it. You can be explicit if needed: *"Transfer this conversation to voice now."*

## Tool parameters

### `target_channel` (required, string)

The canonical channel id to transfer to (e.g. `voice-default`, `discord-dm`). Must be an active channel different from the current one. Friendly names (e.g. `voice`) are resolved automatically.

### `user_confirmed` (required, boolean)

The agent must obtain explicit user approval before transferring the conversation. Ambiguous phrasings such as "tell me via voice" or "send it to discord-voice" are **not** confirmation: the user may want a one-shot delivery in the target channel (use `send_message`) while keeping the conversation in the current channel. The agent asks the user something like "Want me to move our whole conversation to {target}, or just speak the answer there?" and only invokes `transfer_session` with `user_confirmed: true` after the user agrees. Calling with `user_confirmed: false` (or omitting the field) returns an error pointing the agent at `send_message`.

## How transfer works

- The agent calls `transfer_session` with `user_confirmed: true` after the user explicitly agrees to move the conversation.
- An internal LLM call (the "slicer") identifies the latest topic boundary in the source session's history.
- Everything before the boundary is summarized; verbatim messages from the boundary onward are carried directly.
- The target channel's session is seeded with the summary + verbatim slice; its prior history is replaced.
- A greeting is dispatched to the target channel naming the topic (Discord voice channels get a ring + invite if the user is absent).
- In-flight subagents owned by the source conversation are filtered by topic before seeding. The slicer's boundary message timestamp is the watershed: subagents created at or after that timestamp follow the user to the target conversation/channel; subagents created earlier (during a prior topic) stay pinned to the source, so their completion notification lands where that conversation continues.

## What you see in the UI

In the **source** channel (where you were):

> **system** — `→ Continued in Voice` *(Transfer badge)*

In the **target** channel (where the conversation moved to):

> **system** — `↳ Continued from WebChat` *(Transfer badge)*
>
> **assistant** — *"Continuing our conversation here. We were just talking about the TTS streaming bug."*

The "Transfer" badge is rendered with a distinct color so the breadcrumbs stand out from regular messages.

## What happens when the target channel needs you to join

For Discord voice channels: if you say "let's continue in voice" but you're not currently in the voice channel, the bot will:

1. Join the voice channel itself
2. Create a short-lived Discord invite link
3. DM you the link (a "ring")
4. Queue the conversation-continuation greeting

If you click the invite within the TTL → the bot speaks the greeting and you continue the conversation in voice.

If you don't join within the TTL → the bot sends the greeting as a Discord voice-message attachment in DM and leaves the voice channel. You can listen at your convenience.

The same path is used by `send_message` for any proactive voice delivery, so the behavior matches what you've seen before with reminders or scheduled task notifications.

## Undoing a transfer

If you regret the move ("wait, I wanted to keep both threads" or "the slicer dropped something important"), say:

- *"Revert that transfer."*
- *"Bring back what was here before."*

The agent calls `revert_transfer` and the target channel's session is restored to its pre-transfer state. Important caveats:

- **Only the most recent transfer per channel is reversible.** A second consecutive revert fails — the snapshot is consumed when you revert.
- **Snapshots are in-memory only.** If the agent restarts (Docker container restart, redeploy) between the transfer and your revert request, the snapshot is gone.
- **Revert restores the session for the LLM; the UI breadcrumbs stay.** The "↳ Continued from WebChat" line in the target's chat history isn't deleted — it's a historical record.

## When it doesn't work

| Situation | What happens | Why |
|-----------|--------------|-----|
| "Let's transfer to webchat" but you're already in webchat | Error: `target_channel cannot be the same as the current channel` | Self-transfer is a no-op. |
| Transfer from a scheduled-task context | Error: `not a transferable conversation` | Synthetic conversation ids (scheduled tasks, sub-agents) aren't backed by a real user channel. |
| Conversation has fewer than 2 user turns | Error: `Source session has no meaningful history to transfer yet` | The slicer needs something to slice. |
| Target channel isn't registered on this Bridge (e.g., Discord disabled) | Error: `target_channel 'X' is not currently active` | The Bridge tells the agent which channels are configured. |
| Slicer LLM call fails outright | Error: `Transfer failed: could not slice source history (...)` | Transient; retry works. |
| Slicer LLM returns malformed JSON | Succeeds with a degraded note: last 10 messages verbatim, no prior summary | Defensive fallback so a flaky LLM response doesn't block the transfer. |

## Operator notes

Configuration knobs (bound from `Agent:TransferSession`):

- `SlicerModel` — override the LLM used by the slicer. Defaults to `IModelProvider.MemoryModel` (same as compaction).
- `SlicerTemperature` — sampling temperature for the slicer call. Default `0.3`.
- `SlicerSystemPromptOverride` — replace the baked-in slicer prompt. Must still elicit JSON with `boundaryIndex` / `topicOneLine` / `priorSummary` fields.

See `docs/architecture.md` for the component diagram and `docs/superpowers/specs/2026-05-11-cross-channel-session-transfer-design.md` for the original design rationale.
