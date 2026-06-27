# Memory Settings

Found under **Global Settings → Memory**. These tune how aggressively Cortex
de-duplicates memories and how it handles long or idle conversations. **Changes
take effect immediately** — no restart needed.

For the bigger picture of how memory works, see [The Memory System](help:memory-system)
and [Conversation Compaction](help:conversation-compaction).

## Duplicate Detection Threshold

Cosine-similarity cutoff above which a **new** memory is rejected as a duplicate of
an existing one.

- `0.90` is the default.
- Higher = stricter (only near-identical memories are merged; you keep more).
- `0` disables duplicate rejection entirely.

## Compaction Similarity Threshold

The similarity cutoff used by the **periodic compaction sweep** that merges
near-duplicate memories already in the store.

- `0.70` is the default.
- This is intentionally lower than the duplicate threshold, because the sweep is
  consolidating an existing collection rather than gatekeeping new writes.

## Enable periodic compaction

Toggles the background sweep that merges similar memories over time on or off.

## Idle Session Handling

Controls what happens when a conversation sits idle too long.

- **Compact on idle (instead of wiping)** — when on, an idle conversation is
  summarized by the LLM and the summary is kept. When off, idle history is wiped
  entirely. Compaction is the safer choice; it preserves context as a summary.
- **Idle Timeout (minutes)** — minutes of inactivity before a session is compacted
  (or cleared). `360` (6 hours) is the default; `0` disables idle handling.

## Preserve Recent User Turns (compaction)

When summarizing conversation history, keep this many of the **most recent user
turns** (with their assistant/tool replies) **verbatim**, provided their combined
size fits within 25% of the context window.

- `4` is the default.
- `0` means always summarize everything, keeping nothing verbatim.

This keeps the tail of a long conversation crisp while still compressing the older
part into a summary.
