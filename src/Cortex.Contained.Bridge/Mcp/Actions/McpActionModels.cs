using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Mcp.Actions;

/// <summary>
/// Lifecycle state of an approval-gated MCP mutation.
/// Allowed transitions:
/// <code>
/// proposed -&gt; approved | rejected | cancelled | expired
/// approved -&gt; dispatching | cancelled | expired
/// dispatching -&gt; succeeded | failed | outcome_unknown
/// dispatching -&gt; approved (only when dispatch is positively known not to have started)
/// outcome_unknown -&gt; reconciled_succeeded | reconciled_failed
/// </code>
/// All other states are terminal. Terminal states never dispatch again.
/// </summary>
public enum McpActionState
{
    Proposed,
    Approved,
    Rejected,
    Dispatching,
    Succeeded,
    Failed,
    OutcomeUnknown,
    ReconciledSucceeded,
    ReconciledFailed,
    Expired,
    Cancelled,
}

/// <summary>A durable record of one proposed/approved/dispatched MCP mutation.</summary>
public sealed record McpAction
{
    public required string ActionId { get; init; }
    public required string TenantId { get; init; }
    public required string InvocationId { get; init; }
    public string? CorrelationId { get; init; }
    public string? ConversationId { get; init; }
    public string? ChannelId { get; init; }
    public string? WorkerId { get; init; }
    public required string ServerKey { get; init; }
    public required string ToolName { get; init; }
    public required string CanonicalArgumentsJson { get; init; }
    public required string ArgumentsHash { get; init; }
    public required McpActionState State { get; init; }
    public required DateTimeOffset ProposalExpiresAtUtc { get; init; }
    public DateTimeOffset? ApprovalExpiresAtUtc { get; init; }
    public DateTimeOffset? NextAttemptAtUtc { get; init; }
    public string? ResultContent { get; init; }
    public string? Error { get; init; }
    public string? RemoteReference { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public int Version { get; init; }
}

/// <summary>Request to record a new proposed MCP mutation awaiting human approval.</summary>
public sealed record McpActionProposal
{
    public required string TenantId { get; init; }
    public required string InvocationId { get; init; }
    public string? CorrelationId { get; init; }
    public string? ConversationId { get; init; }
    public string? ChannelId { get; init; }
    public string? WorkerId { get; init; }
    public required string ServerKey { get; init; }
    public required string ToolName { get; init; }
    public required string CanonicalArgumentsJson { get; init; }
    public required string ArgumentsHash { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ProposalExpiresAtUtc { get; init; }
}

/// <summary>Filter for listing actions of one tenant, newest first, keyed by <see cref="BeforeActionId"/> paging.</summary>
public sealed record McpActionQuery
{
    public required string TenantId { get; init; }
    public string? BeforeActionId { get; init; }
    public int Limit { get; init; } = 100;
    public string? ServerKey { get; init; }
    public string? ToolName { get; init; }
    public McpActionState? State { get; init; }
    public string? WorkerId { get; init; }
}

/// <summary>Outcome of an approve/reject/reconcile call. A stale <c>expectedArgumentsHash</c> yields <c>Succeeded = false</c> without mutating.</summary>
public sealed record McpActionDecisionResult(
    bool Succeeded,
    McpAction? Action,
    string? Error);

/// <summary>Outcome of a cancel call. A stale <c>expectedArgumentsHash</c> yields <c>Accepted = false</c> without mutating.</summary>
public sealed record McpActionCancelResult(
    bool Accepted,
    McpAction? Action,
    string? Error);

/// <summary>An exclusive claim on one approved action for a single dispatch attempt.</summary>
public sealed record McpActionDispatchLease
{
    public required string ActionId { get; init; }
    public required int AttemptNumber { get; init; }
    public required string InvocationId { get; init; }
    public required string TenantId { get; init; }
    public required string ServerKey { get; init; }
    public required string ToolName { get; init; }
    public required string CanonicalArgumentsJson { get; init; }
    public required string ArgumentsHash { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
}

/// <summary>
/// Result of a finished dispatch attempt. <see cref="State"/> accepts only
/// <see cref="McpActionState.Approved"/>, <see cref="McpActionState.Succeeded"/>,
/// <see cref="McpActionState.Failed"/>, or <see cref="McpActionState.OutcomeUnknown"/>.
/// <see cref="McpActionState.Approved"/> means the attempt was positively known not to have
/// reached the remote server and may be retried at <see cref="RetryAtUtc"/>.
/// </summary>
public sealed record McpActionDispatchCompletion
{
    public required string ActionId { get; init; }
    public required int AttemptNumber { get; init; }
    public required McpActionState State { get; init; }
    public required McpFailureKind FailureKind { get; init; }
    public string? ResultContent { get; init; }
    public string? Error { get; init; }
    public string? RemoteReference { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public DateTimeOffset? RetryAtUtc { get; init; }
}
