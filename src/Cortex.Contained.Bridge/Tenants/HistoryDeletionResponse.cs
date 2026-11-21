using System.Globalization;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tenants;

/// <summary>Body of <c>PUT /api/tenants/{tenantId}/history/recall-tools</c>.</summary>
public sealed record RecallToolsRequest(bool Enabled);

/// <summary>
/// JSON returned by the history DELETE endpoints. Keeps the historical <c>deletedCount</c> field
/// and adds the erasure status plus a ready-to-show confirmation that states the D10 quarantine.
/// </summary>
public sealed record HistoryDeletionResponse(
    int DeletedCount,
    bool ErasureComplete,
    IReadOnlyList<string> CleanupPending,
    int QuarantinedMemories,
    string Message)
{
    /// <summary>Builds the response from the agent's purge result.</summary>
    public static HistoryDeletionResponse From(HistoryPurgeResultDto result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var message = string.Create(CultureInfo.InvariantCulture, $"Deleted {result.Deleted} messages.");
        if (result.QuarantinedMemories > 0)
        {
            message += string.Create(
                CultureInfo.InvariantCulture,
                $" {result.QuarantinedMemories} memories created in that period are hidden from the agent until you keep or delete them under Memory → Needs review. They are never deleted automatically.");
        }

        if (!result.ErasureComplete)
        {
            message += " Some cleanup is still running and will finish automatically.";
        }

        return new HistoryDeletionResponse(result.Deleted, result.ErasureComplete, result.CleanupPending, result.QuarantinedMemories, message);
    }
}
