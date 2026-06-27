using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.ScenarioEvals.Model;

namespace Cortex.Contained.ScenarioEvals.Abstractions;

/// <summary>
/// HTTP client for the Bridge API. Handles dual auth:
/// - X-Api-Key for tenant message endpoints
/// - cortex_session cookie for admin endpoints
/// </summary>
public interface IBridgeApiClient
{
    /// <summary>Send a user message via the tenant API channel and wait for the agent response.</summary>
    Task<(string Response, TokenUsageInfo? Tokens)> SendMessageAsync(string text, CancellationToken ct);

    /// <summary>List all memories for the configured tenant.</summary>
    Task<MemoryListResult> ListMemoriesAsync(int limit = 200, CancellationToken ct = default);

    /// <summary>Flush extraction buffer and compact conversation history.</summary>
    Task CompactAsync(string channelId, CancellationToken ct);

    /// <summary>Run memory compaction/dedup sweep.</summary>
    Task CompactMemoriesAsync(CancellationToken ct);

    /// <summary>Reset the agent session for a channel (clears and re-seeds from history).</summary>
    Task ResetSessionAsync(string channelId, CancellationToken ct);

    /// <summary>Clear everything — messages and memories.</summary>
    Task ResetAllAsync(CancellationToken ct);

    /// <summary>Clear all messages only.</summary>
    Task ClearMessagesAsync(CancellationToken ct);

    /// <summary>Clear all memories only.</summary>
    Task ClearMemoriesAsync(CancellationToken ct);

    /// <summary>Get the API channel ID used for this tenant.</summary>
    string ChannelId { get; }
}
