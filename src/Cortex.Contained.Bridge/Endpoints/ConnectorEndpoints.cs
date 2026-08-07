using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Pairing;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the connector management endpoints (<c>/api/connectors/*</c>): list connectors,
/// approve/deny pairing requests, enable/disable, revoke, and master toggle. Every endpoint
/// requires authorization. The connector WebSocket endpoint (<c>/connector</c>) is separate
/// and anonymous. Tokens are never surfaced — only <see cref="ConnectorProjection"/> is returned.
/// </summary>
internal static class ConnectorEndpoints
{
    /// <summary>Maps the <c>/api/connectors/*</c> endpoints onto <paramref name="app"/>.</summary>
    /// <param name="app">The web application to map onto.</param>
    public static void MapConnectorEndpoints(this WebApplication app)
    {
        // List paired connectors (token-free) plus any pending pairing requests.
        app.MapGet("/api/connectors", (
            IConnectorPairingCoordinator coordinator,
            ConnectorConfigStore configStore,
            ConnectorHost host) =>
        {
            var settings = configStore.GetSettings();
            var attachedIds = host.GetAttachedChannels()
                .Select(c => c.ChannelId)
                .ToHashSet(StringComparer.Ordinal);

            var connectors = coordinator.GetPairedConnectors()
                .Select(s => ConnectorProjection.Project(s, attachedIds.Contains(s.ChannelId), settings.Enabled))
                .ToList();

            var pending = coordinator.GetPendingRequests()
                .Select(r => new
                {
                    requestId = r.RequestId,
                    channelId = r.ChannelId,
                    key = r.Key,
                    instanceId = r.InstanceId,
                    displayName = r.DisplayName,
                    code = r.Code,
                    remoteEndpoint = r.RemoteEndpoint,
                    requestedAt = r.RequestedAt,
                    expiresAt = r.ExpiresAt,
                })
                .ToList();

            return Results.Ok(new { enabled = settings.Enabled, connectors, pending });
        }).RequireAuthorization();

        // Approve a pending pairing request (the human verified the code out-of-band).
        app.MapPost("/api/connectors/pairing/{requestId}/approve", (
            string requestId,
            IConnectorPairingCoordinator coordinator) =>
        {
            if (!IsWellFormedRequestId(requestId))
            {
                return Results.Json(new { error = "invalid pairing request id" }, statusCode: 400);
            }

            if (!TryApproveRequest(coordinator, requestId))
            {
                return Results.Json(new { error = "No such pending pairing request." }, statusCode: 404);
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // Deny a pending pairing request.
        app.MapPost("/api/connectors/pairing/{requestId}/deny", (
            string requestId,
            IConnectorPairingCoordinator coordinator) =>
        {
            if (!IsWellFormedRequestId(requestId))
            {
                return Results.Json(new { error = "invalid pairing request id" }, statusCode: 400);
            }

            if (!TryDenyRequest(coordinator, requestId))
            {
                return Results.Json(new { error = "No such pending pairing request." }, statusCode: 404);
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // Enable/disable a paired connector without losing its pairing. Disabling also drops
        // the live channel (handled by the coordinator).
        app.MapPut("/api/connectors/{channelId}", async (
            string channelId,
            ConnectorEnabledRequest request,
            IConnectorPairingCoordinator coordinator) =>
        {
            var idError = ValidateChannelId(channelId);
            if (idError is not null)
            {
                return Results.Json(new { error = idError }, statusCode: 400);
            }

            if (request?.Enabled is null)
            {
                return Results.Json(new { error = "enabled is required" }, statusCode: 400);
            }

            var updated = await coordinator.SetEnabledAsync(channelId, request.Enabled.Value).ConfigureAwait(false);
            if (!updated)
            {
                return Results.Json(new { error = $"No connector with channel id '{channelId}'." }, statusCode: 404);
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // Revoke a pairing entirely — the connector must pair again to reattach.
        app.MapDelete("/api/connectors/{channelId}", async (
            string channelId,
            IConnectorPairingCoordinator coordinator) =>
        {
            var idError = ValidateChannelId(channelId);
            if (idError is not null)
            {
                return Results.Json(new { error = idError }, statusCode: 400);
            }

            var removed = await coordinator.RevokeAsync(channelId).ConfigureAwait(false);
            if (!removed)
            {
                return Results.Json(new { error = $"No connector with channel id '{channelId}'." }, statusCode: 404);
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // Master kill-switch. Disabling persists the flag AND drops every attached channel live.
        app.MapPost("/api/connectors/toggle", async (
            ConnectorToggleRequest request,
            ConnectorConfigStore configStore,
            ConnectorHost host) =>
        {
            if (request?.Enabled is null)
            {
                return Results.Json(new { error = "enabled is required" }, statusCode: 400);
            }

            var settings = configStore.GetSettings();
            settings.Enabled = request.Enabled.Value;
            configStore.Save(settings);

            if (!settings.Enabled)
            {
                await host.DetachAllAsync("master switch disabled").ConfigureAwait(false);
            }

            return Results.Ok(new { success = true, enabled = settings.Enabled });
        }).RequireAuthorization();
    }

    /// <summary>
    /// Validates a route-supplied channel id. Returns an error message, or <see langword="null"/>
    /// when <paramref name="channelId"/> is a well-formed <c>plugin:&lt;key&gt;:&lt;instance&gt;</c> id.
    /// </summary>
    /// <param name="channelId">The untrusted, route-supplied channel id.</param>
    internal static string? ValidateChannelId(string channelId)
    {
        if (!ConnectorChannelId.IsPluginChannelId(channelId))
        {
            return "not a valid plugin channel id";
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="requestId"/> has the shape the pairing service issues
    /// (<see cref="Guid"/> in "N" format). Rejecting anything else keeps route-supplied text out
    /// of the structured log, which would otherwise be a log-injection vector.
    /// </summary>
    /// <param name="requestId">The untrusted, route-supplied request id.</param>
    internal static bool IsWellFormedRequestId(string requestId)
    {
        return Guid.TryParseExact(requestId, "N", out _);
    }

    /// <summary>Approves a pending pairing request. Returns false when the id is unknown (404).</summary>
    /// <param name="coordinator">The pairing coordinator.</param>
    /// <param name="requestId">The pending request id.</param>
    internal static bool TryApproveRequest(IConnectorPairingCoordinator coordinator, string requestId)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return coordinator.Approve(requestId);
    }

    /// <summary>Denies a pending pairing request. Returns false when the id is unknown (404).</summary>
    /// <param name="coordinator">The pairing coordinator.</param>
    /// <param name="requestId">The pending request id.</param>
    internal static bool TryDenyRequest(IConnectorPairingCoordinator coordinator, string requestId)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return coordinator.Deny(requestId, "denied");
    }
}
