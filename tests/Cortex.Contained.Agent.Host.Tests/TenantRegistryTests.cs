using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests for <see cref="Cortex.Contained.Bridge.Tenants.TenantRegistry"/>.
/// Since TenantRegistry lives in the Bridge project and we're testing from
/// the Agent Host test project, we test the underlying TenantConfig model
/// and the config binding behavior.
/// </summary>
public class TenantConfigTests
{
    [Fact]
    public void TenantConfig_DefaultValues()
    {
        var config = new TenantConfig();

        Assert.Equal(string.Empty, config.Endpoint);
        Assert.False(config.Default);
        Assert.True(config.Enabled);
        Assert.Equal(0, config.Port);
        Assert.Null(config.DiscordUserId);
        Assert.Null(config.DiscordUsername);
        Assert.Null(config.SetupCode);
        Assert.Equal(0, config.SetupCodeExpiresAt);
        Assert.False(config.ApiEnabled);
        Assert.Null(config.ImageVersion);
        Assert.Equal(0, config.IdleTimeoutMinutes);
    }

    [Fact]
    public void TenantConfig_CanSetAllProperties()
    {
        var config = new TenantConfig
        {
            Endpoint = "http://localhost:5100/hub/agent",
            Default = true,
            Enabled = true,
            Port = 5100,
            DiscordUserId = "123456789",
            DiscordUsername = "Alice#1234",
            ApiEnabled = true,
            ImageVersion = "0.1.0-build1",
            IdleTimeoutMinutes = 60,
        };

        Assert.Equal("http://localhost:5100/hub/agent", config.Endpoint);
        Assert.True(config.Default);
        Assert.Equal(5100, config.Port);
        Assert.Equal("123456789", config.DiscordUserId);
        Assert.Equal("Alice#1234", config.DiscordUsername);
        Assert.True(config.ApiEnabled);
        Assert.Equal("0.1.0-build1", config.ImageVersion);
        Assert.Equal(60, config.IdleTimeoutMinutes);
    }

    [Fact]
    public void TenantConfig_MigrateLegacyDiscordUsers()
    {
        var config = new TenantConfig
        {
            DiscordUsers = ["123456789", "987654321"],
        };

        config.MigrateLegacyDiscordUsers();

        Assert.Equal("123456789", config.DiscordUserId);
        Assert.Empty(config.DiscordUsers);
    }

    [Fact]
    public void TenantConfig_MigrateLegacy_SkipsIfAlreadySet()
    {
        var config = new TenantConfig
        {
            DiscordUserId = "existing-user",
            DiscordUsers = ["should-be-ignored"],
        };

        config.MigrateLegacyDiscordUsers();

        Assert.Equal("existing-user", config.DiscordUserId);
        // Legacy list is NOT cleared when DiscordUserId was already set
        Assert.Single(config.DiscordUsers);
    }

    [Fact]
    public void BridgeConfig_TenantsSection_DefaultsToEmpty()
    {
        var config = new BridgeConfig();
        Assert.NotNull(config.Tenants);
        Assert.Empty(config.Tenants);
    }

    [Fact]
    public void BridgeConfig_TenantsSection_CaseInsensitive()
    {
        var config = new BridgeConfig();
        config.Tenants["Cortex"] = new TenantConfig { Endpoint = "http://localhost:5100" };

        Assert.True(config.Tenants.ContainsKey("cortex"));
        Assert.True(config.Tenants.ContainsKey("CORTEX"));
        Assert.Equal("http://localhost:5100", config.Tenants["cortex"].Endpoint);
    }

    [Fact]
    public void BridgeConfig_MultipleTenants()
    {
        var config = new BridgeConfig();
        config.Tenants["admin"] = new TenantConfig
        {
            Endpoint = "http://localhost:5100/hub/agent",
            Default = true,
            Port = 5100,
        };
        config.Tenants["support"] = new TenantConfig
        {
            Endpoint = "http://localhost:5101/hub/agent",
            Port = 5101,
            DiscordUserId = "111111111",
            DiscordUsername = "SupportUser#5678",
        };

        Assert.Equal(2, config.Tenants.Count);
        Assert.True(config.Tenants["admin"].Default);
        Assert.False(config.Tenants["support"].Default);
        Assert.Equal("111111111", config.Tenants["support"].DiscordUserId);
    }
}
