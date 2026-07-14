using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Pure projection from an <see cref="McpServerConfig"/> (+ optional live
/// <see cref="McpServerRuntimeInfo"/>) to the redacted <see cref="McpServerView"/> returned by the
/// REST API. Centralizes secret redaction and status-label computation as a fully unit-testable seam.
/// </summary>
public static class McpServerProjection
{
    /// <summary>Projects one server into its redacted Web-UI view.</summary>
    public static McpServerView Project(
        McpServerConfig config, bool masterEnabled, McpServerRuntimeInfo? runtime, bool needsLogin)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new McpServerView
        {
            Key = config.Key,
            Enabled = config.Enabled,
            Transport = config.Transport == McpTransport.Http ? "http" : "stdio",
            Url = config.Url,
            Command = config.Command,
            Args = config.Args,
            Env = RedactEnv(config.Env),
            Auth = AuthLabel(config.Auth),
            ApiKeyHeader = config.ApiKeyHeader,
            HasSecret = !string.IsNullOrEmpty(config.SecretRef),
            SecretRef = config.SecretRef,
            ToolAllowList = config.ToolAllowList,
            MutationToolAllowList = config.MutationToolAllowList,
            CallTimeoutSeconds = config.CallTimeoutSeconds,
            MaxResultBytes = config.MaxResultBytes,
            Status = StatusLabel(config, masterEnabled, runtime, needsLogin),
            ToolCount = runtime?.Tools.Count ?? 0,
            LastError = runtime?.LastError,
            Tools = runtime is null ? [] : runtime.Tools.Select(t => t.ToolName).ToList(),
        };
    }

    /// <summary>Computes the UI status label for a server.</summary>
    public static string StatusLabel(
        McpServerConfig config, bool masterEnabled, McpServerRuntimeInfo? runtime, bool needsLogin)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!masterEnabled || !config.Enabled)
        {
            return "disabled";
        }

        if (runtime is not null)
        {
            return runtime.Status switch
            {
                McpServerStatus.Connected => "connected",
                McpServerStatus.Error => "error",
                McpServerStatus.NeedsLogin => "needsLogin",
                McpServerStatus.Connecting => "connecting",
                _ => "disconnected",
            };
        }

        return needsLogin ? "needsLogin" : "disconnected";
    }

    /// <summary>
    /// Redacts stdio <c>env</c> values for the API response: a <c>${secret:id}</c> reference is kept
    /// (it carries no value), a value that looks like a raw secret is masked, and a plain literal
    /// (e.g. a flag or path) is shown as-is. A user who pastes a raw secret never sees it echoed back.
    /// </summary>
    private static Dictionary<string, string> RedactEnv(Dictionary<string, string> env)
    {
        var redacted = new Dictionary<string, string>(env.Count, StringComparer.Ordinal);
        foreach (var (name, value) in env)
        {
            redacted[name] = Auth.McpSecretRef.TryParse(value, out _) || !McpEnvSecretHeuristic.LooksLikeSecret(value)
                ? value
                : "***redacted***";
        }

        return redacted;
    }

    private static string AuthLabel(McpAuthMode auth)
    {
        return auth switch
        {
            McpAuthMode.None => "none",
            McpAuthMode.ApiKey => "apiKey",
            McpAuthMode.OAuth => "oauth",
            _ => "auto",
        };
    }
}
