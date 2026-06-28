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
        string? workingDirectory = null)
        : base(serverKey, toolAllowList, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        this.command = command;
        this.arguments = arguments ?? [];
        this.environment = environment ?? new Dictionary<string, string>();
        this.workingDirectory = workingDirectory;
    }

    protected override IClientTransport CreateTransport()
    {
        var options = new StdioClientTransportOptions
        {
            Name = this.ServerKey,
            Command = this.command,
            Arguments = [.. this.arguments],
            // Only the explicitly-configured variables (incl. resolved secrets) reach the child —
            // not the Bridge's full environment.
            EnvironmentVariables = this.environment.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value, StringComparer.Ordinal),
        };

        if (!string.IsNullOrWhiteSpace(this.workingDirectory))
        {
            options.WorkingDirectory = this.workingDirectory;
        }

        return new StdioClientTransport(options);
    }
}
