namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// System, status, credentials, and maintenance methods exposed by the agent.
/// Part of the composed <see cref="IAgentHub"/> surface — these methods share the
/// single SignalR hub connection and route by method name.
/// </summary>
public interface ISystemHub
{
    /// <summary>Get the agent's current status.</summary>
    Task<AgentStatusInfo> GetStatus();

    /// <summary>Update agent configuration at runtime.</summary>
    Task UpdateConfig(AgentConfigUpdate config);

    /// <summary>
    /// Push LLM provider credentials to the agent.
    /// Called by the Bridge after connecting so the agent can call LLM providers directly.
    /// Credentials are held in memory only — never persisted to disk.
    /// </summary>
    Task ProvideCredentials(LlmCredentials credentials);

    /// <summary>
    /// Tell the agent which channels are currently active on the Bridge.
    /// Tools use this list to validate channel targets and to inform the LLM
    /// which channels are available for message delivery.
    /// Called after initial connect and after reconnect.
    /// </summary>
    Task SetActiveChannels(string[] channelIds);

    /// <summary>Health check.</summary>
    Task<HealthInfo> Ping();

    /// <summary>Clear all data: messages and memories.</summary>
    Task ClearAll();

    /// <summary>
    /// Flush extraction buffer and compact conversation for a channel.
    /// Simulates the idle compaction that fires after an inactivity timeout.
    /// </summary>
    Task<CompactConversationResult> CompactConversation(string channelId);

    /// <summary>
    /// Run an inline slash command (e.g. <c>/compact</c>, <c>/context</c>) for a
    /// channel and return the human-readable response text. Used by the Bridge
    /// to back Discord application slash commands that mirror the existing
    /// text-prefix triggers handled by AgentRuntime. Parameter-less today;
    /// dispatches by the leading <c>/word</c> in <paramref name="commandText"/>.
    /// </summary>
    Task<string> RunInlineSlashCommand(string channelId, string commandText);
}
