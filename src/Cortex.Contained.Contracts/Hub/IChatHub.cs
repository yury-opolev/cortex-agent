using Cortex.Contained.Contracts.SystemPrompt;

namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Chat, session lifecycle, and personality methods exposed by the agent.
/// Part of the composed <see cref="IAgentHub"/> surface — these methods share the
/// single SignalR hub connection and route by method name.
/// </summary>
public interface IChatHub
{
    /// <summary>Send a user message to the agent for processing.</summary>
    Task<SendMessageResult> SendMessage(HubInboundMessage message);

    /// <summary>Abort an in-progress generation.</summary>
    Task AbortGeneration(string conversationId);

    /// <summary>
    /// Seed the agent's session for a channel with historical messages from the Bridge.
    /// Called before the first SendMessage for a channel after Bridge (re)start.
    /// If the session already has messages, they are replaced.
    /// </summary>
    Task SeedHistory(string channelId, HubChatMessage[] messages);

    /// <summary>
    /// Reset (clear) the agent's in-memory session for a channel.
    /// Called when history is cleared from the Bridge.
    /// </summary>
    Task ResetSession(string channelId);

    /// <summary>
    /// Reset all agent sessions. Called when all history is cleared.
    /// </summary>
    Task ResetAllSessions();

    /// <summary>
    /// Bridge → Agent. Record a barge-in: replace the in-flight assistant turn
    /// with the truncated text the user actually heard. Idempotent per turn.
    /// </summary>
    Task OnTurnInterrupted(TurnInterruptedNotification notification);

    /// <summary>Get the agent's personality (system prompt from personality.md).</summary>
    Task<string> GetPersonality();

    /// <summary>Set the agent's personality (writes personality.md and reloads the system prompt).</summary>
    Task SetPersonality(string personality);

    /// <summary>Get the agent's self-notes (operating principles).</summary>
    Task<string> GetSelfNotes();

    /// <summary>Set the agent's self-notes (operating principles).</summary>
    Task SetSelfNotes(string content);

    /// <summary>Reset self-notes to default content and return the default.</summary>
    Task<string> ResetSelfNotes();

    /// <summary>
    /// Reset the in-memory session for a channel and re-seed from persisted message history.
    /// Unlike <see cref="ResetSession"/> (which just clears), this reloads recent messages.
    /// </summary>
    Task ResetAndReseedSession(string channelId);

    /// <summary>Get the active system-prompt configuration (templates + authorable segments).</summary>
    Task<SystemPromptConfig> GetSystemPromptConfig();

    /// <summary>
    /// Validate and, if valid, persist a new system-prompt configuration. Returns the
    /// validation result — an invalid config is rejected without being persisted.
    /// </summary>
    Task<SystemPromptValidationResult> SetSystemPromptConfig(SystemPromptConfig config);

    /// <summary>Reset the system-prompt configuration to defaults and return it.</summary>
    Task<SystemPromptConfig> ResetSystemPromptConfig();

    /// <summary>
    /// Render and return the exact system prompt the model would receive for a given
    /// channel, using the live self-notes/skills/operational state and the stored templates.
    /// </summary>
    Task<string> GetSystemPromptPreview(string channelId, bool isVoice);
}
