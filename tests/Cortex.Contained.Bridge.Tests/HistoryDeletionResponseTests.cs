using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tests;

public class HistoryDeletionResponseTests
{
    [Fact]
    public void From_QuarantinedMemories_ConfirmationStatesReviewAndNoAutoDelete()
    {
        var response = HistoryDeletionResponse.From(new HistoryPurgeResultDto { Deleted = 12, ErasureComplete = true, QuarantinedMemories = 3 });

        Assert.Equal(12, response.DeletedCount);
        Assert.Contains("3 memories", response.Message, StringComparison.Ordinal);
        Assert.Contains("never deleted automatically", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_PendingCleanup_SaysItFinishesAutomatically()
    {
        var response = HistoryDeletionResponse.From(new HistoryPurgeResultDto
        {
            Deleted = 1, ErasureComplete = false, CleanupPending = ["messages-wal"],
        });

        Assert.False(response.ErasureComplete);
        Assert.Equal(["messages-wal"], response.CleanupPending);
        Assert.Contains("finish automatically", response.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("memories", response.Message, StringComparison.Ordinal);
    }
}
