using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Represents a single conversation session with the agent.
/// Maintains message history and tracks state.
///
/// This class is a thin facade over four independently-locked sub-components:
/// <see cref="ConversationHistory"/>, <see cref="MemoryExtractionBuffer"/>,
/// <see cref="PendingMessageQueue"/>, and <see cref="GenerationState"/>.
/// </summary>
public sealed class AgentSession : IDisposable
{
    private readonly ConversationHistory history = new();
    private readonly MemoryExtractionBuffer extractionBuffer = new();
    private readonly PendingMessageQueue pending = new();
    private readonly GenerationState generation = new();

    /// <summary>Initialises a session for <paramref name="conversationId"/>.</summary>
    public AgentSession(string conversationId)
    {
        this.ConversationId = conversationId;
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>The unique conversation identifier.</summary>
    public string ConversationId { get; }

    /// <summary>When this session was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>UTC timestamp of the most recent history mutation.</summary>
    public DateTimeOffset LastMessageAt
    {
        get => this.history.LastMessageAt;
        private set => this.history.LastMessageAt = value;
    }

    /// <summary>Optional human-readable title for the conversation.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// <see langword="true"/> while the agent is actively producing a response for this
    /// session.
    /// </summary>
    public bool IsGenerating => this.generation.IsGenerating;

    /// <summary>
    /// Set when the in-flight/just-finished assistant turn was barge-in interrupted;
    /// the played-only text (already ends with "…"). Consulted by the persist site so
    /// durable history matches what was spoken.
    /// </summary>
    public string? InterruptedPlayedText => this.generation.InterruptedPlayedText;

    /// <summary>
    /// Durable record id of the last persisted assistant turn (from
    /// <c>SaveMessageAsync</c>) so an interrupt arriving AFTER persistence can update
    /// the existing row. 0 = none yet for the current turn.
    /// </summary>
    public long LastAssistantRecordId => this.generation.LastAssistantRecordId;

    /// <summary>Mark the current assistant turn as barge-in interrupted.</summary>
    public void MarkInterrupted(string playedText) =>
        this.generation.MarkInterrupted(playedText);

    /// <summary>Record the durable id of the just-persisted assistant turn.</summary>
    public void SetLastAssistantRecordId(long id) =>
        this.generation.SetLastAssistantRecordId(id);

    /// <summary>
    /// Clear the pending-truncation marker once persisted. Keeps
    /// <see cref="LastAssistantRecordId"/> so a SECOND barge-in on the same turn (with
    /// possibly shorter playedText) can still re-update the row; the id is zeroed per
    /// turn by <see cref="BeginGeneration"/>.
    /// </summary>
    public void ClearInterruption() =>
        this.generation.ClearInterruption();

    /// <summary>
    /// Actual prompt token count from the last LLM response.
    /// Updated after each API call to track real context usage.
    /// </summary>
    public int LastPromptTokens { get; set; }

    /// <summary>
    /// The tool-loop round at which the last compaction was performed (-1 = none this
    /// turn). Allows re-compaction after a cooldown of
    /// <c>MinRoundsBetweenCompactions</c> rounds. Reset to -1 when a new user message
    /// arrives.
    /// </summary>
    public int LastCompactionRound { get; set; } = -1;

    // ── Extraction Buffer ────────────────────────────────────────────────

    /// <summary>Appends an entry to the extraction buffer.</summary>
    public void AppendToExtractionBuffer(ExtractionEntry entry) =>
        this.extractionBuffer.Append(entry);

    /// <summary>
    /// Drains the extraction buffer, returning all entries and clearing the list.
    /// </summary>
    public IReadOnlyList<ExtractionEntry> DrainExtractionBuffer() =>
        this.extractionBuffer.Drain();

    /// <summary>
    /// Returns all entries from the extraction buffer without removing them.
    /// Used for session snapshot serialization.
    /// </summary>
    public IReadOnlyList<ExtractionEntry> PeekAllExtractionEntries() =>
        this.extractionBuffer.PeekAll();

    /// <summary>Current extraction buffer count (for flush-decision checks).</summary>
    public int ExtractionBufferCount => this.extractionBuffer.Count;

    /// <summary>
    /// Set by <see cref="AgentSessionStore"/> when the session has been idle longer than
    /// the configured timeout. Instead of wiping history immediately, the flag tells
    /// <see cref="AgentRuntime"/> to run compaction (LLM summarization) before processing
    /// the next message, preserving important context.
    /// </summary>
    public bool NeedsIdleCompaction { get; set; }

    // ── Pending Message Queue ────────────────────────────────────────────

    /// <summary>Number of messages waiting in the pending queue.</summary>
    public int PendingMessageCount => this.pending.Count;

    /// <summary>
    /// Enqueue a message for processing. Thread-safe.
    /// Signals the session loop that work is available.
    /// </summary>
    public void EnqueuePending(AgentMessage message) =>
        this.pending.Enqueue(message);

    /// <summary>
    /// Drain all pending messages, returning them in order. Thread-safe.
    /// </summary>
    public IReadOnlyList<AgentMessage> DrainPendingMessages() =>
        this.pending.DrainAll();

    /// <summary>Wait until at least one pending message is available.</summary>
    public Task WaitForPendingAsync(CancellationToken cancellationToken) =>
        this.pending.WaitAsync(cancellationToken);

    // ── Conversation History ─────────────────────────────────────────────

    /// <summary>Number of user + assistant messages.</summary>
    public int MessageCount => this.history.Count;

    /// <summary>Add a message to the conversation history.</summary>
    public void AddMessage(LlmMessage message) =>
        this.history.Add(message);

    /// <summary>
    /// Appends an assistant message to the session history. If the last message is
    /// already an assistant message (without tool calls), glues the new content onto it
    /// with a separator. This prevents consecutive assistant messages which OpenAI and
    /// GitHub Copilot APIs reject. The Bridge/UI shows separate bubbles; the LLM sees
    /// one merged message.
    /// </summary>
    public void AppendOrGlueAssistantMessage(string content) =>
        this.history.AppendOrGlueAssistant(content);

    /// <summary>
    /// Replace the trailing assistant message's content (barge-in truncation), or
    /// append a new assistant message if the last message is not a plain assistant turn.
    /// Mirrors AppendOrGlueAssistantMessage's tool-call guard.
    /// </summary>
    public void ReplaceOrAppendTrailingAssistant(string content) =>
        this.history.ReplaceOrAppendTrailingAssistant(content);

    /// <summary>Get the full conversation history for building LLM prompts.</summary>
    public IReadOnlyList<LlmMessage> GetHistory() =>
        this.history.Snapshot();

    /// <summary>Get the last N messages (for GetHistory hub method).</summary>
    /// <remarks>Internal messages (e.g. scheduled task instructions) are excluded.</remarks>
    public IReadOnlyList<HubChatMessage> GetChatHistory(int limit) =>
        this.history.GetChatHistory(this.ConversationId, limit);

    /// <summary>Mark the session as generating a response.</summary>
    public CancellationToken BeginGeneration(CancellationToken externalToken) =>
        this.generation.Begin(externalToken);

    /// <summary>Mark the session as done generating.</summary>
    public void EndGeneration() =>
        this.generation.End();

    /// <summary>Cancel the current in-progress generation.</summary>
    public void AbortGeneration() =>
        this.generation.Abort();

    /// <summary>
    /// Trim history to keep only the last <paramref name="maxMessages"/>.
    /// Tool-call groups (an assistant message with ToolCalls followed by its
    /// tool-result messages) are kept or dropped as an atomic unit so that
    /// tool_result blocks always have a matching tool_use.
    /// </summary>
    public void TrimHistory(int maxMessages) =>
        this.history.Trim(maxMessages);

    /// <summary>Clear all history (used for idle reset).</summary>
    public void ClearHistory() =>
        this.history.Clear();

    /// <summary>Get a snapshot of this session's info.</summary>
    public ConversationInfo ToConversationInfo() =>
        new()
        {
            ConversationId = this.ConversationId,
            CreatedAt = this.CreatedAt,
            LastMessageAt = this.history.LastMessageAt,
            MessageCount = this.history.Count,
            Title = this.Title,
        };

    /// <inheritdoc/>
    public void Dispose()
    {
        this.pending.Dispose();
        this.generation.Dispose();
    }
}
