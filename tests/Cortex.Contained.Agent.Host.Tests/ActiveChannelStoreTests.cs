using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests;

public class ActiveChannelStoreTests
{
    [Fact]
    public void Get_BeforeSet_ReturnsEmptyList()
    {
        var store = new ActiveChannelStore();

        var result = store.Get();

        Assert.Empty(result);
    }

    [Fact]
    public void Set_ThenGet_ReturnsSameChannels()
    {
        var store = new ActiveChannelStore();
        var channels = new[] { "webchat-default", "discord-dm" };

        store.Set(channels);

        var result = store.Get();
        Assert.Equal(2, result.Count);
        Assert.Equal("webchat-default", result[0]);
        Assert.Equal("discord-dm", result[1]);
    }

    [Fact]
    public void Set_OverwritesPreviousValue()
    {
        var store = new ActiveChannelStore();

        store.Set(["webchat-default", "discord-dm", "voice-default"]);
        store.Set(["webchat-default"]);

        var result = store.Get();
        Assert.Single(result);
        Assert.Equal("webchat-default", result[0]);
    }

    [Fact]
    public void Set_EmptyArray_ClearsChannels()
    {
        var store = new ActiveChannelStore();

        store.Set(["webchat-default"]);
        store.Set([]);

        Assert.Empty(store.Get());
    }

    [Fact]
    public void Get_ReturnsSameReferenceUntilNextSet()
    {
        var store = new ActiveChannelStore();
        store.Set(["webchat-default"]);

        var first = store.Get();
        var second = store.Get();

        Assert.Same(first, second);
    }

    [Fact]
    public void Set_NewArray_ReturnsDifferentReference()
    {
        var store = new ActiveChannelStore();

        store.Set(["webchat-default"]);
        var first = store.Get();

        store.Set(["webchat-default"]);
        var second = store.Get();

        Assert.NotSame(first, second);
    }
}
