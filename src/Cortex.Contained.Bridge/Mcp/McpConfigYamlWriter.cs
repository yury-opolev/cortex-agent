using System.Globalization;
using System.Text;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Serializes the <see cref="McpSettingsConfig"/> block of the Bridge YAML. Only non-secret
/// fields are written: secret values never appear here — only the <c>secretRef</c> id and the
/// <c>${secret:id}</c> env tokens (references, not values). Extracted so the round-trip and the
/// no-secret guarantee can be unit-tested directly.
/// </summary>
internal static class McpConfigYamlWriter
{
    public static void AppendMcpSection(StringBuilder sb, McpSettingsConfig mcp)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(mcp);

        sb.AppendLine();
        sb.AppendLine("mcp:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  enabled: {Bool(mcp.Enabled)}");

        if (mcp.Servers.Count == 0)
        {
            return;
        }

        sb.AppendLine("  servers:");
        foreach (var server in mcp.Servers)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    - key: {server.Key}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      enabled: {Bool(server.Enabled)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      transport: {server.Transport.ToString().ToLowerInvariant()}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      auth: {Camel(server.Auth)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      callTimeoutSeconds: {server.CallTimeoutSeconds}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      maxResultBytes: {server.MaxResultBytes}");

            if (!string.IsNullOrWhiteSpace(server.Url))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"      url: {Quote(server.Url)}");
            }

            if (!string.IsNullOrWhiteSpace(server.Command))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"      command: {Quote(server.Command)}");
            }

            if (server.Args.Count > 0)
            {
                sb.AppendLine("      args:");
                foreach (var arg in server.Args)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        - {Quote(arg)}");
                }
            }

            if (server.Env.Count > 0)
            {
                sb.AppendLine("      env:");
                foreach (var (name, value) in server.Env)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        {name}: {Quote(value)}");
                }
            }

            if (!string.IsNullOrWhiteSpace(server.ApiKeyHeader))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"      apiKeyHeader: {Quote(server.ApiKeyHeader)}");
            }

            if (!string.IsNullOrWhiteSpace(server.SecretRef))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"      secretRef: {Quote(server.SecretRef)}");
            }

            if (server.ToolAllowList.Count > 0)
            {
                sb.AppendLine("      toolAllowList:");
                foreach (var tool in server.ToolAllowList)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        - {Quote(tool)}");
                }
            }

            if (server.MutationToolAllowList.Count > 0)
            {
                sb.AppendLine("      mutationToolAllowList:");
                foreach (var tool in server.MutationToolAllowList)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        - {Quote(tool)}");
                }
            }
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Camel(McpAuthMode mode) => mode switch
    {
        McpAuthMode.ApiKey => "apiKey",
        McpAuthMode.OAuth => "oauth",
        McpAuthMode.None => "none",
        _ => "auto",
    };

    /// <summary>Double-quotes a scalar (escaping <c>\</c> and <c>"</c>) so YAML-reserved leading characters and <c>${...}</c> tokens are preserved verbatim.</summary>
    private static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
