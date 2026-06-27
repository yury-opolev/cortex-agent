# Conversation Compaction

A conversation can't grow forever — every model has a finite **context window**.
Compaction keeps long conversations going by replacing old history with an LLM-written
**summary** when the context gets large.

## When it happens

Compaction triggers when the conversation reaches about **65% of the model's context
window**, and at a few other moments:

- **Regular** — during the normal tool loop when the threshold is crossed.
- **Idle** — when a session has been inactive past the idle timeout (if "compact on
  idle" is enabled; otherwise idle history is wiped).
- **Emergency** — if the context would otherwise overflow.
- **Manual** — when you ask for it (e.g. a `/compact` command).
- **Seeded history** — when a session is seeded with prior context, such as during a
  cross-channel transfer.

## What it preserves

When summarizing, Cortex can keep the **most recent user turns verbatim** (with their
assistant/tool replies) if they fit within ~25% of the context window — controlled by
**Preserve Recent User Turns** in [Memory Settings](help:memory-settings). The older
part of the conversation becomes a summary; the recent tail stays exact.

## Relationship to memory

Before any compaction, the extraction buffer is **flushed to long-term memory** (see
[The Memory System](help:memory-system)). So durable facts are captured into memory
*before* the raw history is compressed away — compaction shortens the working context
without losing what mattered.
