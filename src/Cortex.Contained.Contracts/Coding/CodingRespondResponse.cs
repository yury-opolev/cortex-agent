namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Outcome of replying to a parked permission / question / plan prompt.
/// <para>
/// This type exists so the call is a REQUEST, not a notification. SignalR's typed-client proxy
/// emits a fire-and-forget send for a <c>Task</c>-returning client method and only a real
/// invocation for <c>Task&lt;T&gt;</c> — so with no result type the Bridge's
/// <c>unknown_request</c> failure was raised, logged, and then silently dropped on the wire while
/// the tool still told the model the response had been accepted.
/// </para>
/// </summary>
public sealed record CodingRespondResponse
{
    public required string RequestId { get; init; }

    /// <summary>True when a parked prompt was found and resolved by this response.</summary>
    public required bool Accepted { get; init; }
}
