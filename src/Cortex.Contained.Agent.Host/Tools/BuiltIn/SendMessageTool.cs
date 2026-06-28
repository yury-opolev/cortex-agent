using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Allows the LLM to proactively send a message to the user without
/// requiring a preceding user message. The Bridge routes the message
/// to the specified channel. A channel must be explicitly specified;
/// omitting it returns an error with a hint about the current channel.
/// </summary>
internal sealed class SendMessageTool : IAgentTool
{
    /// <summary>Maximum image attachments per message — keeps the SignalR payload bounded.</summary>
    private const int MaxAttachments = 4;

    private readonly ActiveChannelStore activeChannelStore;
    private readonly IProactiveMessageDispatcher dispatcher;
    private readonly AttachmentLoader attachmentLoader;

    public SendMessageTool(
        ActiveChannelStore activeChannelStore,
        IProactiveMessageDispatcher dispatcher,
        AttachmentLoader attachmentLoader)
    {
        this.activeChannelStore = activeChannelStore;
        this.dispatcher = dispatcher;
        this.attachmentLoader = attachmentLoader;
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
                           "Can also send images: pass 'attachments' (image file paths within your data sandbox). " +
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
                      "description": "The message text to send. Optional when 'attachments' is provided (caption)."
                    },
                    "channel": {
                      "type": "string",
                      "description": "Target channel: {{channelList}}"
                    },
                    "attachments": {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Optional image file paths within your data sandbox to send (e.g. \"chart.png\"). Up to 4 images; png/jpg/jpeg/gif/webp, max 8 MB each."
                    }
                  },
                  "required": ["channel"]
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

            var hasTextProperty = root.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String;
            var text = hasTextProperty ? (textElement.GetString() ?? string.Empty) : string.Empty;

            // Parse optional attachment paths (sandbox-relative).
            var attachmentPaths = new List<string>();
            if (root.TryGetProperty("attachments", out var attachmentsElement)
                && attachmentsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in attachmentsElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        var path = element.GetString();
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            attachmentPaths.Add(path);
                        }
                    }
                }
            }

            var hasAttachments = attachmentPaths.Count > 0;

            // Require text OR at least one attachment. Text alone keeps its original
            // (test-pinned) error messages; image-only sends skip the text requirement.
            if (!hasAttachments)
            {
                if (!hasTextProperty)
                {
                    return AgentToolResult.Fail("Missing required parameter: text");
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return AgentToolResult.Fail("text cannot be empty");
                }
            }

            if (attachmentPaths.Count > MaxAttachments)
            {
                return AgentToolResult.Fail(
                    $"Too many attachments ({attachmentPaths.Count}); at most {MaxAttachments} allowed per message.");
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

            // Load + validate any attachments from the sandbox. Any failure aborts the
            // whole send (no partial delivery) with a clear, LLM-relayable reason.
            List<MediaAttachment>? attachments = null;
            if (hasAttachments)
            {
                attachments = new List<MediaAttachment>(attachmentPaths.Count);
                foreach (var path in attachmentPaths)
                {
                    var loaded = this.attachmentLoader.Load(path);
                    if (!loaded.Success)
                    {
                        return AgentToolResult.Fail(loaded.Error ?? $"Failed to load attachment '{path}'.");
                    }

                    attachments.Add(loaded.Attachment!);
                }
            }

            // Delegate to the shared dispatcher (handles BridgeClient + MessageStore +
            // deferred-injection bookkeeping). Channel-name resolution and active-channel
            // validation stay here because they're send_message-specific UX concerns.
            var dispatchResult = await this.dispatcher.DispatchAsync(channelId, text, context, cancellationToken, attachments).ConfigureAwait(false);

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
