# Tool Output Truncation

## Problem

When a tool (file_read, run_command, etc.) returns a huge result (100KB+),
the entire output lands in the conversation history as a tool result message.
This wastes context window budget and can cause the LLM to drop all messages
when the context overflows.

Layers 2-5 of context management (pruning, stripping, trimming, reactive retry)
handle this after the fact, but the most effective defense is preventing massive
content from entering the history in the first place.

## How OpenCode Does It

Reference: `opencode/packages/opencode/src/tool/truncation.ts`

1. **Every tool output** goes through `Truncate.output()` before being returned
2. If output exceeds **50KB or 2000 lines**, it is truncated
3. The **full output is saved to a temp file** on disk (`{data-dir}/tool-output/tool_{id}`)
4. The LLM receives: `{first 50KB preview}\n\n...{N} bytes truncated...\n\n{hint}`
5. The hint tells the LLM how to access the rest:
   "Use Grep to search the full content or Read with offset/limit to view specific sections."

### Cleanup mechanism

- `Truncate.init()` registers a global scheduler task (runs immediately on startup + every 1 hour)
- Deletes files older than **7 days** (retention encoded in filename via timestamp-based ID, not filesystem mtime)
- `setInterval` with `.unref()` (won't keep process alive)
- Best-effort: errors silently swallowed, no external daemon
- **Reliable enough** because the immediate-on-startup run cleans up after crashes

## Implementation (Done)

### ToolOutputTruncator

Created `src/Cortex.Contained.Agent.Host/Agent/ToolOutputTruncator.cs` — static utility:

- `Initialize(dataRoot)` — sets output directory, runs cleanup
- `Truncate(output)` — if output exceeds 50KB or 2000 lines:
  - Saves full output to `{dataRoot}/tool-output/{timestamp}-{guid}.txt`
  - Returns preview (first 50KB/2000 lines) + truncation notice + hint
  - Hint: `"Full output saved to: {path}\nUse file_read with offset/limit to view specific sections."`
  - If within limits, returns original string unchanged (reference equality)
- `CleanupStaleFiles()` — deletes files older than 7 days (by creation time)

### Integration point

Integrated at `ToolRegistry.ExecuteAsync` — the single choke point where **all**
tool results flow through. No per-tool modifications needed. Only successful tool
results with non-empty content are truncated (errors pass through unchanged).

This covers all 15 tools automatically:
- **High risk**: `run_command` (512KB cap), `file_read` (10MB files), `file_list` (1000 entries), `file_find` (500 entries)
- **Moderate risk**: `date_time` (list_timezones), `memory_search`, `memory_get`, `memory_update`
- **Low risk**: All others (short confirmation strings)

### Startup initialization

`Program.cs` calls `ToolOutputTruncator.Initialize(sandboxRoot)` at startup,
which creates the output directory and cleans up stale files from previous runs.
No background timer — startup-only cleanup (Option B from the plan). Cortex Host
restarts frequently enough that this is sufficient.

### Tests

Created `tests/Cortex.Contained.Agent.Host.Tests/ToolOutputTruncatorTests.cs` — 13 tests:

- Small/empty/null output returns unchanged
- Exactly-at-limit returns unchanged
- Exceeds max bytes: truncates and saves file
- Exceeds max lines: truncates and saves file
- Preview contains first lines, does not contain truncated lines
- Hint contains absolute file path that exists on disk
- Cleanup deletes old files, preserves recent files
- 120KB web fetch scenario: truncated to ~50KB, full content saved
- 512KB command output scenario: header preserved, footer truncated

## Files created/modified

| File | Action |
|------|--------|
| `src/Cortex.Contained.Agent.Host/Agent/ToolOutputTruncator.cs` | Created |
| `src/Cortex.Contained.Agent.Host/Tools/ToolRegistry.cs` | Modified (truncation after tool execution) |
| `src/Cortex.Contained.Agent.Host/Program.cs` | Modified (Initialize call at startup) |
| `tests/Cortex.Contained.Agent.Host.Tests/ToolOutputTruncatorTests.cs` | Created |

## Context Management Stack (complete)

All layers now implemented, ordered by when they act:

| Layer | When | What | File |
|-------|------|------|------|
| 1. **Tool output truncation** | At tool execution time | Cap output at 50KB/2000 lines, save full to disk | `ToolOutputTruncator.cs` |
| 2. **Prune old tool results** | Before each LLM call | Replace old tool result content with placeholder | `ContextManager.PruneToolResults` |
| 3. **Trim to fit** | Before each LLM call | Drop oldest message groups that exceed budget (skip, don't break) | `TokenEstimator.TrimToFit` |
| 4. **Empty messages guard** | At API call time | Return error instead of sending empty messages list | `DirectLlmClient` |
| 5. **Reactive overflow retry** | After API error | Detect overflow → strip media → compact → retry | `AgentRuntime.EmergencyCompactAsync` |
