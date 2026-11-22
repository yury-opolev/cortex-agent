# Recall Settings

Found under **Tenants → *your tenant* → Recall**. Recall is how the assistant finds past
conversations (`history_search`) and stored memories (`memory_search`). **Changes take effect
immediately** — no restart needed — and apply to this tenant only (except the reranker switch, which is
shared by all tenants).

## Settings

- **History search** — lets the assistant search its own conversation history. When you switch it on,
  the history index is built in the background (watch **Status**); until it is complete, searches cover
  only recent messages. Switching it off hides the search tools at once.
- **Default history search mode** — *Hybrid* finds matching words and similar meaning (when dense
  embeddings are on); *Keyword only* finds exact words.
- **Dense embeddings for history** — needed for hybrid history search. Changing it rebuilds the
  history index.
- **Default memory search mode** — *Semantic*, *Hybrid* or *Keyword only* for memory searches.
- **Reranker budget** — how many hybrid results the reranker reorders, how much text it reads per result,
  and how long it may take.

Values you changed are marked *custom*. **Reset to defaults** removes all of them.

## Reranker (shared by all tenants)

Starts the `cortex-reranker` sidecar and lets every tenant use it. If it fails, the page says why — most
often the image is not built yet (`scripts\Build-RerankerImage.ps1`) or the machine has no usable GPU
(build the CPU variant with `-Variant cpu`).

## Status

Shows the history index build, the embedding progress, the reranker's health, the last event backfill
(with a button to run it) and how many memories wait for review. It refreshes every 5 seconds.

## Delete history

Choose a channel (or all), and everything, everything older than a date, or a date range. Before you
confirm, Cortex shows how many messages will be deleted and how many memories were created in that
period, and you choose what happens to those memories:

- **Review them** — they are hidden from the assistant until you keep or delete them (below).
- **Delete them with the history** — they are erased permanently together with the messages.

## Needs review

Memories created in a deleted period, with the deletion they came from. Keep or delete them one by one,
or select several (or all) and keep or delete them together.
