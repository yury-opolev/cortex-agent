using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests;

public class TenantRouterTests : IAsyncDisposable
{
    private readonly TenantRouter _router;

    public TenantRouterTests()
    {
        var config = new BridgeConfig();
        var registry = new TenantRegistry(
            config,
            () => { },
            NullLogger<TenantRegistry>.Instance);
        _router = new TenantRouter(
            registry,
            NullLoggerFactory.Instance,
            NullLogger<TenantRouter>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _router.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void OnClientConnected_Setter_StoresDelegate()
    {
        var callbackInvoked = false;
        _router.OnClientConnected = (_, _) => callbackInvoked = true;

        Assert.NotNull(_router.OnClientConnected);
        Assert.False(callbackInvoked);
    }

    [Fact]
    public void GetClient_UnregisteredTenant_ReturnsNull()
    {
        var client = _router.GetClient("nonexistent-tenant");

        Assert.Null(client);
    }

    [Fact]
    public async Task DisconnectTenantAsync_UnknownTenant_DoesNotThrow()
    {
        await _router.DisconnectTenantAsync("nonexistent-tenant");
    }

    [Fact]
    public void GetDefaultClient_EmptyConfig_CreatesDefaultTenant()
    {
        // TenantRegistry auto-creates a default tenant when none are configured.
        // GetDefaultClient returns a (disconnected) HubClient for it.
        var client = _router.GetDefaultClient();

        Assert.NotNull(client);
        Assert.False(client!.IsConnected);
    }

    [Fact]
    public void GetConnectedTenantIds_Empty_ReturnsEmptyList()
    {
        var ids = _router.GetConnectedTenantIds();

        Assert.Empty(ids);
    }

    [Fact]
    public void IsConnected_UnknownTenant_ReturnsFalse()
    {
        Assert.False(_router.IsConnected("unknown"));
    }
}
