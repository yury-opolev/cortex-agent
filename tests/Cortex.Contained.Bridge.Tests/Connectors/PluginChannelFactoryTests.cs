using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Contracts.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class PluginChannelFactoryTests
{
    private static ChannelCapabilities StreamingCaps() => new() { SupportsStreaming = true };
    private static ChannelCapabilities NonStreamingCaps() => new() { SupportsStreaming = false };

    [Fact]
    public void Create_StreamingTrue_ReturnsStreamingPluginChannel()
    {
        var channel = PluginChannelFactory.Create("terminal", "default", StreamingCaps(), "Terminal", NullLoggerFactory.Instance);

        Assert.IsType<StreamingPluginChannel>(channel);
        Assert.IsAssignableFrom<IChannelWithStreaming>(channel);
    }

    [Fact]
    public void Create_StreamingFalse_ReturnsPlainPluginChannel()
    {
        var channel = PluginChannelFactory.Create("terminal", "default", NonStreamingCaps(), "Terminal", NullLoggerFactory.Instance);

        Assert.IsNotType<StreamingPluginChannel>(channel);
        Assert.IsNotAssignableFrom<IChannelWithStreaming>(channel);
    }

    [Fact]
    public void Create_StreamingTrue_ChannelIdIsCorrect()
    {
        var channel = PluginChannelFactory.Create("mykey", "myinstance", StreamingCaps(), "My", NullLoggerFactory.Instance);

        Assert.Equal("plugin:mykey:myinstance", channel.ChannelId);
    }

    [Fact]
    public void Create_StreamingFalse_ChannelIdIsCorrect()
    {
        var channel = PluginChannelFactory.Create("mykey", "myinstance", NonStreamingCaps(), "My", NullLoggerFactory.Instance);

        Assert.Equal("plugin:mykey:myinstance", channel.ChannelId);
    }
}
