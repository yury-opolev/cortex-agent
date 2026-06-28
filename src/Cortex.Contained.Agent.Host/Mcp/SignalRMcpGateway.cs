using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Mcp;

/// <summary>
/// <see cref="IMcpGateway"/> implementation that forwards every MCP tool invocation
/// to the connected Bridge via the existing <see cref="IAgentHubClient"/> SignalR proxy.
/// Mirrors <see cref="Coding.SignalRCodingAgent"/>: every invoke is bounded by
/// <see cref="McpGatewayOptions.BridgeInvokeTimeoutSeconds"/> so an unresponsive Bridge can
/// never hold the per-channel lock forever. Transport failures (no Bridge, timeout, dropped
/// connection) map to <see cref="McpToolResult.Fail(string, bool)"/> — the gateway never
/// throws for them.
/// </summary>
public sealed partial class SignalRMcpGateway : IMcpGateway
{
    private const string UnreachableMessage = "MCP bridge unreachable";

    private readonly IBridgeClientProvider bridgeClient;
    private readonly TimeSpan invokeTimeout;
    private readonly ILogger<SignalRMcpGateway> logger;

    public SignalRMcpGateway(IBridgeClientProvider bridgeClient, McpGatewayOptions options, ILogger<SignalRMcpGateway> logger)
    {
        this.bridgeClient = bridgeClient;
        this.invokeTimeout = TimeSpan.FromSeconds(Math.Max(1, options.BridgeInvokeTimeoutSeconds));
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<McpToolResult> InvokeAsync(
        string serverKey,
        string toolName,
        string argumentsJson,
        string? conversationId,
        CancellationToken cancellationToken)
    {
        var client = this.bridgeClient.Client;
        if (client is null)
        {
            this.LogBridgeUnreachable(serverKey, toolName, "not connected");
            return McpToolResult.Fail($"{UnreachableMessage}: the agent is not connected to the Bridge.");
        }

        var invocation = new McpToolInvocation
        {
            ServerKey = serverKey,
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            ConversationId = conversationId,
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(this.invokeTimeout);
        try
        {
            return await client.InvokeMcpTool(invocation).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            this.LogBridgeUnreachable(serverKey, toolName, "timed out");
            return McpToolResult.Fail($"{UnreachableMessage}: invocation timed out after {this.invokeTimeout.TotalSeconds:0}s.");
        }
#pragma warning disable CA1031 // Transport failures must surface as a tool-error result, never crash the agent.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            this.LogBridgeInvokeFailed(serverKey, toolName, ex.Message);
            return McpToolResult.Fail($"{UnreachableMessage}: {ex.Message}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP invoke unreachable for {ServerKey}/{ToolName}: {Reason}")]
    private partial void LogBridgeUnreachable(string serverKey, string toolName, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP invoke failed for {ServerKey}/{ToolName}: {ErrorMessage}")]
    private partial void LogBridgeInvokeFailed(string serverKey, string toolName, string errorMessage);
}
