using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Pure parsers for the MCP OAuth 2.1 discovery chain:
/// <list type="number">
///   <item>a <c>401</c> response's <c>WWW-Authenticate</c> header → the protected-resource metadata URL;</item>
///   <item>protected-resource metadata JSON → the authorization server(s);</item>
///   <item>authorization-server / OIDC metadata JSON → the concrete endpoints + scopes.</item>
/// </list>
/// No I/O and no secrets — every method is a deterministic transform fully covered by unit tests.
/// </summary>
public static partial class McpOAuthMetadata
{
    /// <summary>
    /// Extracts the <c>resource_metadata</c> auth-param from a <c>WWW-Authenticate</c> header value
    /// (quoted or bare), or null when absent/unparseable.
    /// </summary>
    public static string? ParseResourceMetadataUrl(string? wwwAuthenticate)
    {
        if (string.IsNullOrWhiteSpace(wwwAuthenticate))
        {
            return null;
        }

        var match = ResourceMetadataRegex().Match(wwwAuthenticate);
        if (!match.Success)
        {
            return null;
        }

        var quoted = match.Groups["q"];
        var bare = match.Groups["b"];
        var value = quoted.Success ? quoted.Value : bare.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Parses protected-resource metadata JSON and returns its <c>authorization_servers</c> list
    /// (empty when absent or the document is unparseable).
    /// </summary>
    public static IReadOnlyList<string> ParseAuthorizationServers(string protectedResourceJson)
    {
        if (string.IsNullOrWhiteSpace(protectedResourceJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(protectedResourceJson);
            if (!doc.RootElement.TryGetProperty("authorization_servers", out var servers)
                || servers.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<string>(servers.GetArrayLength());
            foreach (var element in servers.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value);
                    }
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Parses authorization-server / OIDC metadata JSON into the concrete endpoints, or null when
    /// the required <c>authorization_endpoint</c> + <c>token_endpoint</c> are missing/unparseable.
    /// </summary>
    public static McpAuthServerEndpoints? ParseAuthorizationServerMetadata(string authServerJson)
    {
        if (string.IsNullOrWhiteSpace(authServerJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(authServerJson);
            var root = doc.RootElement;

            var authorizationEndpoint = GetString(root, "authorization_endpoint");
            var tokenEndpoint = GetString(root, "token_endpoint");
            if (string.IsNullOrWhiteSpace(authorizationEndpoint) || string.IsNullOrWhiteSpace(tokenEndpoint))
            {
                return null;
            }

            var registrationEndpoint = GetString(root, "registration_endpoint");

            var scopes = new List<string>();
            if (root.TryGetProperty("scopes_supported", out var scopesElement)
                && scopesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in scopesElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        var value = element.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            scopes.Add(value);
                        }
                    }
                }
            }

            return new McpAuthServerEndpoints
            {
                AuthorizationEndpoint = authorizationEndpoint,
                TokenEndpoint = tokenEndpoint,
                RegistrationEndpoint = string.IsNullOrWhiteSpace(registrationEndpoint) ? null : registrationEndpoint,
                ScopesSupported = scopes,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    [GeneratedRegex(
        "resource_metadata\\s*=\\s*(?:\"(?<q>[^\"]*)\"|(?<b>[^,\\s]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourceMetadataRegex();
}
