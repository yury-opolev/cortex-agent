using System.Globalization;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tenants;

/// <summary>Body of <c>PUT /api/tenants/{tenantId}/history/recall-tools</c>.</summary>
public sealed record RecallToolsRequest(bool Enabled);

/// <summary>
/// JSON returned by the history DELETE endpoints and <c>POST /api/tenants/{tenantId}/recall/purge</c>. Keeps the
/// historical <c>deletedCount</c> field and adds the erasure status plus a ready-to-show confirmation that states
/// what happened to the memories created in the purged period (the owner's per-purge choice).
/// </summary>
public sealed record HistoryDeletionResponse(
    int DeletedCount,
    bool ErasureComplete,
    IReadOnlyList<string> CleanupPending,
    int QuarantinedMemories,
    string Message,
    int DeletedMemories = 0,
    string DependentMemories = DependentMemoryActions.Review)
{
    /// <summary>Builds the response from the agent's purge result.</summary>
    public static HistoryDeletionResponse From(HistoryPurgeResultDto result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var delete = DependentMemoryActions.IsDelete(result.DependentMemories);
        var message = string.Create(CultureInfo.InvariantCulture, $"Deleted {result.Deleted} messages.");
        if (result.DeletedMemories > 0)
        {
            message += string.Create(
                CultureInfo.InvariantCulture,
                $" You chose Delete: {result.DeletedMemories} memories created in that period were erased with the history.");
        }

        if (result.QuarantinedMemories > 0)
        {
            message += delete
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $" You chose Delete: {result.QuarantinedMemories} memories created in that period are hidden now and are erased when the cleanup finishes.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $" You chose Review: {result.QuarantinedMemories} memories created in that period are hidden from the agent until you keep or delete them under Recall → Needs review.");
        }

        if (!result.ErasureComplete)
        {
            message += " Some cleanup is still running and will finish automatically.";
        }

        return new HistoryDeletionResponse(
            result.Deleted,
            result.ErasureComplete,
            result.CleanupPending,
            result.QuarantinedMemories,
            message,
            result.DeletedMemories,
            delete ? DependentMemoryActions.Delete : DependentMemoryActions.Review);
    }
}
