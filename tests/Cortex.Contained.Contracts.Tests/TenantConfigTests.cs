using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Contracts.Tests;

public class TenantConfigVoicePropertiesTests
{
    [Fact]
    public void DiscordGuildId_DefaultsToNull()
    {
        var config = new TenantConfig();
        Assert.Null(config.DiscordGuildId);
    }

    [Fact]
    public void DiscordVoiceChannelId_DefaultsToNull()
    {
        var config = new TenantConfig();
        Assert.Null(config.DiscordVoiceChannelId);
    }

    [Fact]
    public void VoiceGreeting_DefaultsToNull()
    {
        var config = new TenantConfig();
        Assert.Null(config.VoiceGreeting);
    }

    [Fact]
    public void DiscordVoiceProperties_CanBeSet()
    {
        var config = new TenantConfig
        {
            DiscordGuildId = "123456789012345678",
            DiscordVoiceChannelId = "987654321098765432",
            VoiceGreeting = "Hello",
        };

        Assert.Equal("123456789012345678", config.DiscordGuildId);
        Assert.Equal("987654321098765432", config.DiscordVoiceChannelId);
        Assert.Equal("Hello", config.VoiceGreeting);
    }
}
