using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Endpoints;

/// <summary>
/// Tests <see cref="SettingsEndpoints.TryApplyMaxConcurrentSubagents"/> — the reject-not-clamp
/// boundary for <c>POST /api/settings</c>'s <c>maxConcurrentSubagents</c> field. Extracted as a
/// testable seam because the full minimal-API handler pulls in TenantRouter/Worker/ModelCatalog,
/// none of which this validation logic touches.
/// </summary>
public sealed class SubagentSettingsEndpointTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void Settings_OneAndFifty_AreAccepted(int requestedValue)
    {
        var config = new BridgeConfig { MaxConcurrentSubagents = 5 };

        var accepted = SettingsEndpoints.TryApplyMaxConcurrentSubagents(config, requestedValue, out var error);

        Assert.True(accepted);
        Assert.Null(error);
        Assert.Equal(requestedValue, config.MaxConcurrentSubagents);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Settings_ZeroAndFiftyOne_Return400WithoutMutation(int requestedValue)
    {
        var config = new BridgeConfig { MaxConcurrentSubagents = 5 };

        var accepted = SettingsEndpoints.TryApplyMaxConcurrentSubagents(config, requestedValue, out var error);

        Assert.False(accepted);
        Assert.False(string.IsNullOrEmpty(error));
        // Rejected — config must be left completely untouched, not clamped into range.
        Assert.Equal(5, config.MaxConcurrentSubagents);
    }
}
