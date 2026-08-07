using System.Net;
using Cortex.Contained.Bridge.Connectors.Replay;
using Cortex.Contained.Contracts.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Minimal-API endpoint that upgrades <c>GET /connector</c> to a WebSocket
/// connector session.
/// </summary>
public static class ConnectorEndpoint
{
    /// <summary>
    /// Maps the <c>/connector</c> WebSocket endpoint on <paramref name="app"/>.
    /// Must be placed at the top level — NOT inside the <c>if (webui enabled)</c>
    /// block — because connectors are independent of the Web UI.
    /// </summary>
    public static void MapConnectorEndpoint(this WebApplication app)
    {
        // AllowAnonymous is required here because AddAuthorization() is called
        // unconditionally, and UseAuthorization() is added inside the WebUI block;
        // including it now ensures the endpoint is accessible whether or not the
        // WebUI (and its authorization middleware) is enabled.
        app.Map("/connector", async (HttpContext context) =>
        {
            var bridgeConfig = context.RequestServices.GetRequiredService<BridgeConfig>();
            var settings = bridgeConfig.Connectors;

            // Loopback guard, applied before the socket is accepted. Kestrel binding to
            // WebUi.BindAddress (127.0.0.1 by default) is the primary control; this check is
            // defence in depth for deployments that widen the bind address to expose the Web UI.
            var remoteAddress = context.Connection.RemoteIpAddress;
            if (!IsLoopbackPeer(remoteAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Connector endpoint is only accessible from localhost.").ConfigureAwait(false);
                return;
            }

            // Must be a WebSocket upgrade request.
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("This endpoint requires a WebSocket connection.").ConfigureAwait(false);
                return;
            }

            // Reject when the connector subsystem is disabled.
            if (!settings.Enabled)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Connector subsystem is disabled.").ConfigureAwait(false);
                return;
            }

            var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var remoteEndpoint = remoteAddress?.ToString() ?? "unknown";

            // Second, independent loopback check after the socket is accepted.
            // A proxy that rewrites RemoteIpAddress late (between the first check and socket
            // acceptance) would bypass the pre-accept guard; this catches that case cheaply.
            // NOTE: UseForwardedHeaders is NOT configured in Program.cs for this application,
            // so X-Forwarded-For cannot influence RemoteIpAddress. If that ever changes and
            // forwarded-header middleware is added, this post-accept check (using the real
            // TCP peer address at time of socket acceptance) remains the authoritative guard.
            // We do NOT add an X-Forwarded-For rejection here because it is not reachable in
            // the current configuration, and dead code would be misleading. If the middleware
            // is ever enabled, revisit this comment.
            var postAcceptAddress = context.Connection.RemoteIpAddress;
            if (!IsLoopbackPeer(postAcceptAddress))
            {
                await socket.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                    "Non-loopback peer.",
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var transport = new WebSocketConnectorTransport(socket, remoteEndpoint, settings.Limits.MaxFrameBytes);

            var authenticator = context.RequestServices.GetRequiredService<IConnectorAuthenticator>();
            var registry = context.RequestServices.GetRequiredService<IConnectorRegistry>();
            var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
            var timeProvider = context.RequestServices.GetRequiredService<TimeProvider>();
            var abortDispatcher = context.RequestServices.GetRequiredService<IConnectorAbortDispatcher>();
            var replaySource = context.RequestServices.GetRequiredService<IConnectorReplaySource>();

            var session = new ConnectorSession(transport, authenticator, settings, registry, loggerFactory, timeProvider, abortDispatcher, replaySource);
            await session.RunAsync(context.RequestAborted).ConfigureAwait(false);
        }).AllowAnonymous();
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="remoteAddress"/> is a
    /// loopback address (<c>127.0.0.1</c>, <c>::1</c>, or an IPv4-mapped IPv6
    /// loopback such as <c>::ffff:127.0.0.1</c>).
    /// A null address (e.g. in-process / Unix socket) is rejected.
    /// </summary>
    public static bool IsLoopbackPeer(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        var addr = remoteAddress.IsIPv4MappedToIPv6
            ? remoteAddress.MapToIPv4()
            : remoteAddress;

        return IPAddress.IsLoopback(addr);
    }
}
