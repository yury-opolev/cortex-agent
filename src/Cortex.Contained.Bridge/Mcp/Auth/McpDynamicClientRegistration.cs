using System.Text.Json;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Builds the Dynamic Client Registration (RFC 7591) request body and parses the response. Pure:
/// the HTTP POST to the registration endpoint lives in <see cref="McpOAuthManager"/>. Registers a
/// public (PKCE-only) client for the authorization-code + refresh-token grants.
/// </summary>
public static class McpDynamicClientRegistration
{
    /// <summary>
    /// Builds the RFC 7591 registration request JSON for a public client using the loopback
    /// <paramref name="redirectUri"/>. <paramref name="scope"/> is omitted entirely when null/empty.
    /// </summary>
    public static string BuildRequestJson(string clientName, string redirectUri, string? scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("client_name", clientName);

            writer.WriteStartArray("redirect_uris");
            writer.WriteStringValue(redirectUri);
            writer.WriteEndArray();

            writer.WriteStartArray("grant_types");
            writer.WriteStringValue("authorization_code");
            writer.WriteStringValue("refresh_token");
            writer.WriteEndArray();

            writer.WriteStartArray("response_types");
            writer.WriteStringValue("code");
            writer.WriteEndArray();

            // Public client: PKCE replaces a client secret on the token endpoint.
            writer.WriteString("token_endpoint_auth_method", "none");

            if (!string.IsNullOrWhiteSpace(scope))
            {
                writer.WriteString("scope", scope);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Parses a registration response. Returns null when no <c>client_id</c> is present or the
    /// document is unparseable.
    /// </summary>
    public static McpClientCredentials? ParseResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("client_id", out var clientIdElement)
                || clientIdElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var clientId = clientIdElement.GetString();
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return null;
            }

            string? clientSecret = null;
            if (root.TryGetProperty("client_secret", out var secretElement)
                && secretElement.ValueKind == JsonValueKind.String)
            {
                clientSecret = secretElement.GetString();
            }

            return new McpClientCredentials
            {
                ClientId = clientId,
                ClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
