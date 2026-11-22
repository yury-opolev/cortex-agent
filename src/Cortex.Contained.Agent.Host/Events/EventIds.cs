using System.Buffers;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>
/// Identifiers for the event log: evidence IDs (<c>ev_</c>), runs (<c>run_</c>) and send
/// operations (<c>op_</c>). Each is a prefix plus a UUIDv7 (millisecond time prefix) in 32
/// lowercase hex characters.
/// </summary>
public static class EventIds
{
    /// <summary>Prefix of an event (evidence) ID.</summary>
    public const string EventPrefix = "ev_";

    /// <summary>Prefix of a run ID.</summary>
    public const string RunPrefix = "run_";

    /// <summary>Prefix of a send-operation ID.</summary>
    public const string OperationPrefix = "op_";

    private static readonly SearchValues<char> hex = SearchValues.Create("0123456789abcdef");

    /// <summary>A new event (evidence) ID.</summary>
    public static string NewEventId() => EventPrefix + Next();

    /// <summary>A new run ID.</summary>
    public static string NewRunId() => RunPrefix + Next();

    /// <summary>A new send-operation ID.</summary>
    public static string NewOperationId() => OperationPrefix + Next();

    /// <summary>True when <paramref name="value"/> has the exact shape of an event ID.</summary>
    public static bool IsEventId(string? value) =>
        value is { Length: 35 }
        && value.StartsWith(EventPrefix, StringComparison.Ordinal)
        && !value.AsSpan(EventPrefix.Length).ContainsAnyExcept(hex);

    private static string Next() => Guid.CreateVersion7().ToString("N");
}
