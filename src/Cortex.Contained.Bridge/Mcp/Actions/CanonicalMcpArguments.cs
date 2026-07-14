namespace Cortex.Contained.Bridge.Mcp.Actions;

/// <summary>
/// The canonical form of an MCP tool call's arguments: <paramref name="Json"/> is the compact,
/// key-sorted UTF-8 JSON text and <paramref name="Sha256"/> its <c>sha256:&lt;lowercase hex&gt;</c>
/// digest. The digest is the BINDING IDENTITY a human approval attaches to — an approved mutating
/// call may only ever be dispatched with arguments whose canonical hash matches exactly.
/// </summary>
/// <param name="Json">Canonical compact JSON (object keys ordinal-sorted recursively, array order preserved, numeric lexical form preserved).</param>
/// <param name="Sha256">SHA-256 of the canonical UTF-8 bytes, formatted <c>sha256:&lt;lowercase hex&gt;</c>.</param>
public sealed record CanonicalMcpArguments(string Json, string Sha256);
