using System.Text.Json;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Lists conversation channels with message counts and last-activity timestamps.
/// Output is a JSON array sorted by <c>lastActivity</c> descending — the same
/// shape and order a UI history page would render.
/// </summary>
internal sealed class HistoryListChannelsTool : IAgentTool
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly MessageStore messageStore;

    public HistoryListChannelsTool(MessageStore messageStore)
    {
        this.messageStore = messageStore;
    }

    public string Name => "history_list_channels";

    public string Description =>
        "List conversation channels available to read with history_read. " +
        "Returns a JSON array of {channelId, messageCount, lastActivity} sorted by most recent activity. " +
        "Use this before history_read to discover valid channelId values.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {},
          "required": []
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var summaries = await this.messageStore.GetChannelSummariesAsync(cancellationToken).ConfigureAwait(false);

        var payload = summaries
            .Select(s => new
            {
                ChannelId = s.ChannelId,
                MessageCount = s.MessageCount,
                LastActivity = s.LastActivity,
            })
            .ToArray();

        var json = JsonSerializer.Serialize(payload, jsonOptions);

        return AgentToolResult.Ok(json);
    }
}
