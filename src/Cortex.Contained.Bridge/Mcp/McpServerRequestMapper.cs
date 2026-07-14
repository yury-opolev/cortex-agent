using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Pure mapping + validation helpers for the MCP server REST surface. Maps a Web-UI
/// <see cref="McpServerRequest"/> onto an <see cref="McpServerConfig"/> and validates the server key.
/// Deliberately free of any secret handling — secret values are stored in DPAPI by the endpoint, never
/// here — so this stays a pure, fully unit-testable seam.
/// </summary>
public static class McpServerRequestMapper
{
    /// <summary>
    /// Validates a brand-new server key: non-empty, matching <c>[a-z0-9_-]+</c> (no <c>__</c> run),
    /// and unique (case-insensitive) among <paramref name="existingKeys"/>. Returns an error message,
    /// or <c>null</c> when valid.
    /// </summary>
    public static string? ValidateNewKey(string? key, IEnumerable<string> existingKeys)
    {
        var normalized = key?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(normalized))
        {
            return "Server key is required.";
        }

        if (!McpToolNamer.IsValidServerKey(normalized))
        {
            return "Server key must match [a-z0-9_-]+ and contain no '__' run.";
        }

        if (existingKeys.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return $"A server with key '{normalized}' already exists.";
        }

        return null;
    }

    /// <summary>
    /// Returns the first case-insensitively duplicated server key in <paramref name="keys"/> (lowercased),
    /// or <c>null</c> when all keys are unique. Catches duplicates the per-add <see cref="ValidateNewKey"/>
    /// cannot — e.g. a hand-edited config — that would otherwise let one server silently shadow another.
    /// </summary>
    public static string? FindDuplicateKey(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var normalized = key?.Trim().ToLowerInvariant() ?? string.Empty;
            if (normalized.Length == 0)
            {
                continue;
            }

            if (!seen.Add(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    /// <summary>Deterministic DPAPI secret id for a server's static API key.</summary>
    public static string ApiKeySecretId(string key)
    {
        return $"mcp/{key.Trim().ToLowerInvariant()}/apikey";
    }

    /// <summary>Builds a fresh <see cref="McpServerConfig"/> from an add request (key lowercased).</summary>
    public static McpServerConfig ToConfig(McpServerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var config = new McpServerConfig
        {
            Key = request.Key?.Trim().ToLowerInvariant() ?? string.Empty,
            Enabled = request.Enabled ?? true,
        };
        ApplyTo(config, request);
        return config;
    }

    /// <summary>
    /// Applies the non-null editable fields of <paramref name="request"/> onto
    /// <paramref name="target"/>. A <c>null</c> field is left unchanged (so a partial PUT — e.g. an
    /// enable toggle — preserves the rest). Never touches <see cref="McpServerConfig.SecretRef"/>.
    /// </summary>
    public static void ApplyTo(McpServerConfig target, McpServerRequest request)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Enabled is not null)
        {
            target.Enabled = request.Enabled.Value;
        }

        if (request.Transport is not null)
        {
            target.Transport = ParseTransport(request.Transport);
        }

        if (request.Url is not null)
        {
            target.Url = NullIfBlank(request.Url);
        }

        if (request.Command is not null)
        {
            target.Command = NullIfBlank(request.Command);
        }

        if (request.Args is not null)
        {
            target.Args = request.Args;
        }

        if (request.Env is not null)
        {
            target.Env = request.Env;
        }

        if (request.Auth is not null)
        {
            target.Auth = ParseAuth(request.Auth);
        }

        if (request.ApiKeyHeader is not null)
        {
            target.ApiKeyHeader = NullIfBlank(request.ApiKeyHeader);
        }

        if (request.ToolAllowList is not null)
        {
            target.ToolAllowList = NormalizeAllowList(request.ToolAllowList);
        }

        if (request.MutationToolAllowList is not null)
        {
            // Mutation names are normalized exactly like the exposure allow-list.
            target.MutationToolAllowList = NormalizeAllowList(request.MutationToolAllowList);
        }
    }

    /// <summary>
    /// Enforces the mutation-policy consistency rule: when <paramref name="toolAllowList"/> is
    /// non-empty (restricted exposure), every mutation-classified tool must also be present there —
    /// a mutation classification for a tool that is not even exposed is a configuration mistake.
    /// Returns an error message, or <c>null</c> when consistent.
    /// </summary>
    public static string? ValidateMutationAllowList(
        IReadOnlyCollection<string> toolAllowList, IReadOnlyCollection<string> mutationToolAllowList)
    {
        ArgumentNullException.ThrowIfNull(toolAllowList);
        ArgumentNullException.ThrowIfNull(mutationToolAllowList);

        if (toolAllowList.Count == 0)
        {
            return null;
        }

        foreach (var mutationTool in mutationToolAllowList)
        {
            if (!toolAllowList.Contains(mutationTool, StringComparer.Ordinal))
            {
                return $"Mutation tool '{mutationTool}' must also be present in the tool allow-list.";
            }
        }

        return null;
    }

    /// <summary>
    /// Validates the mutation-policy consistency of applying <paramref name="request"/> onto
    /// <paramref name="target"/> WITHOUT mutating it, so an invalid partial edit can be rejected
    /// before <see cref="ApplyTo"/> touches the live config. Returns an error message or <c>null</c>.
    /// </summary>
    public static string? ValidateMutationPolicy(McpServerConfig target, McpServerRequest request)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);

        var effectiveToolAllowList = request.ToolAllowList is not null
            ? NormalizeAllowList(request.ToolAllowList)
            : target.ToolAllowList;
        var effectiveMutationToolAllowList = request.MutationToolAllowList is not null
            ? NormalizeAllowList(request.MutationToolAllowList)
            : target.MutationToolAllowList;

        return ValidateMutationAllowList(effectiveToolAllowList, effectiveMutationToolAllowList);
    }

    /// <summary>Trims, drops blanks, and de-duplicates (ordinal) an allow-list.</summary>
    public static List<string> NormalizeAllowList(IEnumerable<string>? allowList)
    {
        if (allowList is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in allowList)
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    /// <summary>Parses a transport token (default <see cref="McpTransport.Stdio"/>).</summary>
    public static McpTransport ParseTransport(string? transport)
    {
        return string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase)
            ? McpTransport.Http
            : McpTransport.Stdio;
    }

    /// <summary>Parses an auth-mode token (default <see cref="McpAuthMode.Auto"/>).</summary>
    public static McpAuthMode ParseAuth(string? auth)
    {
        return auth?.Trim().ToLowerInvariant() switch
        {
            "none" => McpAuthMode.None,
            "apikey" => McpAuthMode.ApiKey,
            "oauth" => McpAuthMode.OAuth,
            _ => McpAuthMode.Auto,
        };
    }

    private static string? NullIfBlank(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
