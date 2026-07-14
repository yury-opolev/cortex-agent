namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Body of an approve/reject/cancel decision on one MCP action. <paramref name="ArgumentsHash"/>
/// must equal the action's stored canonical-argument hash EXACTLY — a stale hash never mutates
/// anything (HTTP 409). <paramref name="ExpiresAtUtc"/> (approve only) bounds how long the
/// approval stays dispatchable; omitted, a server-side default applies.
/// </summary>
/// <param name="ArgumentsHash">The exact canonical-argument hash the decision is bound to (<c>sha256:…</c>).</param>
/// <param name="Reason">Optional human-readable reason recorded in the audit trail.</param>
/// <param name="ExpiresAtUtc">Approve only: when the approval expires. Must be in the future.</param>
public sealed record McpActionDecisionRequest(
    string ArgumentsHash,
    string? Reason,
    DateTimeOffset? ExpiresAtUtc);
