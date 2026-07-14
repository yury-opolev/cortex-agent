using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Mcp.Actions;

/// <summary>
/// Outbox dispatcher for approval-gated MCP mutations. On startup it recovers every dispatch
/// interrupted by a crash/restart to <c>outcome_unknown</c> (never silently succeeded, never
/// re-dispatched) and expires stale proposals/approvals. It then repeatedly claims ONE approved
/// action transactionally, re-checks policy (server enabled, ordinary allow-list, mutation
/// classification), and dispatches EXACTLY the stored canonical arguments at-most-once:
/// <list type="bullet">
/// <item>Success → <c>succeeded</c>; MCP-reported error → <c>failed</c>.</item>
/// <item>Timeout / transport loss / in-flight cancellation → <c>outcome_unknown</c>, NEVER retried.</item>
/// <item>Pre-dispatch unavailability (positively not started) → released back to <c>approved</c>
/// with bounded exponential backoff; a pending cancel is honored by the store instead.</item>
/// </list>
/// </summary>
public sealed partial class McpActionDispatcher : BackgroundService
{
    /// <summary>Idle delay between polls when no approved action is due.</summary>
    internal static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);

    private readonly IMcpActionStore store;
    private readonly IMcpInvocationTarget invocationTarget;
    private readonly McpConfigStore configStore;
    private readonly McpActionDispatchRegistry dispatchRegistry;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<McpActionDispatcher> logger;

    public McpActionDispatcher(
        IMcpActionStore store,
        IMcpInvocationTarget invocationTarget,
        McpConfigStore configStore,
        McpActionDispatchRegistry dispatchRegistry,
        TimeProvider timeProvider,
        ILogger<McpActionDispatcher> logger)
    {
        this.store = store;
        this.invocationTarget = invocationTarget;
        this.configStore = configStore;
        this.dispatchRegistry = dispatchRegistry;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await this.RecoverAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var dispatched = false;
            try
            {
                dispatched = await this.ProcessNextAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // The outbox loop must survive any single-iteration failure.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                this.LogLoopIterationFailed(ex.Message);
            }

            if (!dispatched)
            {
                try
                {
                    await Task.Delay(IdlePollInterval, this.timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Crash recovery, run once before the outbox loop: every action left <c>dispatching</c> by
    /// a previous process becomes <c>outcome_unknown</c> (its dispatch may have executed — it is
    /// never assumed successful and NEVER re-dispatched), and stale proposals/approvals expire.
    /// </summary>
    internal async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        var now = this.timeProvider.GetUtcNow();
        var recovered = await this.store.RecoverInterruptedDispatchesAsync(now, cancellationToken).ConfigureAwait(false);
        if (recovered > 0)
        {
            this.LogRecoveredInterrupted(recovered);
        }

        await this.store.ExpireAsync(now, cancellationToken).ConfigureAwait(false);
        return recovered;
    }

    /// <summary>
    /// One outbox turn: expire stale actions, then claim and dispatch at most ONE approved
    /// action. Returns true when an action was claimed (regardless of its outcome).
    /// </summary>
    internal async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = this.timeProvider.GetUtcNow();
        await this.store.ExpireAsync(now, cancellationToken).ConfigureAwait(false);

        var lease = await this.store.TryClaimNextApprovedAsync(now, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        await this.DispatchAsync(lease, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task DispatchAsync(McpActionDispatchLease lease, CancellationToken cancellationToken)
    {
        // Policy is re-evaluated NOW — never trusted from proposal or approval time.
        var settings = this.configStore.GetSettings();
        var server = settings.Enabled ? FindServer(settings, lease.ServerKey) : null;

        if (server is null || !server.Enabled)
        {
            // The server (or the whole MCP subsystem) is switched off right now. Nothing was
            // dispatched — release the action back to approved with bounded backoff; the
            // approval expiry bounds the total retry window.
            await this.CompleteAsync(BuildDeferral(
                lease,
                $"MCP server '{lease.ServerKey}' is not enabled.",
                this.NextRetryAt(lease.AttemptNumber),
                this.timeProvider.GetUtcNow())).ConfigureAwait(false);
            this.LogDeferredUnavailable(lease.ActionId, lease.ServerKey, lease.AttemptNumber);
            return;
        }

        if (!McpToolFilter.IsAllowed(lease.ToolName, server.ToolAllowList))
        {
            await this.CompleteAsync(this.BuildPolicyFailure(
                lease,
                $"MCP tool '{lease.ToolName}' is no longer permitted for server '{lease.ServerKey}'.")).ConfigureAwait(false);
            this.LogPolicyRefused(lease.ActionId, lease.ServerKey, lease.ToolName, "excluded by allow-list");
            return;
        }

        if (!McpToolFilter.IsMutation(lease.ToolName, server.MutationToolAllowList))
        {
            // The admin re-classified the tool since approval: the approval flow's premise no
            // longer holds. Refuse definitively (nothing dispatched) — the tool can now be
            // invoked directly without approval.
            await this.CompleteAsync(this.BuildPolicyFailure(
                lease,
                $"MCP tool '{lease.ToolName}' on server '{lease.ServerKey}' is no longer classified as a mutation; invoke it directly instead.")).ConfigureAwait(false);
            this.LogPolicyRefused(lease.ActionId, lease.ServerKey, lease.ToolName, "no longer mutation-classified");
            return;
        }

        // Dispatch EXACTLY the stored canonical arguments under the original invocation id.
        var invocation = new McpToolInvocation
        {
            InvocationId = lease.InvocationId,
            ServerKey = lease.ServerKey,
            ToolName = lease.ToolName,
            ArgumentsJson = lease.CanonicalArgumentsJson,
        };

        McpToolResult result;
        using var dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.dispatchRegistry.Register(lease.ActionId, dispatchCts);
        try
        {
            this.LogDispatching(lease.ActionId, lease.ServerKey, lease.ToolName, lease.AttemptNumber);
            result = await this.invocationTarget.InvokeApprovedAsync(invocation, dispatchCts.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // An unexpected dispatch fault is AMBIGUOUS: the call may have reached the server.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result = McpToolResult.Unknown(
                lease.InvocationId,
                McpFailureKind.Transport,
                $"dispatch faulted: {ex.Message}; the mutation may still have executed");
        }
        finally
        {
            this.dispatchRegistry.Unregister(lease.ActionId);
        }

        await this.CompleteAsync(this.MapCompletion(lease, result)).ConfigureAwait(false);
    }

    private McpActionDispatchCompletion MapCompletion(McpActionDispatchLease lease, McpToolResult result)
    {
        var completedAt = this.timeProvider.GetUtcNow();
        return result.Outcome switch
        {
            McpToolOutcome.Succeeded => new McpActionDispatchCompletion
            {
                ActionId = lease.ActionId,
                AttemptNumber = lease.AttemptNumber,
                State = McpActionState.Succeeded,
                FailureKind = McpFailureKind.None,
                ResultContent = result.Content,
                CompletedAtUtc = completedAt,
            },

            // Pre-dispatch unavailability: positively known not to have started — release back
            // to approved with bounded backoff (or let a pending cancel be honored by the store).
            McpToolOutcome.Failed when result.FailureKind == McpFailureKind.Unavailable => BuildDeferral(
                lease,
                result.Error ?? "MCP server unavailable before dispatch.",
                this.NextRetryAt(lease.AttemptNumber),
                completedAt),

            // Definitive pre-dispatch cancellation: also positively not started.
            McpToolOutcome.Cancelled => new McpActionDispatchCompletion
            {
                ActionId = lease.ActionId,
                AttemptNumber = lease.AttemptNumber,
                State = McpActionState.Approved,
                FailureKind = McpFailureKind.Cancellation,
                Error = result.Error ?? "cancelled before dispatch",
                CompletedAtUtc = completedAt,
                RetryAtUtc = this.NextRetryAt(lease.AttemptNumber),
            },

            McpToolOutcome.Failed => new McpActionDispatchCompletion
            {
                ActionId = lease.ActionId,
                AttemptNumber = lease.AttemptNumber,
                State = McpActionState.Failed,
                FailureKind = result.FailureKind,
                Error = result.Error ?? "MCP tool invocation failed.",
                CompletedAtUtc = completedAt,
            },

            // Timeout / transport loss / in-flight cancellation: the mutation MAY have executed.
            // outcome_unknown is terminal for the outbox — it is NEVER retried automatically.
            _ => new McpActionDispatchCompletion
            {
                ActionId = lease.ActionId,
                AttemptNumber = lease.AttemptNumber,
                State = McpActionState.OutcomeUnknown,
                FailureKind = result.FailureKind,
                Error = result.Error ?? "MCP invocation outcome is unknown.",
                CompletedAtUtc = completedAt,
            },
        };
    }

    private async Task CompleteAsync(McpActionDispatchCompletion completion)
    {
        try
        {
            // CancellationToken.None: outcome bookkeeping must complete even during shutdown —
            // a hard crash here is covered by startup recovery (dispatching → outcome_unknown).
            await this.store.CompleteAttemptAsync(completion, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // BENIGN: the attempt was already completed (e.g. a concurrent recovery pass or a
            // duplicate completion). The store's state machine already holds the truth — never
            // overwrite it, never re-dispatch.
            this.LogDuplicateCompletionIgnored(completion.ActionId, completion.AttemptNumber, ex.Message);
        }
    }

    private DateTimeOffset NextRetryAt(int attemptNumber)
        => this.timeProvider.GetUtcNow() + McpReconnectBackoff.DelayFor(attemptNumber);

    private static McpActionDispatchCompletion BuildDeferral(
        McpActionDispatchLease lease, string error, DateTimeOffset retryAtUtc, DateTimeOffset completedAtUtc)
        => new()
        {
            ActionId = lease.ActionId,
            AttemptNumber = lease.AttemptNumber,
            State = McpActionState.Approved,
            FailureKind = McpFailureKind.Unavailable,
            Error = error,
            CompletedAtUtc = completedAtUtc,
            RetryAtUtc = retryAtUtc,
        };

    private McpActionDispatchCompletion BuildPolicyFailure(McpActionDispatchLease lease, string error)
        => new()
        {
            ActionId = lease.ActionId,
            AttemptNumber = lease.AttemptNumber,
            State = McpActionState.Failed,
            FailureKind = McpFailureKind.Policy,
            Error = error,
            CompletedAtUtc = this.timeProvider.GetUtcNow(),
        };

    private static McpServerConfig? FindServer(McpSettingsConfig settings, string serverKey)
        => settings.Servers.FirstOrDefault(s => string.Equals(s.Key, serverKey, StringComparison.OrdinalIgnoreCase));

    [LoggerMessage(Level = LogLevel.Information, Message = "Dispatching approved MCP action {ActionId} to {ServerKey}/{ToolName} (attempt {AttemptNumber})")]
    private partial void LogDispatching(string actionId, string serverKey, string toolName, int attemptNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP action {ActionId} deferred: server '{ServerKey}' unavailable before dispatch (attempt {AttemptNumber}); released back to approved with backoff")]
    private partial void LogDeferredUnavailable(string actionId, string serverKey, int attemptNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP action {ActionId} refused at dispatch time for {ServerKey}/{ToolName}: {Reason}")]
    private partial void LogPolicyRefused(string actionId, string serverKey, string toolName, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovered {Count} interrupted MCP action dispatch(es) to outcome_unknown at startup")]
    private partial void LogRecoveredInterrupted(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Duplicate/late completion for MCP action {ActionId} attempt {AttemptNumber} ignored: {Detail}")]
    private partial void LogDuplicateCompletionIgnored(string actionId, int attemptNumber, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "MCP action outbox iteration failed: {ErrorMessage}")]
    private partial void LogLoopIterationFailed(string errorMessage);
}
