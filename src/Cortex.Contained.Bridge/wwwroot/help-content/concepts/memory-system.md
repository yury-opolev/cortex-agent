# The Memory System

Cortex has a long-term memory that survives across conversations. It's built so that
useful facts accumulate without the store filling up with duplicates or noise.

## How facts get in

1. **During a conversation**, your messages and the assistant's replies are appended
   to an extraction buffer. Nothing is extracted yet — the live context is doing the
   remembering for now.
2. **When the conversation is compacted** (see
   [Conversation Compaction](help:conversation-compaction)) — or on idle, manual, or
   emergency triggers — the buffer is flushed to a background extraction service.
3. An LLM **extracts candidate facts** from those messages.
4. Each candidate goes through **consolidation**, where an LLM decides to **ADD** a
   new memory, **UPDATE** an existing one, **DELETE** one that's now wrong, or do
   **NOOP**.

Crucially, the extraction buffer is always flushed *before* the conversation history
is summarized — so nothing is lost when the history is compressed.

## Keeping the store clean

- **Duplicate detection** rejects a new memory that is too similar to an existing one
  (controlled by the Duplicate Detection Threshold).
- A periodic **compaction sweep** merges near-duplicate memories already in the store
  (controlled by the Compaction Similarity Threshold).
- All memory writes — from extraction, the sweep, and manual edits — are **serialized
  through a single lock**, so concurrent updates can't corrupt or race the store.

Tune these in [Memory Settings](help:memory-settings).

## Where it lives and how you control it

Memories are stored with vector embeddings (SQLite + a vector extension) inside the
Agent, enabling semantic recall. You can review, add, edit, and delete memories per
tenant under **Tenant → Memory**, and you can simply ask the assistant to remember or
forget something in conversation.
