using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Mcp;

/// <summary>
/// <see cref="IMcpGateway"/> implementation that forwards every MCP tool invocation
/// to the connected Bridge via the existing <see cref="IAgentHubClient"/> SignalR proxy.
/// Generates one stable invocation id (uuidv7) per dispatch and maps every failure to an
/// explicit outcome:
/// <list type="bullet">
/// <item>No Bridge connection (nothing dispatched) → definitive <see cref="McpToolOutcome.Failed"/>.</item>
/// <item>Caller cancellation or the bounded timeout firing after dispatch → best-effort
/// <see cref="IMcpHubClient.CancelMcpTool"/> with a short ack timeout, then
/// <see cref="McpToolOutcome.OutcomeUnknown"/> — the gateway never waits indefinitely for the
/// cancellation acknowledgement and NEVER retries the invocation.</item>
/// <item>Transport fault after dispatch started → <see cref="McpToolOutcome.OutcomeUnknown"/>.</item>
/// </list>
/// The gateway never throws for transport failures.
/// </summary>
public sealed partial class SignalRMcpGateway : IMcpGateway
{
    private const string UnreachableMessage = "MCP bridge unreachable";
    private const int TimeoutCeilingSeconds = 60;
    private static readonly TimeSpan CancellationSendTimeout = TimeSpan.FromSeconds(5);

    private readonly IBridgeClientProvider bridgeClient;
    private readonly TimeSpan invokeTimeout;
    private readonly ILogger<SignalRMcpGateway> logger;

    public SignalRMcpGateway(IBridgeClientProvider bridgeClient, McpGatewayOptions options, ILogger<SignalRMcpGateway> logger)
    {
        this.bridgeClient = bridgeClient;
        this.invokeTimeout = TimeSpan.FromSeconds(Math.Clamp(options.BridgeInvokeTimeoutSeconds, 1, TimeoutCeilingSeconds));
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<McpToolResult> InvokeAsync(
        string serverKey,
        string toolName,
        string argumentsJson,
        string? conversationId,
        string? channelId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        // One stable id per dispatch, threaded end to end (Bridge, MCP host, cancellation, audit).
        var invocationId = Guid.CreateVersion7().ToString("N");

        var client = this.bridgeClient.Client;
        if (client is null)
        {
            // Nothing was dispatched — this failure is definitive, not ambiguous.
            this.LogBridgeUnreachable(serverKey, toolName, invocationId, "not connected");
            return McpToolResult.Fail(
                invocationId,
                McpFailureKind.Unavailable,
                $"{UnreachableMessage}: the agent is not connected to the Bridge.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Cancelled before anything left the agent — definitive, no cancel signal needed.
            return McpToolResult.Cancelled(invocationId, "MCP tool invocation was cancelled before dispatch.");
        }

        var invocation = new McpToolInvocation
        {
            InvocationId = invocationId,
            ServerKey = serverKey,
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            ConversationId = conversationId,
            ChannelId = channelId,
            CorrelationId = correlationId,
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(this.invokeTimeout);
        try
        {
            return await client.InvokeMcpTool(invocation).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled after dispatch: the call may have executed. Best-effort cancel,
            // then report the ambiguity. NEVER retried.
            this.LogOutcomeUnknown(serverKey, toolName, invocationId, "caller cancelled mid-call");
            await this.SendCancellationAsync(client, invocationId, "caller cancelled").ConfigureAwait(false);
            return McpToolResult.Unknown(
                invocationId,
                McpFailureKind.Cancellation,
                "the invocation was cancelled after dispatch; it may still have executed");
        }
        catch (OperationCanceledException)
        {
            // The bounded timeout fired after dispatch: equally ambiguous.
            this.LogOutcomeUnknown(serverKey, toolName, invocationId, $"timed out after {this.invokeTimeout.TotalSeconds:0}s");
            await this.SendCancellationAsync(client, invocationId, "invocation timed out").ConfigureAwait(false);
            return McpToolResult.Unknown(
                invocationId,
                McpFailureKind.Timeout,
                $"the invocation timed out after {this.invokeTimeout.TotalSeconds:0}s; it may still have executed");
        }
#pragma warning disable CA1031 // Transport failures must surface as a structured result, never crash the agent.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The request may have reached the Bridge before the fault — ambiguous, not definitive.
            this.LogBridgeInvokeFailed(serverKey, toolName, invocationId, ex.Message);
            return McpToolResult.Unknown(
                invocationId,
                McpFailureKind.Transport,
                "the Bridge connection failed mid-call; the invocation may still have executed");
        }
    }

    /// <summary>
    /// Best-effort cancellation signal to the Bridge, bounded by a short fresh timeout token so
    /// the gateway never waits indefinitely for an acknowledgement from an unresponsive Bridge.
    /// </summary>
    private async Task SendCancellationAsync(IAgentHubClient client, string invocationId, string reason)
    {
        using var sendTimeoutCts = new CancellationTokenSource(CancellationSendTimeout);
        try
        {
            var cancellation = new McpToolCancellation { InvocationId = invocationId, Reason = reason };
            await client.CancelMcpTool(cancellation).WaitAsync(sendTimeoutCts.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The cancellation signal is best-effort; a failure to deliver it must not mask the outcome.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogCancellationSendFailed(invocationId, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP invoke unreachable for {ServerKey}/{ToolName} (invocation {InvocationId}): {Reason}")]
    private partial void LogBridgeUnreachable(string serverKey, string toolName, string invocationId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP invoke failed for {ServerKey}/{ToolName} (invocation {InvocationId}): {ErrorMessage}")]
    private partial void LogBridgeInvokeFailed(string serverKey, string toolName, string invocationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP invoke outcome unknown for {ServerKey}/{ToolName} (invocation {InvocationId}): {Reason}")]
    private partial void LogOutcomeUnknown(string serverKey, string toolName, string invocationId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP cancellation send failed for invocation {InvocationId}: {ErrorMessage}")]
    private partial void LogCancellationSendFailed(string invocationId, string errorMessage);
}
