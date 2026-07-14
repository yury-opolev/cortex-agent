namespace Cortex.Contained.Bridge.Tests.Mcp.Actions;

/// <summary>
/// Serializes the SQLite-backed MCP action test classes so xUnit never runs them in parallel with
/// each other. They each dispose via the PROCESS-GLOBAL <c>SqliteConnection.ClearAllPools()</c>,
/// which races when parallel classes clear one another's pooled connections mid-test (a CI flake).
/// Membership (via <c>[Collection]</c>) puts them in one non-parallel collection; it needs no
/// shared fixture — each class still owns its own temp database.
/// </summary>
[CollectionDefinition(Name)]
public sealed class McpActionStoreCollectionDefinition
{
    public const string Name = "mcp-action-sqlite";
}
