namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// One entry in the compact tool-call summary attached to an assistant message.
/// </summary>
/// <param name="Name">Tool name verbatim from the LLM tool call.</param>
/// <param name="Args">Arguments string, truncated to 32 characters with "…" suffix when longer.</param>
/// <param name="Ok">Whether the tool execution succeeded.</param>
/// <param name="Pos">"before" if the tool ran in a tool-only LLM response that preceded this text record; "after" if it ran in the same LLM response that produced this text.</param>
public sealed record ToolCallSummaryEntry(string Name, string Args, bool Ok, string Pos);
