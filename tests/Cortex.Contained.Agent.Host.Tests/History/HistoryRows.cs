using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>Builds synthetic <see cref="MessageRecord"/> rows for history tests.</summary>
internal static class HistoryRows
{
    public static readonly DateTimeOffset Day = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    public static MessageRecord Row(
        long id,
        DateTimeOffset ts,
        string content,
        string role = "user",
        MessageCategory category = MessageCategory.Normal,
        string channel = "ch",
        string? toolCalls = null) => new()
        {
            Id = id,
            UserId = role == "user" ? "u" : "assistant",
            ChannelId = channel,
            Role = role,
            Content = content,
            Timestamp = ts,
            Category = category,
            ToolCalls = toolCalls,
        };

    public static string ToolCalls(params string[] names) =>
        "[" + string.Join(",", names.Select(n => $"{{\"name\":\"{n}\",\"args\":\"\",\"ok\":true,\"pos\":\"after\"}}")) + "]";
}
