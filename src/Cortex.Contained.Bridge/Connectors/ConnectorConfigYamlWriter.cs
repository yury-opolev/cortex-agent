using System.Globalization;
using System.Text;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Serializes the <see cref="ConnectorSettingsConfig"/> block of the Bridge YAML. The Web UI only
/// flips <c>enabled</c>, but every field is written so a save never silently drops a hand-configured
/// policy or limit. Nothing secret lives in this section — connector tokens are held in DPAPI by
/// <c>ConnectorTokenStore</c> and never appear in YAML. Extracted so the round-trip can be
/// unit-tested directly.
/// </summary>
internal static class ConnectorConfigYamlWriter
{
    /// <summary>Appends the <c>connectors:</c> section to <paramref name="sb"/>.</summary>
    /// <param name="sb">The YAML builder being populated.</param>
    /// <param name="connectors">The connector settings to serialize.</param>
    public static void AppendConnectorsSection(StringBuilder sb, ConnectorSettingsConfig connectors)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(connectors);

        sb.AppendLine();
        sb.AppendLine("connectors:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  enabled: {Bool(connectors.Enabled)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  requireApproval: {Bool(connectors.RequireApproval)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  maxConnectors: {connectors.MaxConnectors}");
        sb.AppendLine("  replay:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    maxMessages: {connectors.Replay.MaxMessages}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    maxAge: \"{connectors.Replay.MaxAge.ToString("c", CultureInfo.InvariantCulture)}\"");
        sb.AppendLine("  limits:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    maxFrameBytes: {connectors.Limits.MaxFrameBytes}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    maxMessagesPerMinute: {connectors.Limits.MaxMessagesPerMinute}");
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
