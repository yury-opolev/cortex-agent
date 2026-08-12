namespace Cortex.Contained.Contracts.Coding;

/// <summary>Which kind of coda server-request is parked awaiting a response.</summary>
public enum PendingCodingRequestKind
{
    /// <summary>A tool wants permission to run (coda <c>request/permission</c>).</summary>
    Permission = 0,

    /// <summary>The agent asked the user a clarifying question (coda <c>request/question</c>).</summary>
    Question = 1,

    /// <summary>A plan is waiting for approval (coda <c>request/planApproval</c>).</summary>
    Plan = 2,
}

/// <summary>
/// Describes the prompt a coding session is currently blocked on.
/// <para>
/// The <see cref="RequestId"/> is minted Bridge-side and is the only key
/// <c>coding_session_respond</c> accepts. It used to reach the model exactly once, as text inside
/// an injected envelope, and was recorded nowhere else — so a dropped injection, a context
/// compaction or an agent-host restart left the session permanently unanswerable. Carrying the
/// prompt on <see cref="CodingStatus"/> makes it queryable instead of ephemeral.
/// </para>
/// </summary>
public sealed record PendingCodingRequest
{
    /// <summary>The id to pass back to <c>coding_session_respond</c>.</summary>
    public required string RequestId { get; init; }

    public required PendingCodingRequestKind Kind { get; init; }

    /// <summary>Tool being gated, for <see cref="PendingCodingRequestKind.Permission"/>.</summary>
    public string? ToolName { get; init; }

    /// <summary>Human-readable preview of the gated tool input, for <see cref="PendingCodingRequestKind.Permission"/>.</summary>
    public string? InputPreview { get; init; }

    /// <summary>The question asked, for <see cref="PendingCodingRequestKind.Question"/>.</summary>
    public string? Question { get; init; }

    /// <summary>Offered answers, for <see cref="PendingCodingRequestKind.Question"/>.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>The plan awaiting approval, for <see cref="PendingCodingRequestKind.Plan"/>.</summary>
    public string? Plan { get; init; }

    /// <summary>When the prompt was parked (UTC). Drives the unanswered-prompt timeout.</summary>
    public required DateTimeOffset RequestedAt { get; init; }
}
