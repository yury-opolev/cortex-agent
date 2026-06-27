# Cross-Channel Transfer

Cortex can move an ongoing conversation from one channel to another — for example,
"let's continue this in voice" — carrying the relevant context with it.

## How it works

1. You ask to continue elsewhere. The agent uses an LLM **topic slicer** to find the
   boundary of the current topic in the conversation, so it transfers the relevant
   slice rather than the entire history.
2. That slice **seeds** a session on the target channel, giving the new channel the
   context it needs to pick up where you left off.
3. After seeding, Cortex emits a **proactive greeting** on the target channel.

## Voice targets

When the target is a voice channel and you're not already there, the greeting flows
through the Bridge's ring/invite path — for Discord voice, that means the bot calls
you into the voice channel automatically.

## Reverting

A transfer can be undone with a revert, restoring the prior arrangement if you change
your mind.

This builds on the same seeding-and-compaction machinery described in
[Conversation Compaction](help:conversation-compaction).
