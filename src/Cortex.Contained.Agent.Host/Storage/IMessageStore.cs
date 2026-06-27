using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Persistent per-tenant message store. The production implementation is
/// <see cref="MessageStore"/> (SQLite); <see cref="NullMessageStore"/> is a no-op
/// used when no store is wired (e.g. lightweight tests). Extracting this interface
/// lets <see cref="Cortex.Contained.Agent.Host.Agent.AgentRuntime"/> and
/// <see cref="Cortex.Contained.Agent.Host.Agent.TurnResponseDelivery"/> depend on a
/// non-nullable abstraction and drop their scattered <c>is not null</c> checks.
/// </summary>
public interface IMessageStore
{
    /// <summary>
    /// Saves a message to the store. Returns the auto-generated row Id
    /// (0 when the store does not persist).
    /// </summary>
    Task<long> SaveMessageAsync(
        string userId,
        string channelId,
        string role,
        string content,
        DateTimeOffset timestamp,
        string? messageId = null,
        MessageCategory category = MessageCategory.Normal,
        string? toolCalls = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Patches the tool-calls column of an existing message in place. No-op if the row
    /// does not exist.
    /// </summary>
    Task UpdateToolCallsAsync(
        long messageId,
        string? toolCallsJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a stored message's textual content in place. No-op if the row does not exist.
    /// </summary>
    Task UpdateContentAsync(
        long recordId,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves messages for a channel, ordered oldest-first (chronological).
    /// </summary>
    Task<List<MessageRecord>> GetMessagesAsync(
        string channelId,
        int limit = 100,
        DateTimeOffset? before = null,
        long? beforeId = null,
        MessageVisibility visibility = MessageVisibility.History,
        DateTimeOffset? after = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets conversation summaries grouped by channel, ordered by most recent activity.
    /// </summary>
    Task<List<ConversationSummary>> GetConversationsAsync(
        string? channelId = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches messages across all channels using a simple text match.
    /// </summary>
    Task<List<MessageRecord>> SearchMessagesAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a per-channel summary with total message count and last-activity timestamp.
    /// </summary>
    Task<List<ChannelSummary>> GetChannelSummariesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the total number of distinct conversations (channels with messages).
    /// </summary>
    Task<long> GetConversationCountAsync(
        string? channelId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the total message count for a channel.
    /// </summary>
    Task<long> GetMessageCountAsync(
        string channelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all messages across all channels, ordered chronologically.
    /// </summary>
    Task<List<MessageRecord>> GetAllMessagesAsync(
        MessageVisibility visibility = MessageVisibility.All,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts multiple messages in a single transaction. Returns the number inserted.
    /// </summary>
    Task<int> BulkInsertAsync(
        IReadOnlyList<MessageRecord> records,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all messages for a specific channel.
    /// </summary>
    Task<int> DeleteChannelMessagesAsync(
        string channelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes messages older than the given timestamp for a specific channel.
    /// </summary>
    Task<int> DeleteChannelMessagesOlderThanAsync(
        string channelId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes messages older than the given timestamp across all channels.
    /// </summary>
    Task<int> DeleteMessagesOlderThanAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all messages across all channels.
    /// </summary>
    Task<int> DeleteAllMessagesAsync(CancellationToken cancellationToken = default);
}
