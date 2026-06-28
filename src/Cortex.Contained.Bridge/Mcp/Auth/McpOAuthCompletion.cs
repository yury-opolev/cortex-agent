namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// The outcome of completing an OAuth flow from the loopback callback: whether the code exchange
/// succeeded, the owning server key, and a human-readable error when it did not.
/// </summary>
public sealed record McpOAuthCompletion
{
    /// <summary>True when the code was exchanged and tokens were stored.</summary>
    public required bool Success { get; init; }

    /// <summary>The server key the completed flow belongs to, when known.</summary>
    public string? ServerKey { get; init; }

    /// <summary>A human-readable failure reason (never a token/code/secret).</summary>
    public string? Error { get; init; }

    /// <summary>A successful completion for <paramref name="serverKey"/>.</summary>
    public static McpOAuthCompletion Ok(string serverKey) => new() { Success = true, ServerKey = serverKey };

    /// <summary>A failed completion with <paramref name="error"/>.</summary>
    public static McpOAuthCompletion Fail(string error) => new() { Success = false, Error = error };
}
