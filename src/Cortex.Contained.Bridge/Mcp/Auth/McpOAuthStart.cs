namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// The result of beginning an OAuth authorization: the system-browser <see cref="AuthorizationUrl"/>
/// (carrying PKCE challenge + state) and the single-use <see cref="State"/> the loopback callback
/// correlates against.
/// </summary>
public sealed record McpOAuthStart
{
    /// <summary>The authorization-endpoint URL to open in the system browser.</summary>
    public required string AuthorizationUrl { get; init; }

    /// <summary>The cryptographically random, single-use state bound to this pending request.</summary>
    public required string State { get; init; }
}
