using Cortex.Contained.Bridge.Channels;
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Pairing;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Contracts.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Connectors;

/// <summary>
/// Pins the authorization posture of the connector surface, which is the load-bearing part of
/// the pairing security model: every <c>/api/connectors/*</c> management endpoint must require
/// authorization, because a local process that could reach them would be able to approve its own
/// pairing request and defeat the human code comparison entirely. Conversely the <c>/connector</c>
/// WebSocket endpoint must stay anonymous — connectors authenticate with a pairing token, not the
/// Web UI session cookie.
/// </summary>
public sealed class ConnectorEndpointAuthorizationTests
{
    [Fact]
    public void MapConnectorEndpoints_EveryManagementEndpoint_RequiresAuthorization()
    {
        var endpoints = MapAndCollect(app => app.MapConnectorEndpoints());

        Assert.NotEmpty(endpoints);

        foreach (var endpoint in endpoints)
        {
            var pattern = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? endpoint.DisplayName;
            Assert.True(
                endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null,
                $"Endpoint '{pattern}' is missing RequireAuthorization().");
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        }
    }

    [Fact]
    public void MapConnectorEndpoints_CoversTheFullManagementSurface()
    {
        var patterns = MapAndCollect(app => app.MapConnectorEndpoints())
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        // If an endpoint is added without authorization the test above catches it, but only if
        // it is actually mapped here — so pin the surface too.
        Assert.Contains("/api/connectors", patterns);
        Assert.Contains("/api/connectors/toggle", patterns);
        Assert.Contains("/api/connectors/{channelId}", patterns);
        Assert.Contains("/api/connectors/pairing/{requestId}/approve", patterns);
        Assert.Contains("/api/connectors/pairing/{requestId}/deny", patterns);
    }

    [Fact]
    public void MapConnectorEndpoint_WebSocketEndpoint_StaysAnonymous()
    {
        var endpoint = Assert.Single(MapAndCollect(app => app.MapConnectorEndpoint()));

        Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
    }

    private static IReadOnlyList<Endpoint> MapAndCollect(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();

        // Minimal APIs infer an unregistered complex parameter as a request BODY, which is
        // illegal on GET/DELETE and throws while the endpoints are built. Registering the same
        // services Program.cs registers means this test also proves the endpoint signatures can
        // actually be constructed at startup.
        var bridgeConfig = new BridgeConfig();
        builder.Services.AddSingleton(bridgeConfig);
        builder.Services.AddSingleton(Substitute.For<IConnectorPairingCoordinator>());
        builder.Services.AddSingleton(new ConnectorConfigStore(
            bridgeConfig,
            Path.Combine(Path.GetTempPath(), "connector-auth-tests.yml"),
            NullLogger<ConnectorConfigStore>.Instance));
        builder.Services.AddSingleton(new ConnectorHost(
            new ChannelManager(NullLogger<ChannelManager>.Instance),
            bridgeConfig,
            NullLogger<ConnectorHost>.Instance));

        var app = builder.Build();
        map(app);

        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)];
    }
}
