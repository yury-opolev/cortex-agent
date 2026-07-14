namespace Cortex.Contained.Agent.Host.Mcp;

/// <summary>
/// Redacts MCP tool payloads (arguments/results) before they reach LOGS, <c>ToolExecutionMessage</c>
/// (the Bridge-facing telemetry notification), or the persisted tool-call summary. Every
/// <c>mcp__*</c>-prefixed tool name is treated as sensitive by default — an MCP tool call can carry
/// incident content, PII, or telemetry that must never sit in a log line or history record.
/// <para>
/// TELEMETRY-ONLY: sanitized values are used exclusively for observability surfaces. The REAL
/// arguments still reach the Bridge (dispatch to the actual MCP server) and the REAL result still
/// reaches the LLM tool-result message — redaction never changes tool behavior.
/// </para>
/// </summary>
internal static class McpTelemetrySanitizer
{
    internal const string RedactedPayload = "[redacted MCP payload]";

    internal static string Input(string toolName, string input)
        => toolName.StartsWith("mcp__", StringComparison.Ordinal)
            ? RedactedPayload
            : input;

    internal static string? Output(string toolName, string? output)
        => toolName.StartsWith("mcp__", StringComparison.Ordinal)
            ? RedactedPayload
            : output;
}
