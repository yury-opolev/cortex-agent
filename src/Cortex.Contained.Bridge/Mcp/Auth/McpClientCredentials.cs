namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// The client credentials issued by Dynamic Client Registration (RFC 7591) or pre-configured by
/// the user. A public client has no <see cref="ClientSecret"/>. The secret, when present, is a
/// credential and is stored only in DPAPI — never in config or logs.
/// </summary>
public sealed record McpClientCredentials
{
    /// <summary>The registered OAuth client id.</summary>
    public required string ClientId { get; init; }

    /// <summary>The client secret for a confidential client, or null for a public (PKCE-only) client.</summary>
    public string? ClientSecret { get; init; }
}
