namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Push event when the coding agent requests plan approval (coda <c>request/planApproval</c>).
/// The session blocks until <see cref="CodingRespondRequest"/> arrives.
/// </summary>
public sealed record CodingPlanApprovalRequestEvent
{
    public required string SessionId { get; init; }

    public required string RequestId { get; init; }

    public required string Plan { get; init; }
}
