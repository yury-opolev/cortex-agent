using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Allows the LLM to proactively send a message to the user without
/// requiring a preceding user message. The Bridge routes the message
/// to the specified channel. A channel must be explicitly specified;
/// omitting it returns an error with a hint about the current channel.
/// </summary>
internal sealed class SendMessageTool : IAgentTool
{
    private readonly ActiveChannelStore activeChannelStore;
    private readonly IProactiveMessageDispatcher dispatcher;

    public SendMessageTool(
        ActiveChannelStore activeChannelStore,
        IProactiveMessageDispatcher dispatcher)
    {
        this.activeChannelStore = activeChannelStore;
        this.dispatcher = dispatcher;
    }

    public string Name => "send_message";

    public string Description
    {
        get
        {
            var activeChannels = this.activeChannelStore.Get();
            var channelList = ChannelNameResolver.GetValidChannelNames(activeChannels);

            if (activeChannels.Count > 0)
            {
                var desc = "Send a message to the user proactively (without waiting for user input). " +
                           "Use this to send reminders, notifications, scheduled updates, or to initiate a conversation. " +
                           $"Available channels: {channelList}. " +
                           "You must specify the target channel.";

                // Only add the voice-vs-discord-voice disambiguation when both
                // are offered — otherwise it pollutes the description with
                // mentions of channels the agent can't actually use.
                var hasDiscordVoice = activeChannels.Contains("discord-voice");
                var hasLocalVoice = activeChannels.Contains("voice-default");
                if (hasDiscordVoice && hasLocalVoice)
                {
                    desc += " Note: 'discord-voice' plays audio in the Discord voice channel; " +
                            "'voice' plays audio on the local PC speaker.";
                }

                return desc;
            }

            return "Send a message to the user proactively (without waiting for user input). " +
                   "Use this to send reminders, notifications, scheduled updates, or to initiate a conversation. " +
                   "You must specify the target channel (webchat, discord, discord-dm, discord-guild, discord-voice, voice). " +
                   "Note: 'discord-voice' is the Discord voice channel (spoken aloud), 'voice' is the local PC speaker.";
        }
    }

    public string ParametersSchema
    {
        get
        {
            var activeChannels = this.activeChannelStore.Get();
            var channelList = ChannelNameResolver.GetValidChannelNames(activeChannels);

            return $$"""
                {
                  "type": "object",
                  "properties": {
                    "text": {
                      "type": "string",
                      "description": "The message text to send"
                    },
                    "channel": {
                      "type": "string",
                      "description": "Target channel: {{channelList}}"
                    }
                  },
                  "required": ["text", "channel"]
                }
                """;
        }
    }

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("text", out var textElement))
            {
                return AgentToolResult.Fail("Missing required parameter: text");
            }

            var text = textElement.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                return AgentToolResult.Fail("text cannot be empty");
            }

            var activeChannels = this.activeChannelStore.Get();

            // Resolve the channel name to a canonical channel ID.
            string? channelId = null;
            if (root.TryGetProperty("channel", out var channelElement))
            {
                var channelName = channelElement.GetString();
                if (!string.IsNullOrWhiteSpace(channelName))
                {
                    if (!ChannelNameResolver.TryResolve(channelName, out channelId))
                    {
                        return AgentToolResult.Fail($"Unknown channel '{channelName}'. Available channels: {ChannelNameResolver.GetValidChannelNames(activeChannels)}");
                    }

                    // Check if the resolved channel is actually active
                    if (!ChannelNameResolver.IsChannelActive(channelId, activeChannels))
                    {
                        return AgentToolResult.Fail($"Channel '{channelName}' is not currently active. Available channels: {ChannelNameResolver.GetValidChannelNames(activeChannels)}");
                    }
                }
            }

            // Require an explicit channel — do not silently fall back.
            // The LLM should know the current channel from the system prompt and
            // can decide whether to target it or another channel.
            if (channelId is null)
            {
                var currentChannelHint = ChannelNameResolver.ToFriendlyName(context.ChannelId);
                var channelList = ChannelNameResolver.GetValidChannelNames(activeChannels);
                var hint = currentChannelHint is not null
                    ? $" The user is currently on '{currentChannelHint}'."
                    : string.Empty;
                return AgentToolResult.Fail($"Missing required parameter: channel. Available channels: {channelList}.{hint}");
            }

            // Delegate to the shared dispatcher (handles BridgeClient + MessageStore +
            // deferred-injection bookkeeping). Channel-name resolution and active-channel
            // validation stay here because they're send_message-specific UX concerns.
            var dispatchResult = await this.dispatcher.DispatchAsync(channelId, text, context, cancellationToken).ConfigureAwait(false);

            if (dispatchResult.Success)
            {
                return AgentToolResult.Ok("Message sent successfully" +
                              (dispatchResult.ConversationId is not null ? $" (conversation: {dispatchResult.ConversationId})" : string.Empty));
            }

            return AgentToolResult.Fail(dispatchResult.Error ?? "Failed to send message.");
        }
        catch (JsonException ex)
        {
            return AgentToolResult.Fail($"Invalid JSON arguments: {ex.Message}");
        }
    }
}
