using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Bridge.Storage;

/// <summary>A store the Bridge keeps on the Windows host.</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Label">Human-readable label.</param>
/// <param name="State"><c>encrypted</c>, <c>plaintext</c> or <c>absent</c>.</param>
/// <param name="Detail">What it holds and how it is protected.</param>
public sealed record BridgeStoreState(string Id, string Label, string State, string Detail);

/// <summary>
/// The Bridge's own SQLite stores under <c>%LOCALAPPDATA%\Cortex</c>, classified by header. The Bridge keeps
/// no message history today (the agent owns <c>messages.db</c>); a <c>messages.db</c> in the data directory is
/// a file of an old version, encrypted with the Bridge database key and unused.
/// </summary>
public static class BridgeStorageReport
{
    /// <summary>The stores below <paramref name="cortexDataDir"/>.</summary>
    public static IReadOnlyList<BridgeStoreState> Scan(string cortexDataDir) =>
    [
        Store(
            "mcp-actions",
            "MCP action ledger (mcp/actions.db)",
            Path.Combine(cortexDataDir, "mcp", "actions.db"),
            "Approvals and outcomes of mutating MCP calls, SQLite3 Multiple Ciphers with the DPAPI-protected Bridge database key."),
        Store(
            "legacy-messages",
            "Old Bridge message history (messages.db)",
            Path.Combine(cortexDataDir, "messages.db"),
            "Written by Bridge versions that kept message history; nothing uses it now. Encrypted with the Bridge database key; delete it once you no longer need it."),
    ];

    private static BridgeStoreState Store(string id, string label, string path, string detail) => new(
        id,
        label,
        SqliteFileHeader.Classify(path) switch
        {
            StoredFileState.Encrypted => StorageItemStates.Encrypted,
            StoredFileState.Plaintext => StorageItemStates.Plaintext,
            _ => StorageItemStates.Absent,
        },
        detail);
}
