using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Tests;

public class HistoryDeletionResponseTests
{
    [Fact]
    public void From_ReviewChosen_ConfirmationStatesTheChoiceAndWhereToReview()
    {
        var response = HistoryDeletionResponse.From(new HistoryPurgeResultDto { Deleted = 12, ErasureComplete = true, QuarantinedMemories = 3 });

        Assert.Equal(12, response.DeletedCount);
        Assert.Equal(DependentMemoryActions.Review, response.DependentMemories);
        Assert.Contains("You chose Review: 3 memories", response.Message, StringComparison.Ordinal);
        Assert.Contains("Recall → Needs review", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_DeleteChosen_ConfirmationStatesHowManyWereErased()
    {
        var response = HistoryDeletionResponse.From(new HistoryPurgeResultDto
        {
            Deleted = 12, ErasureComplete = true, DeletedMemories = 2, DependentMemories = DependentMemoryActions.Delete,
        });

        Assert.Equal(2, response.DeletedMemories);
        Assert.Equal(DependentMemoryActions.Delete, response.DependentMemories);
        Assert.Contains("You chose Delete: 2 memories created in that period were erased with the history.", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_DeleteChosenButPending_SaysTheyAreHiddenUntilCleanupFinishes()
    {
        var response = HistoryDeletionResponse.From(new HistoryPurgeResultDto
        {
            Deleted = 1, ErasureComplete = false, CleanupPending = ["dependent-memories"], QuarantinedMemories = 2, DependentMemories = DependentMemoryActions.Delete,
        });

        Assert.Contains("are hidden now and are erased when the cleanup finishes", response.Message, StringComparison.Ordinal);
        Assert.Contains("finish automatically", response.Message, StringComparison.Ordinal);
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
