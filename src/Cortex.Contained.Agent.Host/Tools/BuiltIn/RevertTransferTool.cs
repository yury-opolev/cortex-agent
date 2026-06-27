using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Undo affordance for <c>transfer_session</c>. Restores the target channel's
/// pre-transfer history if a snapshot is still in memory. Use when the user
/// regrets a transfer or asks for the prior state back.
/// </summary>
internal sealed partial class RevertTransferTool : IAgentTool
{
    private readonly Func<IAgentRuntime> agentRuntimeFactory;
    private readonly ILogger<RevertTransferTool> logger;

    public RevertTransferTool(
        Func<IAgentRuntime> agentRuntimeFactory,
        ILogger<RevertTransferTool> logger)
    {
        this.agentRuntimeFactory = agentRuntimeFactory;
        this.logger = logger;
    }

    public string Name => "revert_transfer";

    public string Description =>
        "Restore a channel's pre-transfer history. Use when the user regrets a transfer " +
        "(\"wait, restore what was here before\") or wants to go back to the prior session. " +
        "Only works if a transfer to this channel happened recently in this agent process — " +
        "the snapshot is in-memory and is lost on agent restart. Accepts an optional " +
        "'channel' parameter; defaults to the current channel. " +
        "After a successful revert, the snapshot is consumed — a second revert immediately " +
        "after will fail with \"no recent transfer snapshot.\"";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "channel": {
              "type": "string",
              "description": "Channel id whose pre-transfer history should be restored. Defaults to the current channel."
            }
          }
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string channelId = context.ChannelId;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("channel", out var channelEl))
            {
                var channelRaw = channelEl.GetString();
                if (!string.IsNullOrWhiteSpace(channelRaw))
                {
                    channelId = ChannelNameResolver.TryResolve(channelRaw, out var resolved)
                        ? resolved
                        : channelRaw;
                }
            }
        }
        catch (JsonException ex)
        {
            return AgentToolResult.Fail($"Invalid JSON arguments: {ex.Message}");
        }

        var reverted = await this.agentRuntimeFactory()
            .RevertTransferAsync(channelId, cancellationToken)
            .ConfigureAwait(false);

        if (!reverted)
        {
            this.LogNoSnapshot(channelId);
            return AgentToolResult.Fail($"No recent transfer snapshot for channel '{channelId}'. Either no transfer happened, the snapshot was already used, or the agent restarted since the transfer.");
        }

        this.LogReverted(channelId);
        return AgentToolResult.Ok($"Restored channel '{channelId}' to its pre-transfer state.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "revert_transfer succeeded: channel={ChannelId}")]
    private partial void LogReverted(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "revert_transfer: no snapshot for channel={ChannelId}")]
    private partial void LogNoSnapshot(string channelId);
}
