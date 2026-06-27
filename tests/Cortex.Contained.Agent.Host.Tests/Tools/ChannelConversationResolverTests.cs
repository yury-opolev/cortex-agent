namespace Cortex.Contained.Agent.Host.Tests.Tools;

using Cortex.Contained.Agent.Host.Tools;

public sealed class ChannelConversationResolverTests
{
    private static readonly ChannelConversationResolver Sut = new ChannelConversationResolver();

    [Fact]
    public void ResolveConversationId_DiscordVoice_AppendsTenantId()
    {
        Assert.Equal("discord-voice-default", Sut.ResolveConversationId("discord-voice", "default"));
        Assert.Equal("discord-voice-acme", Sut.ResolveConversationId("discord-voice", "acme"));
    }

    [Fact]
    public void ResolveConversationId_DiscordDm_ReturnsChannelIdUnchanged()
    {
        Assert.Equal("discord-dm", Sut.ResolveConversationId("discord-dm", "default"));
    }

    [Fact]
    public void ResolveConversationId_DiscordGuild_ReturnsChannelIdUnchanged()
    {
        Assert.Equal("discord-guild", Sut.ResolveConversationId("discord-guild", "default"));
    }

    [Fact]
    public void ResolveConversationId_Webchat_ReturnsChannelIdUnchanged()
    {
        Assert.Equal("webchat-default", Sut.ResolveConversationId("webchat-default", "default"));
    }

    [Fact]
    public void ParseConversationId_DiscordVoiceWithSuffix_ReturnsChannelAndTenant()
    {
        var (channel, tenant) = Sut.ParseConversationId("discord-voice-default");
        Assert.Equal("discord-voice", channel);
        Assert.Equal("default", tenant);
    }

    [Fact]
    public void ParseConversationId_DiscordVoiceWithCustomTenant_ReturnsCorrectTenant()
    {
        var (channel, tenant) = Sut.ParseConversationId("discord-voice-acme");
        Assert.Equal("discord-voice", channel);
        Assert.Equal("acme", tenant);
    }

    [Fact]
    public void ParseConversationId_NonVoiceConversation_ReturnsChannelIdNoTenant()
    {
        var (channel, tenant) = Sut.ParseConversationId("discord-dm");
        Assert.Equal("discord-dm", channel);
        Assert.Null(tenant);
    }

    [Fact]
    public void ParseConversationId_Webchat_ReturnsChannelIdNoTenant()
    {
        var (channel, tenant) = Sut.ParseConversationId("webchat-default");
        Assert.Equal("webchat-default", channel);
        Assert.Null(tenant);
    }
}
