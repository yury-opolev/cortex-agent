using Cortex.Contained.Bridge.Connectors.Security;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Pure projection from a token-free <see cref="ConnectorSummary"/> plus live attach state to the
/// shape returned by <c>GET /api/connectors</c>. Centralizes the status-label computation as a
/// fully unit-testable seam, mirroring <c>McpServerProjection</c>.
/// </summary>
/// <remarks>
/// SECURITY: no token field can ever appear here by construction — <see cref="ConnectorSummary"/>
/// carries no token (only <see cref="ConnectorRecord"/> does, and that type never leaves
/// <see cref="ConnectorTokenStore"/> and the pairing service).
/// </remarks>
public static class ConnectorProjection
{
    /// <summary>Projects one paired connector into its Web-UI view.</summary>
    /// <param name="summary">The token-free stored connector summary.</param>
    /// <param name="attached">Whether the connector currently has a live plugin channel attached.</param>
    /// <param name="masterEnabled">The connector subsystem master switch.</param>
    /// <returns>An anonymous object carrying only non-secret connector metadata.</returns>
    public static object Project(ConnectorSummary summary, bool attached, bool masterEnabled)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new
        {
            channelId = summary.ChannelId,
            key = summary.Key,
            instanceId = summary.InstanceId,
            displayName = summary.DisplayName,
            pairedAt = summary.PairedAt,
            lastSeenAt = summary.LastSeenAt,
            enabled = summary.Enabled,
            attached,
            status = StatusLabel(summary, attached, masterEnabled),
        };
    }

    /// <summary>Computes the UI status label for a paired connector.</summary>
    /// <param name="summary">The token-free stored connector summary.</param>
    /// <param name="attached">Whether the connector currently has a live plugin channel attached.</param>
    /// <param name="masterEnabled">The connector subsystem master switch.</param>
    /// <returns><c>disabled</c>, <c>connected</c>, or <c>offline</c>.</returns>
    public static string StatusLabel(ConnectorSummary summary, bool attached, bool masterEnabled)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!masterEnabled || !summary.Enabled)
        {
            return "disabled";
        }

        return attached ? "connected" : "offline";
    }
}
