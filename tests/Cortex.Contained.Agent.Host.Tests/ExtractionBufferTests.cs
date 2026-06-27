using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests;

public class ExtractionBufferTests
{
    private static ExtractionEntry MakeEntry(string role, string content) => new()
    {
        Role = role,
        Content = content,
        Timestamp = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void AppendAndDrain_ReturnsAllEntriesAndClears()
    {
        var session = new AgentSession("conv-1");

        session.AppendToExtractionBuffer(MakeEntry("user", "hello"));
        session.AppendToExtractionBuffer(MakeEntry("assistant", "hi there"));

        Assert.Equal(2, session.ExtractionBufferCount);

        var drained = session.DrainExtractionBuffer();

        Assert.Equal(2, drained.Count);
        Assert.Equal("user", drained[0].Role);
        Assert.Equal("hello", drained[0].Content);
        Assert.Equal("assistant", drained[1].Role);
        Assert.Equal(0, session.ExtractionBufferCount);
    }

    [Fact]
    public void Drain_WhenEmpty_ReturnsEmptyList()
    {
        var session = new AgentSession("conv-1");

        var drained = session.DrainExtractionBuffer();

        Assert.Empty(drained);
    }

    [Fact]
    public void Append_AtCapacity_DoesNotExceedLimit()
    {
        var session = new AgentSession("conv-1");

        // MaxExtractionBufferSize is 100
        for (int i = 0; i < 110; i++)
        {
            session.AppendToExtractionBuffer(MakeEntry("user", $"msg-{i}"));
        }

        Assert.Equal(100, session.ExtractionBufferCount);
    }

}
