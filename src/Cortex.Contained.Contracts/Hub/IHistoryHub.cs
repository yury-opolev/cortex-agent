namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Conversation/message history, channel summaries, and export/import methods
/// exposed by the agent. Part of the composed <see cref="IAgentHub"/> surface —
/// these methods share the single SignalR hub connection and route by method name.
/// </summary>
public interface IHistoryHub
{
    /// <summary>List conversations grouped by channel.</summary>
    Task<ConversationListResult> GetConversations(string? channelId, int limit, int offset);

    /// <summary>Get messages for a conversation/channel.</summary>
    Task<MessageListResult> GetMessages(string conversationId, int limit, int offset);

    /// <summary>Search messages across all conversations.</summary>
    Task<MessageListResult> SearchMessages(string query, int limit);

    /// <summary>Delete all messages for a conversation/channel. Returns count deleted.</summary>
    Task<int> DeleteConversation(string conversationId);

    /// <summary>Clear all messages across all channels. Returns count deleted.</summary>
    Task<int> ClearAllMessages();

    /// <summary>Delete messages older than the given timestamp across all channels. Returns count deleted.</summary>
    Task<int> DeleteMessagesOlderThan(DateTimeOffset olderThan);

    /// <summary>Delete messages older than the given timestamp for a specific channel. Returns count deleted.</summary>
    Task<int> DeleteChannelMessagesOlderThan(string channelId, DateTimeOffset olderThan);

    /// <summary>
    /// List distinct channels that have at least one message, along with each channel's
    /// message count and last-activity timestamp. Ordered by most recent activity first.
    /// </summary>
    Task<IReadOnlyList<ChannelSummaryDto>> GetChannelSummaries();

    /// <summary>Export all agent data (memories, messages, tasks) as a single bundle.</summary>
    Task<ExportBundle> ExportAll();

    /// <summary>Export all memories.</summary>
    Task<ExportMemoriesPayload> ExportMemories();

    /// <summary>Export all messages.</summary>
    Task<ExportMessagesPayload> ExportMessages();

    /// <summary>Export all scheduled tasks.</summary>
    Task<ExportTasksPayload> ExportTasks();

    /// <summary>Import a full export bundle, replacing all existing data.</summary>
    Task<ImportResult> ImportAll(ExportBundle bundle);

    /// <summary>Import memories.</summary>
    Task<ImportResult> ImportMemories(ImportMemoriesRequest request);

    /// <summary>Import messages.</summary>
    Task<ImportResult> ImportMessages(ImportMessagesRequest request);

    /// <summary>Import scheduled tasks.</summary>
    Task<ImportResult> ImportTasks(ImportTasksRequest request);
}
