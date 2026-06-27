using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Owns the ordered list of <see cref="LlmMessage"/> objects for a single conversation,
/// plus the <see cref="LastMessageAt"/> timestamp. All mutations are serialised through
/// an internal <see cref="Lock"/>.
/// </summary>
internal sealed class ConversationHistory
{
    private readonly List<LlmMessage> messages = [];
    private readonly Lock syncLock = new();

    /// <summary>
    /// Initialises the history; <see cref="LastMessageAt"/> is set to <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    public ConversationHistory()
    {
        this.LastMessageAt = DateTimeOffset.UtcNow;
    }

    /// <summary>UTC timestamp of the last mutation (add / append / replace / clear).</summary>
    public DateTimeOffset LastMessageAt { get; internal set; }

    /// <summary>Number of messages currently in history.</summary>
    public int Count
    {
        get
        {
            lock (this.syncLock)
            {
                return this.messages.Count;
            }
        }
    }

    /// <summary>Add a message to the conversation history.</summary>
    public void Add(LlmMessage message)
    {
        lock (this.syncLock)
        {
            this.messages.Add(message);
            this.LastMessageAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Appends an assistant message to history. If the last message is already a
    /// plain assistant message (no tool calls), glues the new content onto it with a
    /// blank-line separator instead of adding a second consecutive assistant message.
    /// </summary>
    public void AppendOrGlueAssistant(string content)
    {
        lock (this.syncLock)
        {
            if (this.IsTrailingPlainAssistant())
            {
                var existing = this.messages[^1];
                var merged = existing.Content + "\n\n" + content;
                this.messages[^1] = existing with { Content = merged };
            }
            else
            {
                this.messages.Add(new LlmMessage
                {
                    Role = "assistant",
                    Content = content,
                    MessageType = LlmMessageType.Proactive,
                });
            }

            this.LastMessageAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Replaces the trailing plain assistant message's content (barge-in truncation),
    /// or appends a new assistant message if the last message is not a plain assistant turn.
    /// </summary>
    public void ReplaceOrAppendTrailingAssistant(string content)
    {
        lock (this.syncLock)
        {
            if (this.IsTrailingPlainAssistant())
            {
                this.messages[^1] = this.messages[^1] with { Content = content };
            }
            else
            {
                this.messages.Add(new LlmMessage
                {
                    Role = "assistant",
                    Content = content,
                    MessageType = LlmMessageType.Proactive,
                });
            }

            this.LastMessageAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Whether the last message is a plain assistant turn (no tool calls) — the shape that
    /// barge-in truncation and proactive-glue both append onto. Caller must hold the lock.
    /// </summary>
    private bool IsTrailingPlainAssistant() =>
        this.messages.Count > 0
        && this.messages[^1].Role == "assistant"
        && this.messages[^1].ToolCalls is null;

    /// <summary>Returns a snapshot of the full history as a new list.</summary>
    public IReadOnlyList<LlmMessage> Snapshot()
    {
        lock (this.syncLock)
        {
            return [.. this.messages];
        }
    }

    /// <summary>
    /// Returns the last <paramref name="limit"/> non-internal messages formatted for the
    /// chat history hub API. <paramref name="conversationId"/> is used to build stable
    /// <c>MessageId</c> values.
    /// </summary>
    public IReadOnlyList<HubChatMessage> GetChatHistory(string conversationId, int limit)
    {
        lock (this.syncLock)
        {
            return this.messages
                .Where(m => !m.IsInternal)
                .TakeLast(limit)
                .Select((m, i) => new HubChatMessage
                {
                    MessageId = $"{conversationId}-{i}",
                    Role = m.Role,
                    Text = m.Content ?? string.Empty,
                    Timestamp = m.Timestamp,
                })
                .ToArray();
        }
    }

    /// <summary>
    /// Trims history to keep only the last <paramref name="maxMessages"/> messages.
    /// System messages at the start are always preserved. Tool-call groups (an assistant
    /// message with <c>ToolCalls</c> followed by its <c>tool</c> result messages) are kept
    /// or dropped as an atomic unit so tool_result blocks always have a matching tool_use.
    /// </summary>
    public void Trim(int maxMessages)
    {
        lock (this.syncLock)
        {
            if (this.messages.Count <= maxMessages)
            {
                return;
            }

            var systemCount = 0;
            while (systemCount < this.messages.Count && this.messages[systemCount].Role == "system")
            {
                systemCount++;
            }

            var nonSystemCount = this.messages.Count - systemCount;
            var keepCount = maxMessages - systemCount;
            if (keepCount <= 0 || nonSystemCount <= keepCount)
            {
                return;
            }

            // First absolute index of the non-system messages we will keep.
            var startIndex = systemCount + (nonSystemCount - keepCount);

            // Walk forward past any leading tool-result messages so we don't orphan them.
            while (startIndex < this.messages.Count && this.messages[startIndex].Role == "tool")
            {
                startIndex++;
            }

            // Drop the non-system messages between the preserved system prefix and the kept tail.
            this.messages.RemoveRange(systemCount, startIndex - systemCount);
        }
    }

    /// <summary>Clears all messages and resets <see cref="LastMessageAt"/>.</summary>
    public void Clear()
    {
        lock (this.syncLock)
        {
            this.messages.Clear();
            this.LastMessageAt = DateTimeOffset.UtcNow;
        }
    }
}
