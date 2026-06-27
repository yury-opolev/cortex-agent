namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Collects proactive messages emitted by tools during a turn (via <c>send_message</c> /
/// session transfer) so the runtime can inject them into target channels' history after the
/// tool loop. Tools may only Add; the runtime reads <see cref="Collected"/>. Replaces the
/// previously-exposed raw mutable list on <see cref="ToolExecutionContext"/>.
/// </summary>
public interface IProactiveMessageCollector
{
    /// <summary>Records a proactive message sent during this turn.</summary>
    void Add(ProactiveMessageRecord message);

    /// <summary>The proactive messages collected so far, in send order.</summary>
    IReadOnlyList<ProactiveMessageRecord> Collected { get; }
}
