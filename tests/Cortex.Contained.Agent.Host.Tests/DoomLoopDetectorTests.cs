using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

public class DoomLoopDetectorTests
{
    [Fact]
    public void Check_ThreeIdenticalCalls_ReturnsTrue()
    {
        var detector = new DoomLoopDetector();
        var call = new LlmToolCall { Id = "1", Name = "bash", Arguments = "{\"cmd\":\"ls\"}" };

        Assert.False(detector.Check([call]));  // 1st
        Assert.False(detector.Check([call]));  // 2nd
        Assert.True(detector.Check([call]));   // 3rd — doom loop
    }

    [Fact]
    public void Check_DifferentArgs_ResetsCount()
    {
        var detector = new DoomLoopDetector();
        var call1 = new LlmToolCall { Id = "1", Name = "bash", Arguments = "{\"cmd\":\"ls\"}" };
        var call2 = new LlmToolCall { Id = "2", Name = "bash", Arguments = "{\"cmd\":\"pwd\"}" };

        Assert.False(detector.Check([call1]));
        Assert.False(detector.Check([call1]));
        Assert.False(detector.Check([call2]));  // different args — resets
        Assert.False(detector.Check([call2]));
    }

    [Fact]
    public void Check_DifferentToolName_ResetsCount()
    {
        var detector = new DoomLoopDetector();
        var call1 = new LlmToolCall { Id = "1", Name = "bash", Arguments = "{}" };
        var call2 = new LlmToolCall { Id = "2", Name = "read_file", Arguments = "{}" };

        Assert.False(detector.Check([call1]));
        Assert.False(detector.Check([call1]));
        Assert.False(detector.Check([call2]));  // different tool — resets
    }

    [Fact]
    public void Check_MultipleDifferentCalls_NoDoomLoop()
    {
        var detector = new DoomLoopDetector();

        for (int i = 0; i < 100; i++)
        {
            var call = new LlmToolCall { Id = $"{i}", Name = "bash", Arguments = $"{{\"cmd\":\"{i}\"}}" };
            Assert.False(detector.Check([call]));
        }
    }

    [Fact]
    public void Check_BatchOfIdenticalCalls_DetectedWithinBatch()
    {
        var detector = new DoomLoopDetector();
        var call = new LlmToolCall { Id = "1", Name = "bash", Arguments = "{\"cmd\":\"ls\"}" };

        // 3 identical calls in a single batch
        Assert.True(detector.Check([call, call, call]));
    }

    [Fact]
    public void Check_BatchOfTwoThenOneMore_Detected()
    {
        var detector = new DoomLoopDetector();
        var call = new LlmToolCall { Id = "1", Name = "bash", Arguments = "{\"cmd\":\"ls\"}" };

        Assert.False(detector.Check([call, call]));  // 2 in batch
        Assert.True(detector.Check([call]));          // 3rd — triggers
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var detector = new DoomLoopDetector();
        var call = new LlmToolCall { Id = "1", Name = "bash", Arguments = "{\"cmd\":\"ls\"}" };

        detector.Check([call]);
        detector.Check([call]);
        detector.Reset();
        Assert.False(detector.Check([call]));  // reset — starts fresh
        Assert.False(detector.Check([call]));
    }
}
