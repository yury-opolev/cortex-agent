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
        sb.AppendLine("  media:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    enabled: {Bool(connectors.Media.Enabled)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    maxAttachmentsPerMessage: {connectors.Media.MaxAttachmentsPerMessage}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    maxAttachmentBytes: {connectors.Media.MaxAttachmentBytes}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    maxInlineBytes: {connectors.Media.MaxInlineBytes}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    handleTtl: \"{connectors.Media.HandleTtl.ToString("c", CultureInfo.InvariantCulture)}\"");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    maxStoredBytesPerConnector: {connectors.Media.MaxStoredBytesPerConnector}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    maxUploadsPerMinute: {connectors.Media.MaxUploadsPerMinute}");

        // Only emit the sequence when it is non-empty. An empty list means "use the built-in
        // defaults", and writing a bare `allowedMimeTypes:` key would round-trip that intent
        // just as well but reads as though someone deliberately allowed nothing.
        if (connectors.Media.AllowedMimeTypes is { Count: > 0 })
        {
            sb.AppendLine("    allowedMimeTypes:");
            foreach (var mimeType in connectors.Media.AllowedMimeTypes)
            {
                // MIME types are drawn from a closed allow-list of bare ASCII tokens, so they
                // need no YAML quoting; emitting them raw keeps the file readable.
                sb.AppendLine(CultureInfo.InvariantCulture, $"      - {mimeType}");
            }
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
