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
            // SECURITY: content-free — only the exception TYPE, never ex.Message (the SignalR
            // transport fault could echo a fragment of the connection endpoint or hub protocol).
            this.LogBridgeInvokeFailed(serverKey, toolName, invocationId, ex.GetType().Name);
            return McpToolResult.Unknown(
                invocationId,
                McpFailureKind.Transport,
                "the Bridge connection failed mid-call; the invocation may still have executed");
        }
    }

    /// <inheritdoc />
    public async Task<McpActionStatusResponse> GetActionStatusAsync(string actionId, CancellationToken cancellationToken)
    {
        var client = this.bridgeClient.Client;
        if (client is null)
        {
            return new McpActionStatusResponse
            {
                Found = false,
                Error = $"{UnreachableMessage}: the agent is not connected to the Bridge.",
            };
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(this.invokeTimeout);
        try
        {
            var request = new McpActionStatusRequest { ActionId = actionId };
            return await client.GetMcpActionStatus(request).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A status lookup is read-only; transport failures surface as a structured result.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // SECURITY: the LOG carries only the exception TYPE. The Error field below is
            // returned to the LLM as mcp_action_status tool-result content — that raw ex.Message
            // is intentional there (the agent needs to know why the lookup failed).
            this.LogActionCallFailed("status", actionId, ex.GetType().Name);
            return new McpActionStatusResponse
            {
                Found = false,
                Error = $"{UnreachableMessage}: {ex.Message}",
            };
        }
    }

    /// <inheritdoc />
    public async Task<McpActionCancelResponse> CancelActionAsync(string actionId, string argumentsHash, CancellationToken cancellationToken)
    {
        var client = this.bridgeClient.Client;
        if (client is null)
        {
            return new McpActionCancelResponse
            {
                Accepted = false,
                Error = $"{UnreachableMessage}: the agent is not connected to the Bridge.",
            };
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(this.invokeTimeout);
        try
        {
            var request = new McpActionCancelRequest { ActionId = actionId, ArgumentsHash = argumentsHash };
            return await client.CancelMcpAction(request).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The Bridge decides the cancel; a transport failure here must surface as not-accepted, never a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // SECURITY: the LOG carries only the exception TYPE. The Error field below is
            // returned to the LLM as mcp_action_cancel tool-result content — that raw ex.Message
            // is intentional there (the agent needs to know why the cancel failed).
            this.LogActionCallFailed("cancel", actionId, ex.GetType().Name);
            return new McpActionCancelResponse
            {
                Accepted = false,
                Error = $"{UnreachableMessage}: {ex.Message}. The cancel may not have been recorded — check the action's status.",
            };
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
            // SECURITY: content-free — only the exception TYPE, never ex.Message.
            this.LogCancellationSendFailed(invocationId, ex.GetType().Name);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP action {Operation} call failed for action {ActionId}: {ErrorMessage}")]
    private partial void LogActionCallFailed(string operation, string actionId, string errorMessage);
}
