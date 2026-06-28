namespace Cortex.Contained.Contracts.Config;

/// <summary>How the Bridge authenticates to an MCP server.</summary>
public enum McpAuthMode
{
    /// <summary>Auto-discover (HTTP only): connect; on 401 fall back to OAuth. Treated as <see cref="None"/> for stdio.</summary>
    Auto,

    /// <summary>Public server; nothing attached.</summary>
    None,

    /// <summary>Static API key / PAT / bearer from DPAPI (env for stdio, header for http).</summary>
    ApiKey,

    /// <summary>OAuth 2.1 auto-discovery (HTTP). Implemented in a later phase.</summary>
    OAuth,
}
