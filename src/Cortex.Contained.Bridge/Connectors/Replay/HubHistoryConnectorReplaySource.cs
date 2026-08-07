using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Connectors.Replay;

/// <summary>
/// Implements <see cref="IConnectorReplaySource"/> by paging backwards through the agent's
/// message history via <see cref="Hub.HubClient.GetMessagesAsync"/>.
/// </summary>
public sealed partial class HubHistoryConnectorReplaySource : IConnectorReplaySource
{
    /// <summary>Maximum pages to fetch in a single replay request to guard against runaway stores.</summary>
    internal const int MaxPages = 20;

    /// <summary>
    /// The role value used by the agent host when storing assistant responses.
    /// Verified by grepping <c>AgentRuntime.cs</c> — the literal is <c>"assistant"</c>.
    /// Comparison is case-insensitive for robustness.
    /// </summary>
    internal const string AssistantRole = "assistant";

    private readonly TenantRouter tenantRouter;
    private readonly BridgeConfig config;
    private readonly ILogger<HubHistoryConnectorReplaySource> logger;

    /// <summary>Initialises a new <see cref="HubHistoryConnectorReplaySource"/>.</summary>
    public HubHistoryConnectorReplaySource(
        TenantRouter tenantRouter,
        BridgeConfig config,
        ILogger<HubHistoryConnectorReplaySource> logger)
    {
        this.tenantRouter = tenantRouter;
        this.config = config;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectorReplayMessage>> GetMissedMessagesAsync(
        string channelId,
        DateTimeOffset since,
        CancellationToken ct)
    {
        try
        {
            return await this.FetchMissedMessagesAsync(channelId, since, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.LogReplayFetchFailed(channelId, ex);
            return [];
        }
    }

    private async Task<IReadOnlyList<ConnectorReplayMessage>> FetchMissedMessagesAsync(
        string channelId,
        DateTimeOffset since,
        CancellationToken ct)
    {
        // Resolve HubClient using the same pattern as TenantRouterConnectorAbortDispatcher.
        var tenantId = this.tenantRouter.ResolveChannel(channelId);
        var client = tenantId is not null
            ? this.tenantRouter.GetClient(tenantId)
            : this.tenantRouter.GetDefaultClient();

        if (client is null || !client.IsConnected)
        {
            this.LogHubUnavailable(channelId);
            return [];
        }

        var replayConfig = this.config.Connectors.Replay;
        var maxMessages = replayConfig.MaxMessages;

        // The MaxAge cap cannot be defeated by a connector sending an ancient sinceCursor, and
        // clamping to now stops a future-dated cursor from suppressing the connector's replay
        // for good.
        var now = DateTimeOffset.UtcNow;
        var floor = Min(Max(since, now - replayConfig.MaxAge), now);

        var pageSize = Math.Min(maxMessages, 100);
        var accumulated = new List<MessageEntryDto>();
        var exhausted = false;

        for (var page = 0; page < MaxPages && !exhausted; page++)
        {
            var offset = page * pageSize;
            MessageListResult result;

            try
            {
                result = await client.GetMessagesAsync(channelId, pageSize, offset, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.LogPageFetchFailed(channelId, page, ex);
                break;
            }

            if (result.Messages.Count == 0)
            {
                break;
            }

            foreach (var entry in result.Messages)
            {
                accumulated.Add(entry);
            }

            // Stop if we have seen a message at or before floor, or collected enough eligible
            // messages, or exhausted the store. "Eligible" counts only assistant messages,
            // because counting every role would stop paging early on a chatty channel and
            // silently drop older outbound messages the connector actually missed.
            var reachedFloor = result.Messages.Any(m => m.Timestamp <= floor);
            var eligibleCount = accumulated.Count(m =>
                string.Equals(m.Role, AssistantRole, StringComparison.OrdinalIgnoreCase));
            var collectedEnough = eligibleCount >= maxMessages;
            var storeExhausted = offset + result.Messages.Count >= result.TotalCount;

            if (reachedFloor || collectedEnough || storeExhausted)
            {
                exhausted = true;
            }
        }

        return SelectReplayMessages(accumulated, floor, maxMessages);
    }

    /// <summary>
    /// Pure filtering and ordering step — extracted for unit testability because
    /// <see cref="Hub.HubClient"/> is sealed and cannot be substituted.
    /// </summary>
    internal static IReadOnlyList<ConnectorReplayMessage> SelectReplayMessages(
        IReadOnlyList<MessageEntryDto> entries,
        DateTimeOffset floor,
        int maxMessages)
    {
        var filtered = entries
            .Where(e => string.Equals(e.Role, AssistantRole, StringComparison.OrdinalIgnoreCase))
            .Where(e => !string.IsNullOrWhiteSpace(e.Content))
            .Where(e => e.Timestamp > floor)
            .OrderBy(e => e.Timestamp)
            .ToList();

        // When there are more than maxMessages, keep the NEWEST ones.
        if (filtered.Count > maxMessages)
        {
            filtered = filtered.Skip(filtered.Count - maxMessages).ToList();
        }

        return filtered.Select(e => new ConnectorReplayMessage
        {
            MessageId = e.MessageId,
            ConversationId = e.ChannelId ?? string.Empty,
            Text = e.Content,
            Timestamp = e.Timestamp,
        }).ToList();
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector replay: no connected hub client for channel {ChannelId}. Replay skipped.")]
    private partial void LogHubUnavailable(string channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector replay: failed to fetch missed messages for channel {ChannelId}.")]
    private partial void LogReplayFetchFailed(string channelId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector replay: failed to fetch page {Page} for channel {ChannelId}.")]
    private partial void LogPageFetchFailed(string channelId, int page, Exception ex);
}
