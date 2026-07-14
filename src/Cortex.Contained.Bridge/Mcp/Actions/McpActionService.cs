using System.Text.Json;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Mcp.Actions;

/// <summary>
/// Routes every agent-initiated MCP tool invocation by mutation classification:
/// <list type="bullet">
/// <item>READ tool → the existing direct <see cref="McpHostService"/> path.</item>
/// <item>MUTATION tool → canonicalize the arguments, persist a <c>proposed</c> action in the
/// durable store, and return a SUCCESSFUL awaiting-approval result WITHOUT any remote call.
/// The mutation only ever reaches the remote server later, via the outbox dispatcher, after a
/// human approval bound to the exact canonical-argument hash.</item>
/// </list>
/// Also serves the agent's <c>mcp_action_status</c>/<c>mcp_action_cancel</c> tools and the
/// cancel path of the REST API (which must poke the active dispatch, not just the store).
/// </summary>
public sealed partial class McpActionService
{
    /// <summary>How long a proposal waits for a human decision before it expires.</summary>
    internal static readonly TimeSpan ProposalTtl = TimeSpan.FromHours(24);

    /// <summary>The exact agent-facing message accompanying an awaiting-approval result.</summary>
    internal const string AwaitingApprovalMessage = "Awaiting exact-argument approval. Do not repeat this mutation.";

    private static readonly JsonSerializerOptions ContentSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IMcpActionStore store;
    private readonly IMcpInvocationTarget invocationTarget;
    private readonly McpConfigStore configStore;
    private readonly McpActionDispatchRegistry dispatchRegistry;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<McpActionService> logger;

    public McpActionService(
        IMcpActionStore store,
        IMcpInvocationTarget invocationTarget,
        McpConfigStore configStore,
        McpActionDispatchRegistry dispatchRegistry,
        TimeProvider timeProvider,
        ILogger<McpActionService> logger)
    {
        this.store = store;
        this.invocationTarget = invocationTarget;
        this.configStore = configStore;
        this.dispatchRegistry = dispatchRegistry;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Invokes an MCP tool for <paramref name="tenantId"/>. A read tool dispatches directly; a
    /// mutation-classified tool is persisted as a proposed action and completes successfully
    /// with <see cref="McpToolDisposition.AwaitingApproval"/> — no remote call is made.
    /// Re-invoking an identical pending mutation deduplicates to the existing action.
    /// </summary>
    public async Task<McpToolResult> InvokeAsync(string tenantId, McpToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(invocation);

        if (!this.IsMutation(invocation.ServerKey, invocation.ToolName))
        {
            return await this.invocationTarget.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
        }

        CanonicalMcpArguments canonical;
        try
        {
            canonical = McpCanonicalArguments.Canonicalize(invocation.ArgumentsJson);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            // Nothing was persisted or dispatched — a definitive validation failure.
            this.LogCanonicalizationFailed(invocation.ServerKey, invocation.ToolName, invocation.InvocationId, ex.Message);
            return McpToolResult.Fail(
                invocation.InvocationId,
                McpFailureKind.Validation,
                $"MCP mutation arguments could not be canonicalized: {ex.Message}");
        }

        var now = this.timeProvider.GetUtcNow();
        McpAction action;
        try
        {
            action = await this.store.ProposeAsync(
                new McpActionProposal
                {
                    TenantId = tenantId,
                    InvocationId = invocation.InvocationId,
                    CorrelationId = invocation.CorrelationId,
                    ConversationId = invocation.ConversationId,
                    ChannelId = invocation.ChannelId,
                    WorkerId = invocation.WorkerId,
                    ServerKey = invocation.ServerKey,
                    ToolName = invocation.ToolName,
                    CanonicalArgumentsJson = canonical.Json,
                    ArgumentsHash = canonical.Sha256,
                    CreatedAtUtc = now,
                    ProposalExpiresAtUtc = now + ProposalTtl,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Same invocation id re-proposed with DIFFERENT arguments — definitive, nothing dispatched.
            this.LogProposeConflict(invocation.ServerKey, invocation.ToolName, invocation.InvocationId, ex.Message);
            return McpToolResult.Fail(invocation.InvocationId, McpFailureKind.Validation, ex.Message);
        }

        this.LogMutationProposed(action.ActionId, invocation.ServerKey, invocation.ToolName, action.ArgumentsHash);

        var content = JsonSerializer.Serialize(
            new AwaitingApprovalContent(
                action.ActionId,
                McpActionWireStatus.From(action.State),
                action.ArgumentsHash,
                AwaitingApprovalMessage),
            ContentSerializerOptions);

        return McpToolResult.AwaitingApproval(invocation.InvocationId, action.ActionId, action.ArgumentsHash, content);
    }

    /// <summary>Looks up the status of one action for the agent's <c>mcp_action_status</c> tool.</summary>
    public async Task<McpActionStatusResponse> GetStatusAsync(string tenantId, string actionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (string.IsNullOrWhiteSpace(actionId))
        {
            return new McpActionStatusResponse { Found = false, Error = "action_id is required" };
        }

        var action = await this.store.GetAsync(tenantId, actionId, cancellationToken).ConfigureAwait(false);
        if (action is null)
        {
            return new McpActionStatusResponse { Found = false, Error = $"No MCP action '{actionId}'." };
        }

        return new McpActionStatusResponse
        {
            Found = true,
            ActionId = action.ActionId,
            Status = McpActionWireStatus.From(action.State),
            ArgumentsHash = action.ArgumentsHash,
            ServerKey = action.ServerKey,
            ToolName = action.ToolName,
            ResultContent = action.ResultContent,
            Error = action.Error,
            RemoteReference = action.RemoteReference,
        };
    }

    /// <summary>
    /// Cancels one action, bound to its exact canonical-argument hash. Proposed/approved →
    /// cancelled immediately. Dispatching → the cancel request is recorded AND the active
    /// invocation is asked to cancel; the dispatch outcome decides — a call that already began
    /// remotely resolves to <c>outcome_unknown</c>, NEVER <c>cancelled</c>.
    /// </summary>
    public async Task<McpActionCancelResponse> CancelAsync(string tenantId, string actionId, string argumentsHash, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(argumentsHash))
        {
            return new McpActionCancelResponse { Accepted = false, Error = "action_id and arguments_hash are required" };
        }

        var result = await this.store.CancelAsync(tenantId, actionId, argumentsHash, actor, cancellationToken).ConfigureAwait(false);
        if (!result.Accepted)
        {
            return new McpActionCancelResponse
            {
                Accepted = false,
                Status = result.Action is { } existing ? McpActionWireStatus.From(existing.State) : null,
                Error = result.Error,
            };
        }

        if (result.Action is { State: McpActionState.Dispatching })
        {
            // The mutation may be in flight: signal the active invocation. The action stays
            // dispatching until the outcome is known — it must never be reported cancelled here.
            var signalled = this.dispatchRegistry.RequestCancel(actionId);
            this.LogDispatchCancelRequested(actionId, signalled);
        }

        return new McpActionCancelResponse
        {
            Accepted = true,
            Status = McpActionWireStatus.From(result.Action!.State),
        };
    }

    private bool IsMutation(string serverKey, string toolName)
    {
        var settings = this.configStore.GetSettings();
        var server = FindServer(settings, serverKey);
        return server is not null && McpToolFilter.IsMutation(toolName, server.MutationToolAllowList);
    }

    private static McpServerConfig? FindServer(McpSettingsConfig settings, string serverKey)
        => settings.Servers.FirstOrDefault(s => string.Equals(s.Key, serverKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>The agent-facing JSON payload of an awaiting-approval tool result.</summary>
    private sealed record AwaitingApprovalContent(
        string ActionId,
        string Status,
        string ArgumentsHash,
        string Message);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP mutation proposed as action {ActionId} for {ServerKey}/{ToolName} (hash {ArgumentsHash}); awaiting exact-argument approval")]
    private partial void LogMutationProposed(string actionId, string serverKey, string toolName, string argumentsHash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP mutation canonicalization failed for {ServerKey}/{ToolName} (invocation {InvocationId}): {ErrorMessage}")]
    private partial void LogCanonicalizationFailed(string serverKey, string toolName, string invocationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP mutation proposal conflict for {ServerKey}/{ToolName} (invocation {InvocationId}): {ErrorMessage}")]
    private partial void LogProposeConflict(string serverKey, string toolName, string invocationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cancel requested for dispatching MCP action {ActionId}; active invocation signalled: {Signalled}")]
    private partial void LogDispatchCancelRequested(string actionId, bool signalled);
}
