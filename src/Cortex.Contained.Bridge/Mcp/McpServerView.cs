namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// The redacted, Web-UI-facing projection of one configured MCP server. <b>Secret-safe by
/// construction:</b> it exposes only whether a secret is set (<see cref="HasSecret"/>) and the DPAPI
/// reference id (<see cref="SecretRef"/>) — never a secret value, and there is no property capable of
/// carrying one.
/// </summary>
public sealed record McpServerView
{
    public required string Key { get; init; }

    public required bool Enabled { get; init; }

    /// <summary><c>"stdio"</c> or <c>"http"</c>.</summary>
    public required string Transport { get; init; }

    public string? Url { get; init; }

    public string? Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Stdio environment variables (non-secret by design; secrets use <c>${secret:id}</c> references).</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary><c>"auto"</c> / <c>"none"</c> / <c>"apiKey"</c> / <c>"oauth"</c>.</summary>
    public required string Auth { get; init; }

    public string? ApiKeyHeader { get; init; }

    /// <summary>True when a static API-key secret reference is set (the value lives in DPAPI).</summary>
    public bool HasSecret { get; init; }

    /// <summary>The DPAPI secret reference id (safe to surface), or null.</summary>
    public string? SecretRef { get; init; }

    public IReadOnlyList<string> ToolAllowList { get; init; } = [];

    /// <summary>Tools explicitly classified as mutating by the admin (require approval).</summary>
    public IReadOnlyList<string> MutationToolAllowList { get; init; } = [];

    /// <summary>Per-call timeout, in seconds, before the Bridge reports an ambiguous timeout.</summary>
    public int CallTimeoutSeconds { get; init; }

    /// <summary>Maximum UTF-8 byte size of a flattened tool result before it is truncated.</summary>
    public int MaxResultBytes { get; init; }

    /// <summary><c>"connected"</c> / <c>"error"</c> / <c>"needsLogin"</c> / <c>"connecting"</c> / <c>"disconnected"</c> / <c>"disabled"</c>.</summary>
    public required string Status { get; init; }

    public int ToolCount { get; init; }

    public string? LastError { get; init; }

    /// <summary>Discovered server-local tool names (for the allow-list UI).</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];
}
