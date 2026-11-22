namespace Cortex.Contained.Agent.Host.Events;

/// <summary>One piece of evidence behind a delivery: a recorded tool result of the producing run.</summary>
/// <param name="EventId">The <c>tool_result</c> evidence ID.</param>
/// <param name="Tool">Tool name.</param>
/// <param name="Ok">Whether the tool succeeded.</param>
/// <param name="Digest">Redacted digest (allowlisted tools only), or null.</param>
public sealed record DeliveryEvidence(string EventId, string Tool, bool Ok, string? Digest);

/// <summary>
/// What the target conversation is told about a message delivered from elsewhere: who produced it,
/// when, in which run, its send state, and the evidence behind it — rendered from events.
/// </summary>
public sealed record DeliveryReport
{
    /// <summary>The channel the message was delivered to.</summary>
    public required string ChannelId { get; init; }

    /// <summary>When the send was attempted.</summary>
    public required DateTimeOffset AttemptedAt { get; init; }

    /// <summary><c>acknowledged</c>, <c>rejected</c> or <c>unknown</c>.</summary>
    public required string State { get; init; }

    /// <summary>Producer kind (see <see cref="AuthorKinds"/>).</summary>
    public required string ProducerKind { get; init; }

    /// <summary>Task or subagent ID, when known.</summary>
    public string? ProducerId { get; init; }

    /// <summary>Human-readable producer, e.g. <c>scheduled task 1bffa504 ("Morning brief")</c>.</summary>
    public string? ProducerLabel { get; init; }

    /// <summary>The producing run.</summary>
    public string? RunId { get; init; }

    /// <summary>The send operation.</summary>
    public string? OperationId { get; init; }

    /// <summary>The <c>delivery_attempted</c> event ID.</summary>
    public string? DeliveryEventId { get; init; }

    /// <summary>Recorded tool results of the producing run before the send.</summary>
    public IReadOnlyList<DeliveryEvidence> Evidence { get; init; } = [];

    /// <summary>False when the events could not be read (the report then says so).</summary>
    public bool EvidenceAvailable { get; init; } = true;
}
