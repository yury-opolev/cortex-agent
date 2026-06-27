using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// No-op <see cref="IMessageStore"/> used when no persistent store is wired (e.g. lightweight
/// unit tests that construct <see cref="Cortex.Contained.Agent.Host.Agent.AgentRuntime"/>
/// without a database). Writes are discarded; reads return empty/zero. This replaces the
/// scattered <c>messageStore is not null</c> guards in the runtime with a single substitution
/// at construction time.
/// </summary>
public sealed class NullMessageStore : IMessageStore
{
    /// <summary>Shared singleton instance — the store is stateless.</summary>
    public static readonly NullMessageStore Instance = new();

    private NullMessageStore()
    {
    }

    /// <inheritdoc />
    public Task<long> SaveMessageAsync(
        string userId,
        string channelId,
        string role,
        string content,
        DateTimeOffset timestamp,
        string? messageId = null,
        MessageCategory category = MessageCategory.Normal,
        string? toolCalls = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    /// <inheritdoc />
    public Task UpdateToolCallsAsync(
        long messageId,
        string? toolCallsJson,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task UpdateContentAsync(
        long recordId,
        string content,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<List<MessageRecord>> GetMessagesAsync(
        string channelId,
        int limit = 100,
        DateTimeOffset? before = null,
        long? beforeId = null,
        MessageVisibility visibility = MessageVisibility.History,
        DateTimeOffset? after = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new List<MessageRecord>());

    /// <inheritdoc />
    public Task<List<ConversationSummary>> GetConversationsAsync(
        string? channelId = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new List<ConversationSummary>());

    /// <inheritdoc />
    public Task<List<MessageRecord>> SearchMessagesAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new List<MessageRecord>());

    /// <inheritdoc />
    public Task<List<ChannelSummary>> GetChannelSummariesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new List<ChannelSummary>());

    /// <inheritdoc />
    public Task<long> GetConversationCountAsync(
        string? channelId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    /// <inheritdoc />
    public Task<long> GetMessageCountAsync(
        string channelId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    /// <inheritdoc />
    public Task<List<MessageRecord>> GetAllMessagesAsync(
        MessageVisibility visibility = MessageVisibility.All,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new List<MessageRecord>());

    /// <inheritdoc />
    public Task<int> BulkInsertAsync(
        IReadOnlyList<MessageRecord> records,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    /// <inheritdoc />
    public Task<int> DeleteChannelMessagesAsync(
        string channelId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    /// <inheritdoc />
    public Task<int> DeleteChannelMessagesOlderThanAsync(
        string channelId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    /// <inheritdoc />
    public Task<int> DeleteMessagesOlderThanAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    /// <inheritdoc />
    public Task<int> DeleteAllMessagesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
