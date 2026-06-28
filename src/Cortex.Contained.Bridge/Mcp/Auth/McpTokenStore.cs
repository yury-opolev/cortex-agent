using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Persists OAuth tokens (access/refresh + client identity + token endpoint) per MCP server in the
/// DPAPI-backed secret store under <c>mcp/&lt;serverKey&gt;/oauth</c>. Tokens are serialized to a
/// single JSON blob and encrypted at rest by the underlying store. Telemetry carries only the
/// server key and the action — never a token, code, or secret.
/// </summary>
public sealed partial class McpTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IMcpTokenSecretStore secretStore;
    private readonly ILogger<McpTokenStore> logger;

    public McpTokenStore(IMcpTokenSecretStore secretStore, ILogger<McpTokenStore> logger)
    {
        this.secretStore = secretStore;
        this.logger = logger;
    }

    /// <summary>The DPAPI secret id under which a server's OAuth blob is stored.</summary>
    public static string SecretId(string serverKey) => $"mcp/{serverKey}/oauth";

    /// <summary>Loads the stored tokens for <paramref name="serverKey"/>, or null when none exist/parse.</summary>
    public McpOAuthTokens? Get(string serverKey)
    {
        var blob = this.secretStore.GetSecret(SecretId(serverKey));
        if (string.IsNullOrEmpty(blob))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<McpOAuthTokens>(blob, JsonOptions);
        }
        catch (JsonException ex)
        {
            this.LogParseFailed(serverKey, ex.Message);
            return null;
        }
    }

    /// <summary>Encrypts and stores <paramref name="tokens"/> for <paramref name="serverKey"/>.</summary>
    public void Save(string serverKey, McpOAuthTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var blob = JsonSerializer.Serialize(tokens, JsonOptions);
        this.secretStore.SetSecret(SecretId(serverKey), blob);
        this.LogSaved(serverKey);
    }

    /// <summary>Removes any stored tokens for <paramref name="serverKey"/>.</summary>
    public void Clear(string serverKey)
    {
        this.secretStore.RemoveSecret(SecretId(serverKey));
        this.LogCleared(serverKey);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}': OAuth tokens stored")]
    private partial void LogSaved(string serverKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}': OAuth tokens cleared")]
    private partial void LogCleared(string serverKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}': stored OAuth blob could not be parsed: {Error}")]
    private partial void LogParseFailed(string serverKey, string error);
}
