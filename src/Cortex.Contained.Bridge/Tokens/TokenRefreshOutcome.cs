namespace Cortex.Contained.Bridge.Tokens;

/// <summary>
/// Result of a successful token-refresh exchange performed by an
/// <see cref="ITokenRefreshStrategy"/>. The service uses this to persist and apply
/// the new tokens; failures are surfaced as thrown exceptions, not this type.
/// </summary>
internal sealed record TokenRefreshOutcome
{
    /// <summary>Fresh access token (the value the agent uses as its bearer credential).</summary>
    public required string AccessToken { get; init; }

    /// <summary>
    /// Fresh refresh token, or <c>null</c> when the scheme has no rotating refresh
    /// token (e.g. GitHub Copilot, whose access token is minted from a long-lived PAT).
    /// </summary>
    public string? RefreshToken { get; init; }

    /// <summary>Unix ms when <see cref="AccessToken"/> expires. <c>0</c> = unknown.</summary>
    public long ExpiresAtMs { get; init; }
}
