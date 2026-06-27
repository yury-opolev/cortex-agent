namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>Default in-memory <see cref="IProactiveMessageCollector"/>; one per turn.</summary>
public sealed class ProactiveMessageCollector : IProactiveMessageCollector
{
    private readonly List<ProactiveMessageRecord> messages = [];

    /// <inheritdoc />
    public IReadOnlyList<ProactiveMessageRecord> Collected => this.messages;

    /// <inheritdoc />
    public void Add(ProactiveMessageRecord message)
    {
        ArgumentNullException.ThrowIfNull(message);
        this.messages.Add(message);
    }
}
