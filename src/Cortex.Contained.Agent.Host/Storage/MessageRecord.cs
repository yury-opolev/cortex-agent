using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Represents a persisted message in the Agent Host SQLite database.
/// Maps 1:1 with rows in the <c>Messages</c> table.
/// </summary>
public sealed record MessageRecord
{
    /// <summary>Auto-incremented primary key.</summary>
    public long Id { get; init; }

    /// <summary>SHA-256 hashed sender ID (for user messages) or "assistant".</summary>
    public required string UserId { get; init; }

    /// <summary>Channel identifier (e.g. "webchat-default", "discord-dm").</summary>
    public required string ChannelId { get; init; }

    /// <summary>Message role: "user", "assistant", "tool", or "system".</summary>
    public required string Role { get; init; }

    /// <summary>Message text content.</summary>
    public required string Content { get; init; }

    /// <summary>UTC timestamp in ISO 8601 format.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Optional external message ID for deduplication.</summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// Controls visibility: Normal (0) = everywhere, Internal (1) = hidden from UI/seeding,
    /// System (2) = visible in UI but excluded from seeding.
    /// </summary>
    public MessageCategory Category { get; init; }

    /// <summary>
    /// Compact JSON summary of tool calls associated with this assistant message.
    /// NULL on every other role and on rows written before this column existed.
    /// Shape: array of <c>{name, args, ok, pos}</c> entries — see <c>ToolCallSummary</c>.
    /// </summary>
    public string? ToolCalls { get; init; }
}
