using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Shared SDK-client lifecycle for <see cref="IMcpServerConnection"/> implementations. Subclasses
/// supply only the transport (<see cref="CreateTransport"/>); this base owns connect → list →
/// filter → cache, the <c>tools/call</c> path, status transitions, and disposal. Deliberately
/// thin — the per-transport surface is just the transport factory.
/// </summary>
public abstract partial class McpServerConnectionBase : IMcpServerConnection
{
    private readonly IReadOnlyCollection<string> toolAllowList;
    private readonly ILogger logger;
    private readonly Lock stateLock = new();

    private McpClient? client;
    private IReadOnlyList<McpToolDefinition> tools = [];
    private McpServerStatus status = McpServerStatus.Disconnected;
    private string? lastError;

    protected McpServerConnectionBase(string serverKey, IReadOnlyCollection<string> toolAllowList, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        this.ServerKey = serverKey;
        this.toolAllowList = toolAllowList ?? [];
        this.logger = logger;
    }

    public string ServerKey { get; }

    public McpServerStatus Status
    {
        get
        {
            lock (this.stateLock)
            {
                return this.status;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (this.stateLock)
            {
                return this.lastError;
            }
        }
    }

    public IReadOnlyList<McpToolDefinition> Tools
    {
        get
        {
            lock (this.stateLock)
            {
                return this.tools;
            }
        }
    }

    /// <summary>Builds the transport for this connection. Called once per <see cref="ConnectAsync"/>.</summary>
    protected abstract IClientTransport CreateTransport();

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        this.SetStatus(McpServerStatus.Connecting, error: null);
        this.LogConnecting(this.ServerKey);

        try
        {
            var transport = this.CreateTransport();
            var created = await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: null, cancellationToken)
                .ConfigureAwait(false);

            var listed = await created.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var definitions = new List<McpToolDefinition>(listed.Count);
            foreach (var tool in listed)
            {
                if (!McpToolFilter.IsAllowed(tool.Name, this.toolAllowList))
                {
                    continue;
                }

                definitions.Add(new McpToolDefinition
                {
                    ServerKey = this.ServerKey,
                    ToolName = tool.Name,
                    FullName = McpToolNamer.Full(this.ServerKey, tool.Name),
                    Description = tool.Description ?? string.Empty,
                    ParametersSchemaJson = tool.JsonSchema.GetRawText(),
                });
            }

            lock (this.stateLock)
            {
                this.client = created;
                this.tools = definitions;
                this.status = McpServerStatus.Connected;
                this.lastError = null;
            }

            this.LogConnected(this.ServerKey, definitions.Count);
        }
#pragma warning disable CA1031 // Connection failures must surface via status, never crash the host.
        catch (Exception ex)
        {
            this.SetStatus(McpServerStatus.Error, ex.Message);
            this.LogConnectFailed(this.ServerKey, ex.Message);
        }
#pragma warning restore CA1031
    }

    public async Task<McpToolResult> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken)
    {
        // SECURITY: the allow-list is a policy boundary, not just a catalog filter. Re-check at
        // invoke time so a (prompt-injected) agent cannot call an excluded tool by naming it directly.
        if (!McpToolFilter.IsAllowed(toolName, this.toolAllowList))
        {
            this.LogToolNotAllowed(this.ServerKey, toolName);
            return McpToolResult.Fail($"MCP tool '{toolName}' is not permitted for server '{this.ServerKey}'.");
        }

        McpClient? activeClient;
        lock (this.stateLock)
        {
            activeClient = this.status == McpServerStatus.Connected ? this.client : null;
        }

        if (activeClient is null)
        {
            return McpToolResult.Fail($"MCP server '{this.ServerKey}' is not connected.");
        }

        try
        {
            var arguments = McpArguments.Parse(argumentsJson);
            var result = await activeClient.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var mapped = McpResultMapper.ToToolResult(result);
            this.LogToolInvoked(this.ServerKey, toolName, mapped.IsError);
            return mapped;
        }
#pragma warning disable CA1031 // Tool/transport failures map to a structured result, never thrown.
        catch (Exception ex)
        {
            // Log the detail host-side only; the agent receives a sanitized, secret-free message.
            this.LogToolFailed(this.ServerKey, toolName, ex.Message);
            return McpToolResult.Fail(McpErrorSanitizer.ToolFailure(this.ServerKey, toolName));
        }
#pragma warning restore CA1031
    }

    public async ValueTask DisposeAsync()
    {
        McpClient? toDispose;
        lock (this.stateLock)
        {
            toDispose = this.client;
            this.client = null;
            this.tools = [];
            this.status = McpServerStatus.Disconnected;
        }

        if (toDispose is not null)
        {
            try
            {
                await toDispose.DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort disposal.
            catch (Exception ex)
            {
                this.LogDisposeFailed(this.ServerKey, ex.Message);
            }
#pragma warning restore CA1031
        }

        GC.SuppressFinalize(this);
    }

    private void SetStatus(McpServerStatus newStatus, string? error)
    {
        lock (this.stateLock)
        {
            this.status = newStatus;
            this.lastError = error;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}' connecting")]
    private partial void LogConnecting(string serverKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}' connected: {ToolCount} tools")]
    private partial void LogConnected(string serverKey, int toolCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}' connect failed: {Error}")]
    private partial void LogConnectFailed(string serverKey, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool invoked: server '{ServerKey}' tool '{ToolName}' isError={IsError}")]
    private partial void LogToolInvoked(string serverKey, string toolName, bool isError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool failed: server '{ServerKey}' tool '{ToolName}': {Error}")]
    private partial void LogToolFailed(string serverKey, string toolName, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool invocation blocked by allow-list: server '{ServerKey}' tool '{ToolName}'")]
    private partial void LogToolNotAllowed(string serverKey, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}' dispose failed: {Error}")]
    private partial void LogDisposeFailed(string serverKey, string error);
}
