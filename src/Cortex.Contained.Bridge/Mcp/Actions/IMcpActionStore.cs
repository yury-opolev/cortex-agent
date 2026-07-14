namespace Cortex.Contained.Bridge.Mcp.Actions;

/// <summary>
/// Durable, encrypted store of record for approval-gated MCP mutations. Every proposed,
/// approved, and dispatched mutation is persisted with its canonical-argument hash so a human
/// approval binds to exact arguments and the outbox can dispatch it at-most-once with crash
/// recovery. <c>expectedArgumentsHash</c> parameters implement optimistic concurrency: a
/// mismatch returns a distinct non-mutating result instead of silently proceeding.
/// </summary>
public interface IMcpActionStore : IAsyncDisposable
{
    /// <summary>
    /// Records a new proposed mutation. Idempotent on (tenant, invocation): re-proposing an
    /// already-recorded invocation returns the existing action. A proposal whose
    /// (tenant, server, tool, arguments-hash) fingerprint matches an ACTIVE action
    /// (proposed/approved/dispatching/outcome_unknown) deduplicates to that action.
    /// </summary>
    Task<McpAction> ProposeAsync(McpActionProposal proposal, CancellationToken cancellationToken);

    /// <summary>Loads one action by tenant and id, or null when it does not exist.</summary>
    Task<McpAction?> GetAsync(string tenantId, string actionId, CancellationToken cancellationToken);

    /// <summary>Lists a tenant's actions, newest first, filtered by the query.</summary>
    Task<IReadOnlyList<McpAction>> ListAsync(McpActionQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Transitions proposed → approved when <paramref name="expectedArgumentsHash"/> matches the
    /// stored hash and the proposal has not expired. The approval expires at <paramref name="expiresAtUtc"/>.
    /// </summary>
    Task<McpActionDecisionResult> ApproveAsync(string tenantId, string actionId, string expectedArgumentsHash, string actor, string? reason, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken);

    /// <summary>Transitions proposed → rejected when <paramref name="expectedArgumentsHash"/> matches.</summary>
    Task<McpActionDecisionResult> RejectAsync(string tenantId, string actionId, string expectedArgumentsHash, string actor, string? reason, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels a proposed or approved action (immediate transition to cancelled). For a
    /// dispatching action the cancel request is only recorded — the dispatch outcome decides.
    /// </summary>
    Task<McpActionCancelResult> CancelAsync(string tenantId, string actionId, string expectedArgumentsHash, string actor, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims the next due approved action (approved → dispatching) and opens a new
    /// dispatch attempt. Returns null when nothing is claimable. At most one caller obtains a
    /// lease for any given action.
    /// </summary>
    Task<McpActionDispatchLease?> TryClaimNextApprovedAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records the outcome of a dispatch attempt. The completion state must be Succeeded,
    /// Failed, OutcomeUnknown, or Approved (positively known not to have started; retried at
    /// <see cref="McpActionDispatchCompletion.RetryAtUtc"/>). Throws when the action is not dispatching.
    /// </summary>
    Task CompleteAttemptAsync(McpActionDispatchCompletion completion, CancellationToken cancellationToken);

    /// <summary>
    /// Crash recovery: marks every leftover dispatching action outcome_unknown (never silently
    /// succeeded) in one pass. Idempotent. Returns the number of recovered actions.
    /// </summary>
    Task<int> RecoverInterruptedDispatchesAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Expires proposed actions past their proposal deadline and approved actions past their
    /// approval deadline. Returns the number of expired actions.
    /// </summary>
    Task<int> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an outcome_unknown action to reconciled_succeeded or reconciled_failed based on
    /// human/remote evidence. Only accepts actions in outcome_unknown.
    /// </summary>
    Task<McpActionDecisionResult> ReconcileAsync(string tenantId, string actionId, string expectedArgumentsHash, bool succeeded, string actor, string evidence, string? remoteReference, CancellationToken cancellationToken);
}
