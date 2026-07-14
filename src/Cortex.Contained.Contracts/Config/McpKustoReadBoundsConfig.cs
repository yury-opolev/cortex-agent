namespace Cortex.Contained.Contracts.Config;

/// <summary>
/// Optional read-bounds policy for a Kusto (Azure Data Explorer) MCP tool. This is a POLICY SEAM:
/// it is usable ONLY with a TRUSTED WRAPPER MCP that exposes structured fields for cluster,
/// database, lookback window, row limit, and query as separate, named tool arguments — never with
/// a server whose only surface is unrestricted raw KQL execution. If an MCP server exposes raw KQL
/// with no structured cluster/database/lookback/row-limit fields to bind against, this policy has
/// nothing to enforce and that server must NOT be enabled for autonomous workers at all.
/// <para>
/// Enforcement lives in <c>Cortex.Contained.Bridge.Mcp.McpInvocationPolicyEvaluator</c>, which
/// fails CLOSED: a misconfigured policy (missing allow-list or non-positive bounds) rejects every
/// request rather than being treated as "no restriction".
/// </para>
/// </summary>
public sealed class McpKustoReadBoundsConfig
{
    /// <summary>
    /// Exact-match allow-listed Kusto cluster. Empty (default) makes the policy unusable — every
    /// request is rejected until an administrator sets this explicitly.
    /// </summary>
    public string AllowedCluster { get; set; } = string.Empty;

    /// <summary>
    /// Exact-match allow-listed database within <see cref="AllowedCluster"/>. Empty (default)
    /// makes the policy unusable — every request is rejected until set explicitly.
    /// </summary>
    public string AllowedDatabase { get; set; } = string.Empty;

    /// <summary>Maximum lookback window, in hours, a read may request. Must be positive.</summary>
    public int MaxLookbackHours { get; set; } = 24;

    /// <summary>Maximum row count a read may request. Must be positive.</summary>
    public int MaxRowLimit { get; set; } = 1000;
}
