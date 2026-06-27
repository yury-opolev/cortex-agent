using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Tests.Coding;

public sealed class CodingPolicyTests
{
    [Theory]
    [InlineData(CodingPolicy.Prompt, CodingPolicy.Prompt, true)]
    [InlineData(CodingPolicy.Prompt, CodingPolicy.YoloSafe, false)]
    [InlineData(CodingPolicy.YoloSafe, CodingPolicy.Prompt, true)]
    [InlineData(CodingPolicy.YoloSafe, CodingPolicy.Yolo, false)]
    [InlineData(CodingPolicy.Yolo, CodingPolicy.YoloSafe, true)]
    [InlineData(CodingPolicy.Yolo, CodingPolicy.Yolo, true)]
    public void IsWithinCeiling_enforces_ordering(CodingPolicy ceiling, CodingPolicy requested, bool expected)
    {
        Assert.Equal(expected, CodingPolicyExtensions.IsWithinCeiling(requested, ceiling));
    }
}
