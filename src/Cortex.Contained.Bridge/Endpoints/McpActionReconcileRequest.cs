namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Body of a reconcile decision resolving an <c>outcome_unknown</c> MCP action based on human
/// or remote evidence. <paramref name="Outcome"/> must be <c>succeeded</c> or <c>failed</c>;
/// <paramref name="Evidence"/> (what was verified on the remote system) is mandatory.
/// </summary>
/// <param name="ArgumentsHash">The exact canonical-argument hash the reconciliation is bound to (<c>sha256:…</c>).</param>
/// <param name="Outcome"><c>succeeded</c> or <c>failed</c>.</param>
/// <param name="Evidence">Mandatory description of the evidence backing the resolution.</param>
/// <param name="RemoteReference">Optional remote-system reference (e.g. the created issue's URL).</param>
public sealed record McpActionReconcileRequest(
    string ArgumentsHash,
    string Outcome,
    string Evidence,
    string? RemoteReference);
