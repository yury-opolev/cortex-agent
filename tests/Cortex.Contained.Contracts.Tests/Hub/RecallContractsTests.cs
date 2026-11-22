using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Contracts.Tests.Hub;

public sealed class RecallContractsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("review", true)]
    [InlineData("DELETE", true)]
    [InlineData("purge", false)]
    public void DependentMemoryActions_IsValid_AcceptsReviewDeleteOrEmpty(string? value, bool expected)
    {
        Assert.Equal(expected, DependentMemoryActions.IsValid(value));
    }

    [Fact]
    public void HistoryPurgeRequestDto_Default_ReviewsDependentMemories()
    {
        var request = new HistoryPurgeRequestDto();

        Assert.Equal(DependentMemoryActions.Review, request.DependentMemories);
        Assert.False(DependentMemoryActions.IsDelete(request.DependentMemories));
    }

    [Fact]
    public void RecallHub_Methods_UseSerializableDtos()
    {
        var keep = typeof(IRecallHub).GetMethod(nameof(IRecallHub.KeepQuarantinedMemories))!;
        var delete = typeof(IRecallHub).GetMethod(nameof(IRecallHub.DeleteQuarantinedMemories))!;

        Assert.Equal(typeof(Task<QuarantineDecisionResultDto>), keep.ReturnType);
        Assert.Equal([typeof(IReadOnlyList<string>)], delete.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(typeof(Task<HistoryPurgePreviewDto>), typeof(IRecallHub).GetMethod(nameof(IRecallHub.PreviewHistoryPurge))!.ReturnType);
    }
}
