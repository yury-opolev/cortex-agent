using System.Collections.Frozen;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// The host-resolved authentication material for one MCP server: environment variables to inject
/// into a stdio child, or headers to attach to an http transport — or a "needs login" signal when
/// the user must complete an interactive flow before the server is usable. Never carries the
/// secret id, only the already-resolved values (which never leave the host).
/// </summary>
public sealed record McpResolvedAuth
{
    /// <summary>Environment variables for the spawned stdio process (with secrets resolved).</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = FrozenDictionary<string, string>.Empty;

    /// <summary>HTTP headers to attach (e.g. <c>Authorization: Bearer …</c>).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = FrozenDictionary<string, string>.Empty;

    /// <summary>True when the server requires an interactive login the host has not yet completed.</summary>
    public bool NeedsAuth { get; init; }

    /// <summary>Human-readable reason when <see cref="NeedsAuth"/> is true.</summary>
    public string? NeedsAuthReason { get; init; }

    /// <summary>No auth attached.</summary>
    public static McpResolvedAuth None { get; } = new();

    /// <summary>The server needs an interactive login before it can be used.</summary>
    public static McpResolvedAuth RequiresLogin(string reason) =>
        new() { NeedsAuth = true, NeedsAuthReason = reason };
}
