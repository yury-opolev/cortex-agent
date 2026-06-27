using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Speech;

namespace Cortex.Contained.Channels.Discord.Tests;

public class DiscordChannelTests
{
    [Fact]
    public void ChannelId_ReturnsDmChannelId()
    {
        var options = new DiscordChannelOptions { BotToken = "fake-token" };
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<DiscordChannel>>();
        var channel = new DiscordChannel(logger, options);

        Assert.Equal("discord-dm", channel.ChannelId);
    }

    [Fact]
    public void Type_ReturnsDiscord()
    {
        var options = new DiscordChannelOptions { BotToken = "fake-token" };
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<DiscordChannel>>();
        var channel = new DiscordChannel(logger, options);

        Assert.Equal(ChannelType.Discord, channel.Type);
    }

    [Fact]
    public void Status_InitiallyDisconnected()
    {
        var options = new DiscordChannelOptions { BotToken = "fake-token" };
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<DiscordChannel>>();
        var channel = new DiscordChannel(logger, options);

        Assert.Equal(ChannelStatus.Disconnected, channel.Status);
    }

    [Fact]
    public void Capabilities_SupportsExpectedFeatures()
    {
        var options = new DiscordChannelOptions { BotToken = "fake-token" };
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<DiscordChannel>>();
        var channel = new DiscordChannel(logger, options);

        Assert.True(channel.Capabilities.SupportsMedia);
        Assert.True(channel.Capabilities.SupportsRichText);
        Assert.True(channel.Capabilities.SupportsGroups);
        Assert.True(channel.Capabilities.SupportsEditing);
        Assert.True(channel.Capabilities.SupportsDeletion);
        Assert.True(channel.Capabilities.SupportsReactions);
        Assert.True(channel.Capabilities.SupportsStreaming);
        Assert.Equal(2000, channel.Capabilities.MaxMessageLength);
    }

    [Fact]
    public async Task SendMessageAsync_WhenDisconnected_ReturnsFailure()
    {
        var options = new DiscordChannelOptions { BotToken = "fake-token" };
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<DiscordChannel>>();
        var channel = new DiscordChannel(logger, options);

        var result = await channel.SendMessageAsync(new Cortex.Contained.Contracts.Messages.OutboundMessage
        {
            MessageId = "test",
            ConversationId = "discord-dm",
            ChannelId = "discord-dm",
            Content = new Cortex.Contained.Contracts.Messages.MessageContent { Text = "Hello" },
        });

        Assert.False(result.Success);
        Assert.Contains("not connected", result.ErrorMessage);
    }

    [Fact]
    public void DmChannelId_Constant_IsDiscordDm()
    {
        Assert.Equal("discord-dm", DiscordChannel.DmChannelId);
    }

    [Fact]
    public void GuildChannelId_Constant_IsDiscordGuild()
    {
        Assert.Equal("discord-guild", DiscordChannel.GuildChannelId);
    }

    [Fact]
    public void ChunkText_ShortText_ReturnsSingleChunk()
    {
        var result = DiscordChannel.ChunkText("Hello world", 2000);
        Assert.Single(result);
        Assert.Equal("Hello world", result[0]);
    }

    [Fact]
    public void ChunkText_LongText_SplitsAtNewline()
    {
        var line1 = new string('a', 1500);
        var line2 = new string('b', 1500);
        var text = $"{line1}\n{line2}";

        var result = DiscordChannel.ChunkText(text, 2000);
        Assert.Equal(2, result.Count);
        Assert.Equal(line1, result[0]);
        Assert.Equal(line2, result[1]);
    }

    [Fact]
    public void ChunkText_ExactLength_ReturnsSingleChunk()
    {
        var text = new string('x', 2000);
        var result = DiscordChannel.ChunkText(text, 2000);
        Assert.Single(result);
    }
}

public class DiscordChannelOptionsTests
{
    [Fact]
    public void DefaultOptions_DmVoiceTranscriptionFalse()
    {
        var options = new DiscordChannelOptions { BotToken = "token" };
        Assert.False(options.DmVoiceTranscription);
    }

    [Fact]
    public void DmVoiceTranscription_CanBeSetTrue()
    {
        var options = new DiscordChannelOptions { BotToken = "token", DmVoiceTranscription = true };
        Assert.True(options.DmVoiceTranscription);
    }

    [Fact]
    public void DefaultOptions_DmVoiceReplyModeText()
    {
        var options = new DiscordChannelOptions { BotToken = "token" };
        Assert.Equal("text", options.DmVoiceReplyMode);
    }

    [Fact]
    public void DmVoiceReplyMode_CanBeSetToVoice()
    {
        var options = new DiscordChannelOptions { BotToken = "token", DmVoiceReplyMode = "voice" };
        Assert.Equal("voice", options.DmVoiceReplyMode);
    }

    [Fact]
    public void FullConfig_AllPropertiesSet()
    {
        var options = new DiscordChannelOptions
        {
            BotToken = "my-token",
            DmVoiceTranscription = true,
            DmVoiceReplyMode = "voice",
            SilenceTimeoutMs = 2000,
            EnableBargeIn = false,
        };

        Assert.Equal("my-token", options.BotToken);
        Assert.True(options.DmVoiceTranscription);
        Assert.Equal("voice", options.DmVoiceReplyMode);
        Assert.Equal(2000, options.SilenceTimeoutMs);
        Assert.False(options.EnableBargeIn);
    }
}

public class DiscordChannelOptionsSlimTests
{
    [Fact]
    public void DefaultOptions_HasExpectedProperties()
    {
        var options = new DiscordChannelOptions { BotToken = "token" };

        // These should still exist
        Assert.Equal("token", options.BotToken);
        Assert.False(options.DmVoiceTranscription);
        Assert.Equal("text", options.DmVoiceReplyMode);
        Assert.Equal(1500, options.SilenceTimeoutMs);
        Assert.True(options.EnableBargeIn);
        Assert.True(options.UseTurnDetector);
    }

    [Fact]
    public void Options_DoesNotHaveGuildIdProperty()
    {
        // Verify GuildId, GuildTextChannelId, VoiceChannelId, EnableVoice, VoiceGreeting
        // are no longer on the type (compile-time check — this test just documents intent)
        var type = typeof(DiscordChannelOptions);
        Assert.Null(type.GetProperty("GuildId"));
        Assert.Null(type.GetProperty("GuildTextChannelId"));
        Assert.Null(type.GetProperty("VoiceChannelId"));
        Assert.Null(type.GetProperty("EnableVoice"));
        Assert.Null(type.GetProperty("VoiceGreeting"));
    }
}

public class DiscordChannelMultiVoiceTests
{
    private static DiscordChannel CreateChannel(
        DiscordChannelOptions? options = null,
        ISpeechToText? stt = null,
        ITextToSpeech? tts = null,
        System.Net.Http.HttpClient? httpClient = null)
    {
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<DiscordChannel>>();
        options ??= new DiscordChannelOptions { BotToken = "fake-token" };
        return new DiscordChannel(logger, options, stt, tts, httpClient);
    }

    [Fact]
    public void Constructor_NoVoiceHandlersInitially()
    {
        var channel = CreateChannel();
        // Channel should work without any voice handlers
        Assert.Equal(ChannelStatus.Disconnected, channel.Status);
    }

    [Fact]
    public async Task SendStreamingUpdateAsync_NoVoiceHandlers_DoesNotThrow()
    {
        var channel = CreateChannel();
        // Should be a no-op when no voice handlers exist
        await channel.SendStreamingUpdateAsync("some-conversation", "partial text");
    }

    [Fact]
    public async Task ReconcileVoiceHandlers_WithoutSpeechServices_Skips()
    {
        var channel = CreateChannel(stt: null, tts: null);
        var configs = new Dictionary<string, VoiceHandlerConfig>();
        // Should not throw when speech services are unavailable
        await channel.ReconcileVoiceHandlers(configs);
    }

    [Fact]
    public void Constructor_WithSpeechServices_SetsProperties()
    {
        var stt = Substitute.For<ISpeechToText>();
        var tts = Substitute.For<ITextToSpeech>();
        var httpClient = new System.Net.Http.HttpClient();

        var channel = CreateChannel(stt: stt, tts: tts, httpClient: httpClient);

        Assert.Equal("discord-dm", channel.ChannelId);
        Assert.Equal(ChannelType.Discord, channel.Type);
        Assert.Equal(ChannelStatus.Disconnected, channel.Status);

        httpClient.Dispose();
    }

    [Fact]
    public void Constructor_WithNullSpeechServices_StillWorks()
    {
        var channel = CreateChannel(stt: null, tts: null, httpClient: null);

        Assert.Equal("discord-dm", channel.ChannelId);
        Assert.Equal(ChannelType.Discord, channel.Type);
    }

    [Fact]
    public void Capabilities_IncludesAudioOgg()
    {
        var channel = CreateChannel();

        Assert.Contains("audio/ogg", channel.Capabilities.SupportedMediaTypes);
        Assert.Contains("audio/mpeg", channel.Capabilities.SupportedMediaTypes);
    }

    [Fact]
    public async Task SendMessageAsync_WhenDisconnected_WithSpeechServices_ReturnsFailure()
    {
        var stt = Substitute.For<ISpeechToText>();
        var tts = Substitute.For<ITextToSpeech>();
        var httpClient = new System.Net.Http.HttpClient();

        var channel = CreateChannel(stt: stt, tts: tts, httpClient: httpClient);

        var result = await channel.SendMessageAsync(new Cortex.Contained.Contracts.Messages.OutboundMessage
        {
            MessageId = "test",
            ConversationId = "discord-dm",
            ChannelId = "discord-dm",
            Content = new Cortex.Contained.Contracts.Messages.MessageContent { Text = "Hello" },
        });

        Assert.False(result.Success);
        Assert.Contains("not connected", result.ErrorMessage);

        httpClient.Dispose();
    }

    [Fact]
    public void OldConstructor_StillWorks()
    {
        // Verify backward compatibility — old constructor without speech services
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<DiscordChannel>>();
        var options = new DiscordChannelOptions { BotToken = "fake-token" };
        var channel = new DiscordChannel(logger, options);

        Assert.Equal("discord-dm", channel.ChannelId);
        Assert.Equal(ChannelStatus.Disconnected, channel.Status);
    }
}

public class DiscordChannelVoiceRoutingTests
{
    [Theory]
    [InlineData("discord-voice-default", "default")]
    [InlineData("discord-voice-tenant1", "tenant1")]
    [InlineData("discord-voice-tenant-with-hyphens", "tenant-with-hyphens")]
    [InlineData("discord-voice-DEFAULT", "DEFAULT")]
    public void TryGetVoiceTenantId_ValidFormat_ParsesTenantId(string conversationId, string expectedTenantId)
    {
        var result = DiscordChannel.TryGetVoiceTenantId(conversationId, out var tenantId);

        Assert.True(result);
        Assert.Equal(expectedTenantId, tenantId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("discord-voice-")]
    [InlineData("discord-dm")]
    [InlineData("discord-guild")]
    [InlineData("12345")]
    [InlineData("voice-default")]
    public void TryGetVoiceTenantId_InvalidFormat_ReturnsFalse(string? conversationId)
    {
        var result = DiscordChannel.TryGetVoiceTenantId(conversationId, out var tenantId);

        Assert.False(result);
        Assert.Equal(string.Empty, tenantId);
    }
}
