using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests.Tools;

public class ProactiveMessageCollectorTests
{
    [Fact]
    public void Collected_Empty_WhenNothingAdded()
    {
        var collector = new ProactiveMessageCollector();

        Assert.Empty(collector.Collected);
    }

    [Fact]
    public void Add_ThenCollected_ReturnsInOrder()
    {
        var collector = new ProactiveMessageCollector();
        var first = new ProactiveMessageRecord { ChannelId = "voice-default", Text = "Hello" };
        var second = new ProactiveMessageRecord { ChannelId = "discord-dm", Text = "World" };

        collector.Add(first);
        collector.Add(second);

        Assert.Equal(2, collector.Collected.Count);
        Assert.Same(first, collector.Collected[0]);
        Assert.Same(second, collector.Collected[1]);
    }

    [Fact]
    public void Add_Null_Throws()
    {
        var collector = new ProactiveMessageCollector();

        Assert.Throws<ArgumentNullException>(() => collector.Add(null!));
    }
}
