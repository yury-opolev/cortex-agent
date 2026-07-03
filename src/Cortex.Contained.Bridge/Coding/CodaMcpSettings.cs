namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// UI-managed MCP policy for the coda coding engine. A null <see cref="Mcp"/> means "not set via the
/// UI — fall back to the <c>Coding:Coda:Mcp</c> value from cortex.yml".
/// </summary>
public sealed record CodaMcpSettings(CodaMcpPolicy? Mcp, string? CuratedMcpDir);
