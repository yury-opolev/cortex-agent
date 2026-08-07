using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests;

public class ChannelCatalogTests
{
    // ── ResolveCanonical — first-party ────────────────────────────────────

    [Fact]
    public void ResolveCanonical_KnownAlias_ReturnsCanonicalId()
    {
        var result = ChannelCatalog.ResolveCanonical("webchat");

        Assert.Equal("webchat-default", result);
    }

    [Fact]
    public void ResolveCanonical_CanonicalIdAsAlias_ReturnsSelf()
    {
        var result = ChannelCatalog.ResolveCanonical("webchat-default");

        Assert.Equal("webchat-default", result);
    }

    // ── ResolveCanonical — plugin channels ────────────────────────────────

    [Fact]
    public void ResolveCanonical_ValidPluginId_ReturnsSelf()
    {
        var result = ChannelCatalog.ResolveCanonical("plugin:terminal:default");

        Assert.Equal("plugin:terminal:default", result);
    }

    [Fact]
    public void ResolveCanonical_InvalidPluginId_UpperCase_ReturnsNull()
    {
        var result = ChannelCatalog.ResolveCanonical("plugin:BAD:x");

        Assert.Null(result);
    }

    [Fact]
    public void ResolveCanonical_Unknown_ReturnsNull()
    {
        var result = ChannelCatalog.ResolveCanonical("scheduled");

        Assert.Null(result);
    }

    // ── ByCanonicalId — first-party ───────────────────────────────────────

    [Fact]
    public void ByCanonicalId_KnownId_ReturnsDescriptor()
    {
        var descriptor = ChannelCatalog.ByCanonicalId("webchat-default");

        Assert.NotNull(descriptor);
        Assert.Equal("webchat-default", descriptor.CanonicalId);
        Assert.Equal("webchat", descriptor.FriendlyName);
    }

    // ── ByCanonicalId — plugin channels ───────────────────────────────────

    [Fact]
    public void ByCanonicalId_ValidPluginId_ReturnsSynthesisedDescriptor()
    {
        var descriptor = ChannelCatalog.ByCanonicalId("plugin:terminal:default");

        Assert.NotNull(descriptor);
        Assert.Equal("plugin:terminal:default", descriptor.CanonicalId);
        Assert.Equal("plugin:terminal:default", descriptor.FriendlyName);
        Assert.Equal("Connector (terminal)", descriptor.DisplayName);
        Assert.Equal("the terminal connector", descriptor.PromptLabel);
        Assert.Contains("plugin:terminal:default", descriptor.Aliases);
    }

    [Fact]
    public void ByCanonicalId_InvalidPluginId_UpperCase_ReturnsNull()
    {
        var descriptor = ChannelCatalog.ByCanonicalId("plugin:BAD:x");

        Assert.Null(descriptor);
    }

    [Fact]
    public void ByCanonicalId_Null_ReturnsNull()
    {
        var descriptor = ChannelCatalog.ByCanonicalId(null);

        Assert.Null(descriptor);
    }

    [Fact]
    public void ByCanonicalId_Unknown_ReturnsNull()
    {
        var descriptor = ChannelCatalog.ByCanonicalId("scheduled");

        Assert.Null(descriptor);
    }

    // ── All array is unaffected by plugin support ─────────────────────────

    [Fact]
    public void All_DoesNotContainPluginEntries()
    {
        Assert.DoesNotContain(ChannelCatalog.All,
            d => d.CanonicalId.StartsWith("plugin:", StringComparison.Ordinal));
    }
}
