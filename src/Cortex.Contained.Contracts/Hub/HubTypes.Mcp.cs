namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// A single MCP tool exposed to the agent, namespaced under its owning server.
/// Pushed from the Bridge (the MCP host) to the agent as part of an
/// <see cref="McpToolCatalog"/>.
/// </summary>
public sealed record McpToolDefinition
{
    /// <summary>The owning MCP server's key (e.g. <c>github</c>).</summary>
    public required string ServerKey { get; init; }

    /// <summary>The tool's name as reported by the MCP server (e.g. <c>create_issue</c>).</summary>
    public required string ToolName { get; init; }

    /// <summary>Namespaced agent-facing name: <c>mcp__{ServerKey}__{ToolName}</c>.</summary>
    public required string FullName { get; init; }

    /// <summary>Human-readable description for the LLM.</summary>
    public required string Description { get; init; }

    /// <summary>JSON Schema (string) for the tool's parameters.</summary>
    public required string ParametersSchemaJson { get; init; }

    /// <summary>
    /// True when the tool is classified as a mutation by explicit admin policy
    /// (<c>MutationToolAllowList</c>) and therefore requires a human-approved action flow;
    /// the direct invocation path refuses it. Defaults to false (read-only) — classification
    /// is never inferred from tool names or untrusted MCP annotations.
    /// </summary>
    public bool RequiresApproval { get; init; }
}

/// <summary>
/// Full, replace-all catalog of currently-available MCP tools across all enabled
/// servers. The Bridge re-pushes this whenever the available tool set changes.
/// </summary>
public sealed record McpToolCatalog
{
    /// <summary>The complete set of currently-available MCP tools.</summary>
    public IReadOnlyList<McpToolDefinition> Tools { get; init; } = [];
}

/// <summary>
/// Explicit outcome of an MCP tool invocation. The critical distinction is between a
/// definitive <see cref="Failed"/> (the call verifiably did not take effect, or the server
/// itself reported the failure) and <see cref="OutcomeUnknown"/> (the request was dispatched
/// but the answer never arrived — the call MAY have executed). An
/// <see cref="OutcomeUnknown"/> invocation must NEVER be retried automatically: repeating a
/// mutating call could double-execute its side effect.
/// </summary>
public enum McpToolOutcome
{
    /// <summary>The tool ran and returned a successful result.</summary>
    Succeeded,

    /// <summary>Definitive failure: rejected before dispatch, or the server reported an error.</summary>
    Failed,

    /// <summary>Definitive cancellation before the request was dispatched.</summary>
    Cancelled,

    /// <summary>
    /// Ambiguous post-dispatch failure (timeout, transport loss, or in-flight cancellation).
    /// The call may have executed. Never auto-retry; inspect action status or remote state.
    /// </summary>
    OutcomeUnknown,
}

/// <summary>Classifies why an MCP tool invocation did not succeed.</summary>
public enum McpFailureKind
{
    /// <summary>No failure (the invocation succeeded).</summary>
    None,

    /// <summary>The request was malformed (e.g. arguments were not valid JSON).</summary>
    Validation,

    /// <summary>Blocked by policy (e.g. the tool is excluded by the server's allow-list).</summary>
    Policy,

    /// <summary>The target server or the Bridge was unavailable before dispatch.</summary>
    Unavailable,

    /// <summary>The server needs (re-)authorization before it can be used.</summary>
    Authentication,

    /// <summary>The MCP server itself reported a tool error (<c>isError</c> response).</summary>
    Tool,

    /// <summary>The bounded invocation timeout elapsed after dispatch started.</summary>
    Timeout,

    /// <summary>The transport failed after dispatch started.</summary>
    Transport,

    /// <summary>The invocation was cancelled.</summary>
    Cancellation,
}

/// <summary>
/// An agent-initiated request to invoke a single MCP tool. Routed Agent → Bridge
/// over the hub; the Bridge attaches auth and calls the real MCP server.
/// </summary>
public sealed record McpToolInvocation
{
    /// <summary>
    /// Stable, unique id for this invocation (uuidv7, <c>"N"</c> format). Generated exactly
    /// once per Agent → Bridge dispatch and threaded end to end so cancellation, results,
    /// and audit records all refer to the same attempt.
    /// </summary>
    public required string InvocationId { get; init; }

    /// <summary>The target MCP server's key.</summary>
    public required string ServerKey { get; init; }

    /// <summary>The tool's name as reported by the MCP server.</summary>
    public required string ToolName { get; init; }

    /// <summary>JSON-encoded tool arguments.</summary>
    public required string ArgumentsJson { get; init; }

    /// <summary>Originating conversation id, for tracing. Null when not in scope.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Originating channel id (e.g. <c>webchat-default</c>). Null when not in scope.</summary>
    public string? ChannelId { get; init; }

    /// <summary>Originating turn correlation id, for end-to-end tracing. Null when not in scope.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Originating subagent worker id. Null when invoked by the main agent.</summary>
    public string? WorkerId { get; init; }
}

/// <summary>
/// How an MCP tool invocation was disposed of by the Bridge. A mutation-classified tool never
/// executes inline: it is recorded as a proposed action and the invocation completes
/// successfully with <see cref="AwaitingApproval"/> — the actual dispatch happens later,
/// after a human approves the exact canonical arguments.
/// </summary>
public enum McpToolDisposition
{
    /// <summary>The invocation ran to completion (the normal read-tool path).</summary>
    Completed,

    /// <summary>
    /// The invocation was recorded as a proposed mutation awaiting exact-argument human
    /// approval. Nothing was dispatched to the remote server. The caller must NOT repeat
    /// the call — the action id tracks the pending mutation.
    /// </summary>
    AwaitingApproval,
}

/// <summary>
/// The result of an MCP tool invocation, mapped back to the agent over the hub. Carries an
/// explicit <see cref="Outcome"/> instead of a bare error flag so callers can distinguish a
/// definitive failure (safe to retry deliberately) from an ambiguous
/// <see cref="McpToolOutcome.OutcomeUnknown"/> (must never be retried automatically).
/// </summary>
public sealed record McpToolResult
{
    /// <summary>The id of the invocation this result answers.</summary>
    public required string InvocationId { get; init; }

    /// <summary>The explicit outcome of the invocation.</summary>
    public required McpToolOutcome Outcome { get; init; }

    /// <summary>The durable action id when the invocation proposed an approval-gated mutation.</summary>
    public string? ActionId { get; init; }

    /// <summary>The canonical-argument hash the pending approval is bound to, when applicable.</summary>
    public string? ArgumentsHash { get; init; }

    /// <summary>How the invocation was disposed of. Defaults to <see cref="McpToolDisposition.Completed"/>.</summary>
    public McpToolDisposition Disposition { get; init; }

    /// <summary>Why the invocation did not succeed; <see cref="McpFailureKind.None"/> on success.</summary>
    public McpFailureKind FailureKind { get; init; }

    /// <summary>Flattened MCP content (text/json) on success; empty otherwise.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>True when the server needs authorization before it can be used.</summary>
    public bool NeedsAuth { get; init; }

    /// <summary>Error message when the invocation did not succeed; otherwise null.</summary>
    public string? Error { get; init; }

    /// <summary>Whether the invocation did not succeed (any non-<see cref="McpToolOutcome.Succeeded"/> outcome).</summary>
    public bool IsError => this.Outcome != McpToolOutcome.Succeeded;

    /// <summary>A successful result carrying <paramref name="content"/>.</summary>
    public static McpToolResult Ok(string invocationId, string content) => new()
    {
        InvocationId = invocationId,
        Outcome = McpToolOutcome.Succeeded,
        Content = content,
    };

    /// <summary>
    /// A definitive failure: the invocation verifiably did not take effect, or the server itself
    /// reported the error. Optionally flagged as needing auth.
    /// </summary>
    public static McpToolResult Fail(string invocationId, McpFailureKind failureKind, string error, bool needsAuth = false) => new()
    {
        InvocationId = invocationId,
        Outcome = McpToolOutcome.Failed,
        FailureKind = failureKind,
        Error = error,
        NeedsAuth = needsAuth,
    };

    /// <summary>A definitive cancellation before the request was dispatched.</summary>
    public static McpToolResult Cancelled(string invocationId, string error) => new()
    {
        InvocationId = invocationId,
        Outcome = McpToolOutcome.Cancelled,
        FailureKind = McpFailureKind.Cancellation,
        Error = error,
    };

    /// <summary>
    /// An ambiguous post-dispatch failure: the call may have executed. Callers must never
    /// auto-retry this invocation.
    /// </summary>
    public static McpToolResult Unknown(string invocationId, McpFailureKind failureKind, string error) => new()
    {
        InvocationId = invocationId,
        Outcome = McpToolOutcome.OutcomeUnknown,
        FailureKind = failureKind,
        Error = error,
    };

    /// <summary>
    /// A SUCCESSFUL result recording that the invocation was persisted as a proposed mutation
    /// awaiting exact-argument approval — deliberately not an error, so no retry machinery
    /// ever re-issues the mutation. <paramref name="content"/> carries the agent-facing JSON
    /// payload describing the pending action.
    /// </summary>
    public static McpToolResult AwaitingApproval(string invocationId, string actionId, string argumentsHash, string content) => new()
    {
        InvocationId = invocationId,
        Outcome = McpToolOutcome.Succeeded,
        Content = content,
        ActionId = actionId,
        ArgumentsHash = argumentsHash,
        Disposition = McpToolDisposition.AwaitingApproval,
    };
}

/// <summary>Agent → Bridge request for the current status of one approval-gated MCP action.</summary>
public sealed record McpActionStatusRequest
{
    /// <summary>The durable action id (as returned in an awaiting-approval tool result).</summary>
    public required string ActionId { get; init; }
}

/// <summary>Bridge → Agent answer to an <see cref="McpActionStatusRequest"/>.</summary>
public sealed record McpActionStatusResponse
{
    /// <summary>True when the action exists for the requesting tenant.</summary>
    public required bool Found { get; init; }

    /// <summary>The action id, when found.</summary>
    public string? ActionId { get; init; }

    /// <summary>The action's lifecycle status (snake_case, e.g. <c>proposed</c>, <c>outcome_unknown</c>).</summary>
    public string? Status { get; init; }

    /// <summary>The canonical-argument hash the action is bound to.</summary>
    public string? ArgumentsHash { get; init; }

    /// <summary>The target MCP server key.</summary>
    public string? ServerKey { get; init; }

    /// <summary>The target tool name.</summary>
    public string? ToolName { get; init; }

    /// <summary>The dispatched tool's result content, when the action succeeded.</summary>
    public string? ResultContent { get; init; }

    /// <summary>The most recent error recorded on the action, if any.</summary>
    public string? Error { get; init; }

    /// <summary>A remote-system reference recorded on completion/reconciliation, if any.</summary>
    public string? RemoteReference { get; init; }
}

/// <summary>
/// Agent → Bridge request to cancel one approval-gated MCP action. The
/// <see cref="ArgumentsHash"/> must match the stored canonical hash exactly — a stale hash
/// never mutates anything.
/// </summary>
public sealed record McpActionCancelRequest
{
    /// <summary>The durable action id to cancel.</summary>
    public required string ActionId { get; init; }

    /// <summary>The exact canonical-argument hash the cancel is bound to.</summary>
    public required string ArgumentsHash { get; init; }
}

/// <summary>
/// Bridge → Agent answer to an <see cref="McpActionCancelRequest"/>. A dispatching action is
/// never reported cancelled: the dispatch outcome decides (a call that already reached the
/// remote server resolves to <c>outcome_unknown</c>, never <c>cancelled</c>).
/// </summary>
public sealed record McpActionCancelResponse
{
    /// <summary>True when the cancel request was accepted (immediately applied or recorded).</summary>
    public required bool Accepted { get; init; }

    /// <summary>The action's status after the cancel request (snake_case).</summary>
    public string? Status { get; init; }

    /// <summary>Why the cancel was not accepted, if it was not.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// A best-effort request to cancel an in-flight MCP tool invocation, routed Agent → Bridge.
/// Identified by the invocation's stable id; cancellation of an already-completed or unknown
/// invocation is a no-op.
/// </summary>
public sealed record McpToolCancellation
{
    /// <summary>The id of the invocation to cancel.</summary>
    public required string InvocationId { get; init; }

    /// <summary>Optional human-readable reason (e.g. <c>caller cancelled</c>, <c>timed out</c>).</summary>
    public string? Reason { get; init; }
}
