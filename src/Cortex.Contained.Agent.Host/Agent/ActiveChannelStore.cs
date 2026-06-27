namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Thread-safe store for the list of active channel IDs pushed by the Bridge.
/// Shared between <see cref="AgentRuntime"/> (which sets the channels) and
/// tools (which read them to validate channel targets).
/// </summary>
public sealed class ActiveChannelStore
{
    private volatile string[] activeChannels = [];
    private volatile int version;

    /// <summary>
    /// Set the list of active channel IDs.
    /// Called by <see cref="AgentRuntime.SetActiveChannels"/>.
    /// Increments <see cref="Version"/> so consumers can detect changes.
    /// </summary>
    public void Set(string[] channelIds)
    {
        this.activeChannels = channelIds;
        Interlocked.Increment(ref this.version);
    }

    /// <summary>
    /// Get the current list of active channel IDs.
    /// Returns an empty array if not yet set.
    /// </summary>
    public IReadOnlyList<string> Get() => this.activeChannels;

    /// <summary>
    /// Monotonically increasing version counter. Incremented each time
    /// <see cref="Set"/> is called. Consumers can compare against a cached
    /// version to detect when tool definitions need rebuilding.
    /// </summary>
    public int Version => this.version;
}
