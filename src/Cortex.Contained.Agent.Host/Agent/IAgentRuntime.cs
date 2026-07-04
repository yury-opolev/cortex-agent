using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Core agent runtime interface. Handles message processing,
/// session management, and conversation lifecycle.
/// </summary>
public interface IAgentRuntime
{
    /// <summary>
    /// Enqueue an inbound message for processing. Returns immediately with an
    /// acceptance result. The actual LLM generation happens asynchronously
    /// in the consumer loop.
    /// </summary>
    Task<SendMessageResult> HandleMessageAsync(
        HubInboundMessage message,
        CancellationToken cancellationToken);

    /// <summary>
    /// Start the background consumer loop that processes enqueued messages.
    /// Called once at application startup.
    /// </summary>
    Task StartProcessingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stop the consumer loop and drain remaining messages.
    /// Called at application shutdown.
    /// </summary>
    Task StopProcessingAsync(CancellationToken cancellationToken);

    /// <summary>Abort an in-progress generation.</summary>
    Task AbortGenerationAsync(string conversationId);

    /// <summary>Get the agent's current status.</summary>
    Task<AgentStatusInfo> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Update the agent's configuration at runtime.</summary>
    Task UpdateConfigAsync(AgentConfigUpdate config, CancellationToken cancellationToken);

    /// <summary>Get the current personality (system prompt from personality.md).</summary>
    Task<string> GetPersonalityAsync(CancellationToken cancellationToken);

    /// <summary>Set the personality (writes personality.md and reloads the system prompt).</summary>
    Task SetPersonalityAsync(string personality, CancellationToken cancellationToken);

    /// <summary>
    /// Seed the agent's in-memory session for a channel with historical messages.
    /// Replaces any existing history. Called by the Bridge before the first message.
    /// </summary>
    Task SeedHistoryAsync(string channelId, HubChatMessage[] messages, CancellationToken cancellationToken);

    /// <summary>
    /// Transfer-style seed: drain the target conversation's pending extraction buffer
    /// (stale, unrelated topic) and replace its in-memory history with the seed
    /// payload constructed by the transfer_session tool. Unlike
    /// <see cref="SeedHistoryAsync"/>, this method always replaces — there is no
    /// "skip if state exists" guard, because a transfer's intent is precisely to
    /// overwrite the target with the source's recent context.
    /// <para>
    /// Before replacing, the target's existing history is snapshotted in-memory
    /// so a subsequent <see cref="RevertTransferAsync"/> call can restore it.
    /// Only the most recent snapshot per conversation is retained.
    /// </para>
    /// </summary>
    Task TransferSessionAsync(
        string targetConversationId,
        IReadOnlyList<LlmMessage> seedMessages,
        CancellationToken cancellationToken);

    /// <summary>
    /// Restore a channel's pre-transfer history if a snapshot was captured by
    /// <see cref="TransferSessionAsync"/>. Returns true if a snapshot existed
    /// and was restored, false if there is no recent snapshot for this channel
    /// (either no transfer ever happened, or the snapshot was already consumed
    /// by a prior revert, or the agent restarted since the transfer).
    /// </summary>
    Task<bool> RevertTransferAsync(string channelId, CancellationToken cancellationToken);

    /// <summary>
    /// Reset (clear) the agent's in-memory session for a channel.
    /// Called when history is cleared from the Bridge.
    /// </summary>
    Task ResetSessionAsync(string channelId, CancellationToken cancellationToken);

    /// <summary>
    /// Flush extraction buffer and compact conversation for a channel on demand.
    /// </summary>
    Task<CompactConversationResult> CompactChannelAsync(string channelId, CancellationToken cancellationToken);

    /// <summary>
    /// Run an inline slash command (e.g. <c>/compact</c>, <c>/context</c>) and
    /// return the human-readable response text. Synchronous-result analogue of
    /// the text-prefix path; the Bridge uses this to back Discord application
    /// slash commands that mirror the existing text triggers.
    /// </summary>
    Task<string> RunInlineSlashCommandAsync(string channelId, string commandText, CancellationToken cancellationToken);

    /// <summary>
    /// Reset all agent sessions. Called when all history is cleared.
    /// </summary>
    Task ResetAllSessionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Bridge → Agent barge-in entrypoint. Replace the conversation's trailing
    /// assistant turn with the truncated text the user actually heard (already
    /// ends with "…"). Idempotent per turn.
    /// </summary>
    Task RecordInterruptedAssistantTurnAsync(string conversationId, string playedText);

    /// <summary>
    /// Get the current default model for LLM requests.
    /// Used by memory services (compaction, ingest tool) that need the agent's
    /// configured model rather than a hardcoded value.
    /// </summary>
    string GetDefaultModel();

    /// <summary>
    /// Set the default model for LLM requests. Called when credentials are
    /// pushed from the Bridge, using the first provider's default model
    /// and its context window / max output tokens from the Bridge config.
    /// </summary>
    void SetDefaultModel(string model, int contextWindow = 128_000, int maxOutputTokens = 8_192);

    /// <summary>
    /// Set the model used for memory tasks (context steering, extraction, compaction).
    /// Null = fall back to default model.
    /// </summary>
    void SetMemoryModel(string? model);

    /// <summary>
    /// Set the list of active channel IDs from the Bridge.
    /// Tools use this to validate channel targets.
    /// </summary>
    void SetActiveChannels(string[] channelIds);

    /// <summary>
    /// Get the list of active channel IDs from the Bridge.
    /// Returns an empty array if not yet set.
    /// </summary>
    IReadOnlyList<string> GetActiveChannels();

    /// <summary>Get the active system-prompt configuration (templates + authorable segments).</summary>
    Task<Contracts.SystemPrompt.SystemPromptConfig> GetSystemPromptConfigAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Validate and, if valid, persist a new system-prompt configuration. Returns the
    /// validation result — an invalid config is rejected without being persisted. Emits
    /// an audit log describing the changed fields and fingerprint transition.
    /// </summary>
    Task<Contracts.SystemPrompt.SystemPromptValidationResult> SetSystemPromptConfigAsync(
        Contracts.SystemPrompt.SystemPromptConfig config, CancellationToken cancellationToken);

    /// <summary>Reset the system-prompt configuration to defaults and return it.</summary>
    Task<Contracts.SystemPrompt.SystemPromptConfig> ResetSystemPromptConfigAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Render and return the exact system prompt the model would receive for a given
    /// channel, using the live self-notes/skills/operational state and the stored templates.
    /// </summary>
    Task<string> GetSystemPromptPreviewAsync(string channelId, bool isVoice, CancellationToken cancellationToken);
}
