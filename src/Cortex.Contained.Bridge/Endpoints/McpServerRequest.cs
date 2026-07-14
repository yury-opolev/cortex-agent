using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Add/edit request for an MCP server from the Web UI. The <see cref="Secret"/> field is
/// <b>write-only</b>: it is accepted on save (stored in DPAPI) but never echoed back by any GET.
/// Absent (<c>null</c>) fields are left unchanged on edit; an empty <see cref="Secret"/> clears the
/// stored secret.
/// </summary>
public sealed class McpServerRequest
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    [JsonPropertyName("auth")]
    public string? Auth { get; set; }

    [JsonPropertyName("apiKeyHeader")]
    public string? ApiKeyHeader { get; set; }

    /// <summary>Write-only static API-key secret. <c>null</c> = leave unchanged; empty = clear.</summary>
    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    [JsonPropertyName("toolAllowList")]
    public List<string>? ToolAllowList { get; set; }

    /// <summary>
    /// Tools the administrator explicitly classifies as mutating (require approval). Distinct
    /// from <see cref="ToolAllowList"/>, which controls exposure; when that list is non-empty,
    /// every mutation tool must also be present there.
    /// </summary>
    [JsonPropertyName("mutationToolAllowList")]
    public List<string>? MutationToolAllowList { get; set; }
}
