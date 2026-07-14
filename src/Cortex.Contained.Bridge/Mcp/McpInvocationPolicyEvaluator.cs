using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Pure, fully unit-testable enforcement of <see cref="McpKustoReadBoundsConfig"/>. Intended for a
/// TRUSTED WRAPPER MCP tool that exposes cluster/database/lookback/row-limit/query as separate
/// structured arguments — never for a tool that accepts only a raw, unbounded KQL string. Callers
/// must invoke <see cref="EvaluateKustoRead"/> BEFORE dispatching the invocation to the MCP server;
/// a non-null return means the request must be refused.
/// <para>
/// Fails CLOSED: a misconfigured policy (missing allow-listed cluster/database, or a non-positive
/// bound) rejects EVERY request rather than being treated as "no restriction configured".
/// </para>
/// </summary>
public static class McpInvocationPolicyEvaluator
{
    /// <summary>
    /// Evaluates a structured Kusto read request against <paramref name="bounds"/>. Returns
    /// <c>null</c> when the request is within policy, or a human-readable rejection reason
    /// otherwise. Every structured field is required — a wrapper tool that cannot supply all five
    /// (cluster, database, lookback, row limit, query) is not a fit for this policy seam at all.
    /// </summary>
    public static string? EvaluateKustoRead(
        McpKustoReadBoundsConfig bounds,
        string? cluster,
        string? database,
        int? lookbackHours,
        int? rowLimit,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        if (string.IsNullOrWhiteSpace(bounds.AllowedCluster)
            || string.IsNullOrWhiteSpace(bounds.AllowedDatabase)
            || bounds.MaxLookbackHours <= 0
            || bounds.MaxRowLimit <= 0)
        {
            return "Kusto read-bounds policy is not configured with an allow-listed cluster/database and positive bounds; refusing every request.";
        }

        if (string.IsNullOrWhiteSpace(cluster)
            || string.IsNullOrWhiteSpace(database)
            || lookbackHours is null
            || rowLimit is null
            || string.IsNullOrWhiteSpace(query))
        {
            return "Kusto read request is missing a required structured field (cluster, database, lookbackHours, rowLimit, query).";
        }

        if (!string.Equals(cluster, bounds.AllowedCluster, StringComparison.Ordinal)
            || !string.Equals(database, bounds.AllowedDatabase, StringComparison.Ordinal))
        {
            return $"Cluster '{cluster}' / database '{database}' is not the allow-listed cluster/database.";
        }

        if (lookbackHours <= 0 || lookbackHours > bounds.MaxLookbackHours
            || rowLimit <= 0 || rowLimit > bounds.MaxRowLimit)
        {
            return $"Requested lookback ({lookbackHours}h) or row limit ({rowLimit}) exceeds the configured bound (max {bounds.MaxLookbackHours}h / {bounds.MaxRowLimit} rows).";
        }

        return null;
    }
}
