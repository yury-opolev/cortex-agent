using System.Text.Json.Nodes;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>An event to append. <see cref="Payload"/> is policy-filtered JSON and never holds message content.</summary>
/// <param name="Type">See <see cref="EventTypes"/>.</param>
/// <param name="AuthorKind">See <see cref="AuthorKinds"/>.</param>
public sealed record EventDraft(string Type, string AuthorKind)
{
    /// <summary>Task or subagent ID, when there is one.</summary>
    public string? AuthorId { get; init; }

    /// <summary>The conversation (erasure domain) the event belongs to, or null.</summary>
    public string? ChannelId { get; init; }

    /// <summary>The run that produced the event, or null.</summary>
    public string? RunId { get; init; }

    /// <summary>The event that caused this one (a call for a result, the original for a correction).</summary>
    public string? CausationId { get; init; }

    /// <summary><c>Messages.Id</c> when the event describes a stored message.</summary>
    public long? MessageRowId { get; init; }

    /// <summary>Send-operation ID for delivery events.</summary>
    public string? OperationId { get; init; }

    /// <summary>Unique key that makes the append idempotent (backfill, delivery states).</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Event time; defaults to now.</summary>
    public DateTimeOffset? Ts { get; init; }

    /// <summary>Content-free JSON payload (≤ 8 KB serialized).</summary>
    public JsonObject Payload { get; init; } = new();

    /// <summary>A draft attributed to <paramref name="run"/> (or to the main agent outside a run).</summary>
    public static EventDraft ForRun(string type, RunProvenance? run) =>
        new(type, run?.AuthorKind ?? AuthorKinds.Main)
        {
            AuthorId = run?.AuthorId,
            ChannelId = run?.ChannelId,
            RunId = run?.RunId,
        };
}

/// <summary>A stored event.</summary>
public sealed record EventRecord(
    long Seq,
    string Id,
    DateTimeOffset Ts,
    string Type,
    string AuthorKind,
    string? AuthorId,
    string? ChannelId,
    string? RunId,
    string? CausationId,
    long? MessageRowId,
    string? OperationId,
    JsonObject Payload);
