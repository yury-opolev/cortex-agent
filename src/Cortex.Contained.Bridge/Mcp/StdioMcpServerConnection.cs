using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// An MCP server reached by spawning a local child process speaking MCP over stdio. The process
/// is started with a minimal, explicit environment (the configured <c>Env</c>, which carries any
/// DPAPI-resolved secrets) — never the Bridge's full environment.
/// </summary>
public sealed class StdioMcpServerConnection : McpServerConnectionBase
{
    private readonly string command;
    private readonly IReadOnlyList<string> arguments;
    private readonly IReadOnlyDictionary<string, string> environment;
    private readonly string? workingDirectory;

    public StdioMcpServerConnection(
        string serverKey,
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyCollection<string> toolAllowList,
        ILogger<StdioMcpServerConnection> logger,
        string? workingDirectory = null,
        IReadOnlyCollection<string>? mutationToolAllowList = null,
        int callTimeoutSeconds = McpServerConfig.DefaultCallTimeoutSeconds,
        int maxResultBytes = McpResultMapper.DefaultMaxResultBytes)
        : base(serverKey, toolAllowList, mutationToolAllowList ?? [], logger, callTimeoutSeconds, maxResultBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        this.command = command;
        this.arguments = arguments ?? [];
        this.environment = environment ?? new Dictionary<string, string>();
        this.workingDirectory = workingDirectory;
    }

    protected override IClientTransport CreateTransport()
        => new StdioClientTransport(BuildTransportOptions(this.ServerKey, this.command, this.arguments, this.environment, this.workingDirectory));

    /// <summary>
    /// Builds the stdio transport options with a HARDENED environment. The spawned third-party
    /// process must NOT inherit the Bridge's environment — that holds <c>CORTEX_HUB_TOKEN</c> and
    /// other secrets which untrusted server code could read. We start from the SDK's minimal safe
    /// base (PATH etc.) and layer ONLY the explicitly-configured variables (incl. DPAPI-resolved
    /// secrets) on top. Extracted as the unit-testable security seam.
    /// </summary>
    internal static StdioClientTransportOptions BuildTransportOptions(
        string serverKey,
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? workingDirectory)
    {
        var env = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var (name, value) in environment)
        {
            env[name] = value;
        }

        var options = new StdioClientTransportOptions
        {
            Name = serverKey,
            Command = command,
            Arguments = [.. arguments],
            InheritEnvironmentVariables = false,
            EnvironmentVariables = env,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            options.WorkingDirectory = workingDirectory;
        }

        return options;
    }
}
