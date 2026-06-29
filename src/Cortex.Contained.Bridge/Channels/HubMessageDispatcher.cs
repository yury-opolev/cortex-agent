using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Channels;

/// <summary>
/// Routes messages bidirectionally between the <see cref="ChannelManager"/>
/// (local messaging channels) and the <see cref="HubClient"/> (SignalR
/// connection to the Agent Hub inside the container).
/// In multi-tenant mode, resolves Discord sender IDs to tenant-specific
/// <see cref="HubClient"/> instances via <see cref="TenantRouter"/>.
///
/// Message persistence is handled exclusively by the Agent Host — the Bridge
/// is a stateless message router and does not maintain its own message store.
/// </summary>
public sealed partial class HubMessageDispatcher
{
    private readonly ChannelManager channelManager;
    private readonly TenantRouter tenantRouter;
    private readonly ILogger<HubMessageDispatcher> logger;

    /// <summary>Maps conversationId → channelId so outbound messages reach the right channel.</summary>
    private readonly ConcurrentDictionary<string, string> conversationChannelMap = new();

    /// <summary>Accumulates streamed text per conversation for channels that do not support streaming.</summary>
    private readonly ConcurrentDictionary<string, string> streamingTextAccumulator = new();

    /// <summary>Maps tenantId → DM channel snowflake, cached when inbound DMs arrive.</summary>
    private readonly ConcurrentDictionary<string, ulong> tenantDmSnowflakes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tracks which <see cref="HubClient"/> instances have been wired to prevent duplicate handlers.</summary>
    private readonly HashSet<HubClient> wiredClients = [];

    /// <summary>Matches setup codes like AB3K-7X4P (two groups of 4 alphanumeric chars).</summary>
    private static readonly Regex SetupCodePattern = new(@"^[A-Z0-9]{4}-[A-Z0-9]{4}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Discord DM channel ID constant (must match DiscordChannel.DmChannelId).</summary>
    private const string DiscordDmChannelId = "discord-dm";

    public HubMessageDispatcher(
        ChannelManager channelManager,
        TenantRouter tenantRouter,
        ILogger<HubMessageDispatcher> logger)
    {
        this.channelManager = channelManager;
        this.tenantRouter = tenantRouter;
        this.logger = logger;
    }

    /// <summary>
    /// Subscribes to channel events for inbound message flow.
    /// Call once after construction, before the hub client is connected.
    /// </summary>
    public void Initialize()
    {
        // Inbound: channel → agent
        this.channelManager.MessageReceived += OnChannelMessageReceivedAsync;
    }

    /// <summary>
    /// Subscribes to hub client events for outbound message flow.
    /// The <paramref name="tenantId"/> is captured in closures so outbound handlers
    /// can resolve the correct DM target for each tenant.
    /// Idempotent: calling with the same client instance is a no-op.
    /// </summary>
    public void WireHubClient(HubClient client, string tenantId)
    {
        lock (this.wiredClients)
        {
            if (!this.wiredClients.Add(client))
            {
                return;
            }
        }

        // Outbound: agent → channel (closures capture tenantId)
        client.OnResponseChunk += chunk => this.OnAgentResponseChunkAsync(chunk, tenantId);
        client.OnResponseComplete += response => this.OnAgentResponseCompleteAsync(response, tenantId);
        client.OnError += agentError => this.OnAgentErrorAsync(agentError, tenantId);

        // Proactive: agent-initiated messages to channels
        client.OnProactiveMessage += message => this.OnProactiveMessageAsync(message, tenantId);
    }

    // ──────────────────────────────────────────────
    //  Inbound flow (Channel → Agent)
    // ──────────────────────────────────────────────

    private async Task OnChannelMessageReceivedAsync(IChannel channel, InboundMessage message)
    {
        try
        {
            // ── Discord DM interception: pairing gate ──────────────────
            // For Discord DMs, only forward to an agent if the sender is
            // already linked to a tenant. Unlinked users are either paired
            // via a setup code or silently ignored.
            if (string.Equals(message.ChannelId, DiscordDmChannelId, StringComparison.OrdinalIgnoreCase))
            {
                var mappedTenant = this.tenantRouter.ResolveDiscordUser(message.Sender.Id);
                if (mappedTenant is null)
                {
                    // Sender not linked — check if the message is a setup code
                    var text = message.Content.Text?.Trim() ?? string.Empty;
                    if (SetupCodePattern.IsMatch(text))
                    {
                        var tenantId = this.tenantRouter.ResolveSetupCode(text);
                        if (tenantId is not null)
                        {
                            // Pair the user to the tenant
                            this.tenantRouter.PairDiscordUser(tenantId, message.Sender.Id, message.Sender.DisplayName);
                            this.LogDiscordUserPaired(message.Sender.Id, tenantId);

                            // Send confirmation via Discord
                            await SendDiscordReplyAsync(channel, message, "Connected! I'm your personal assistant. Send me a message anytime.").ConfigureAwait(false);
                            return;
                        }
                    }

                    // Not a valid code or no matching tenant — silently discard.
                    // Add a delay to slow down brute-force code guessing attempts.
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    this.LogDiscordMessageDiscarded(message.Sender.Id);
                    return;
                }
            }

            // ── Normal message flow ────────────────────────────────────
            var senderIdHash = HashSenderId(message.Sender.Id);
            var correlationId = Guid.NewGuid().ToString("N");

            this.LogInboundMessage(message.ChannelId, message.ConversationId, senderIdHash);

            // Remember the channel for this conversation so outbound messages
            // can be routed back.
            this.conversationChannelMap[message.ConversationId] = message.ChannelId;

            // Cache the DM channel snowflake for the resolved tenant so outbound
            // messages from this tenant can be rewritten to the correct Discord target.
            if (string.Equals(message.ChannelId, DiscordDmChannelId, StringComparison.OrdinalIgnoreCase)
                && message.Properties is not null
                && message.Properties.TryGetValue("dm_snowflake", out var snowflakeStr)
                && ulong.TryParse(snowflakeStr, out var snowflake))
            {
                var resolvedTenantId = this.tenantRouter.ResolveDiscordUser(message.Sender.Id)
                    ?? this.tenantRouter.ResolveChannel(message.ChannelId);
                if (resolvedTenantId is not null)
                {
                    this.tenantDmSnowflakes[resolvedTenantId] = snowflake;
                    this.LogDmSnowflakeCached(resolvedTenantId, snowflake);
                }
            }

            var hubMessage = new HubInboundMessage
            {
                ConversationId = message.ConversationId,
                ChannelId = message.ChannelId,
                SenderIdHash = senderIdHash,
                Text = message.Content.Text ?? string.Empty,
                Attachments = message.Content.Attachments,
                Timestamp = message.Timestamp,
                CorrelationId = correlationId,
                IsVoice = message.Properties?.ContainsKey("voice") == true,
            };

            // Resolve which HubClient to use:
            // For Discord DMs the sender is already confirmed linked (above).
            // For other channels: 1) channel assignment, 2) default tenant.
            var targetClient = ResolveHubClient(message.Sender.Id, message.ChannelId)
                ?? throw new InvalidOperationException("No hub client available for message dispatch");

            var result = await targetClient.SendMessageAsync(hubMessage, CancellationToken.None).ConfigureAwait(false);

            this.LogMessageSentToAgent(message.ConversationId, result.Accepted);

            if (!result.Accepted)
            {
                this.LogMessageSendFailed(message.ConversationId, result.RejectionReason ?? "Unknown rejection reason");
            }
        }
        catch (Exception ex)
        {
            this.LogMessageSendFailed(message.ConversationId, ex.Message);
        }
    }

    /// <summary>Sends a simple text reply back through a channel (used for pairing responses).</summary>
    private static async Task SendDiscordReplyAsync(IChannel channel, InboundMessage original, string text)
    {
        var reply = new OutboundMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ConversationId = original.ConversationId,
            ChannelId = original.ChannelId,
            Content = new MessageContent { Text = text },
        };
        await channel.SendMessageAsync(reply, CancellationToken.None).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────
    //  Outbound flow (Agent → Channel)
    // ──────────────────────────────────────────────

    private async Task OnAgentResponseChunkAsync(ResponseChunkMessage chunk, string tenantId)
    {
        try
        {
            this.LogStreamingChunk(chunk.ConversationId, chunk.SequenceNumber);

            // Look up channel from mapping, falling back to conversationId as channelId
            // (in our architecture conversationId == channelId for all user channels).
            if (!this.conversationChannelMap.TryGetValue(chunk.ConversationId, out var channelId))
            {
                channelId = chunk.ConversationId;
            }

            if (!this.channelManager.TryGetChannel(channelId, out var channel) || channel is null)
            {
                return;
            }

            // Rewrite DM conversationId to tenant-specific snowflake for delivery.
            var deliveryConversationId = this.RewriteDmConversationId(chunk.ConversationId, tenantId);

            if (channel is IChannelWithStreaming streamingChannel)
            {
                await streamingChannel.SendStreamingUpdateAsync(
                    deliveryConversationId,
                    chunk.Text,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                // Accumulate text for non-streaming channels; it will be sent
                // as a single message when OnResponseComplete fires.
                this.streamingTextAccumulator.AddOrUpdate(
                    chunk.ConversationId,
                    chunk.Text,
                    (_, existing) => existing + chunk.Text);
            }
        }
        catch (Exception ex)
        {
            this.LogOutboundSendFailed(chunk.ConversationId, ex.Message);
        }
    }

    private async Task OnAgentResponseCompleteAsync(ResponseCompleteMessage response, string tenantId)
    {
        try
        {
            if (!this.conversationChannelMap.TryGetValue(response.ConversationId, out var channelId))
            {
                channelId = response.ConversationId;
            }

            this.LogOutboundResponse(response.ConversationId, channelId);

            if (!this.channelManager.TryGetChannel(channelId, out var channel) || channel is null)
            {
                this.LogOutboundSendFailed(response.ConversationId, $"Channel '{channelId}' not found");
                return;
            }

            // Rewrite DM conversationId to tenant-specific snowflake for delivery.
            var deliveryConversationId = this.RewriteDmConversationId(response.ConversationId, tenantId);

            var outbound = new OutboundMessage
            {
                MessageId = response.MessageId,
                ConversationId = deliveryConversationId,
                ChannelId = channelId,
                Content = new MessageContent { Text = response.FullText },
                IsThinking = response.IsThinking,
            };

            if (channel is IChannelWithStreaming streamingChannel)
            {
                await streamingChannel.FinalizeStreamingAsync(
                    deliveryConversationId,
                    outbound,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var result = await channel.SendMessageAsync(outbound, CancellationToken.None).ConfigureAwait(false);
                if (!result.Success)
                {
                    this.LogOutboundSendFailed(response.ConversationId, result.ErrorMessage ?? "Channel send returned failure");
                }
            }

            // Clean up accumulated text if any.
            this.streamingTextAccumulator.TryRemove(response.ConversationId, out _);
        }
        catch (Exception ex)
        {
            this.LogOutboundSendFailed(response.ConversationId, ex.Message);
        }
    }

    private async Task OnAgentErrorAsync(AgentErrorMessage agentError, string tenantId)
    {
        try
        {
            this.LogAgentErrorForwarded(agentError.ConversationId, agentError.ErrorCode);

            if (!this.conversationChannelMap.TryGetValue(agentError.ConversationId, out var channelId))
            {
                channelId = agentError.ConversationId;
            }

            if (!this.channelManager.TryGetChannel(channelId, out var channel) || channel is null)
            {
                return;
            }

            // Rewrite DM conversationId to tenant-specific snowflake for delivery.
            var deliveryConversationId = this.RewriteDmConversationId(agentError.ConversationId, tenantId);

            var errorOutbound = new OutboundMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                ConversationId = deliveryConversationId,
                ChannelId = channelId,
                Content = new MessageContent { Text = agentError.Message },
            };

            await channel.SendMessageAsync(errorOutbound, CancellationToken.None).ConfigureAwait(false);

            // Clean up accumulated text on error.
            this.streamingTextAccumulator.TryRemove(agentError.ConversationId, out _);
        }
        catch (Exception ex)
        {
            this.LogOutboundSendFailed(agentError.ConversationId, ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    //  Proactive flow (Agent-initiated → Channel)
    // ──────────────────────────────────────────────

    private async Task<ProactiveMessageResult> OnProactiveMessageAsync(ProactiveMessage message, string tenantId)
    {
        try
        {
            var targetChannelId = message.ChannelId;

            // A target channel must be explicitly specified — no silent fallback.
            if (targetChannelId is null)
            {
                var available = string.Join(", ", this.channelManager.GetAllChannels().Select(c => c.ChannelId));
                var error = $"No target channel specified for proactive message. Available channels: {available}";
                this.LogProactiveMessageFailed(error);
                return new ProactiveMessageResult { Success = false, Error = error };
            }

            if (!this.channelManager.TryGetChannel(targetChannelId, out var channel) || channel is null)
            {
                var available = string.Join(", ", this.channelManager.GetAllChannels().Select(c => c.ChannelId));
                var error = $"Channel '{targetChannelId}' not found. Available channels: {available}";
                this.LogProactiveMessageFailed(error);
                return new ProactiveMessageResult { Success = false, Error = error };
            }

            this.LogProactiveMessage(channel.ChannelId, message.ConversationId);

            // Use the conversation ID from the message if provided, otherwise generate one.
            // Rewrite DM conversationId to tenant-specific snowflake for delivery.
            var conversationId = message.ConversationId ?? Guid.NewGuid().ToString("N");
            var deliveryConversationId = this.RewriteDmConversationId(conversationId, tenantId);

            // Discord voice delivery requires a conversation ID of the form
            // "discord-voice-{tenantId}" — DiscordChannel.SendMessageAsync only
            // routes to the voice handler (→ TTS) when that prefix is present.
            // A fresh GUID would fall through to the DM path instead and the
            // message would be delivered as text. Check the requested channel
            // ID (what the agent asked for) rather than channel.ChannelId,
            // because "discord-voice" is an alias that resolves to the
            // DiscordChannel instance whose primary ChannelId is "discord-dm".
            if (string.Equals(targetChannelId, "discord-voice", StringComparison.Ordinal))
            {
                deliveryConversationId = $"discord-voice-{tenantId}";
            }

            var messageId = Guid.NewGuid().ToString("N");

            var outbound = new OutboundMessage
            {
                MessageId = messageId,
                ConversationId = deliveryConversationId,
                ChannelId = channel.ChannelId,
                Content = new MessageContent
                {
                    Text = message.Text,
                    Attachments = message.Attachments,
                },
            };

            var result = await channel.SendMessageAsync(outbound, CancellationToken.None).ConfigureAwait(false);

            if (result.Success)
            {
                // Update the conversation-channel map so future responses route correctly
                this.conversationChannelMap[conversationId] = channel.ChannelId;

                this.LogProactiveMessageDelivered(deliveryConversationId, channel.ChannelId);
                return new ProactiveMessageResult { Success = true, ConversationId = deliveryConversationId };
            }

            var sendError = result.ErrorMessage ?? "Channel send failed";
            this.LogProactiveMessageDeliveryFailed(channel.ChannelId, sendError, message.Text);
            return new ProactiveMessageResult
            {
                Success = false,
                Error = $"Channel '{channel.ChannelId}' delivery failed (external error, retrying will not help): {sendError}",
            };
        }
        catch (Exception ex)
        {
            this.LogProactiveMessageDeliveryException(message.ChannelId ?? "(none)", ex.Message, message.Text, ex);
            return new ProactiveMessageResult
            {
                Success = false,
                Error = $"Channel '{message.ChannelId}' delivery failed (internal error, retrying will not help): {ex.Message}",
            };
        }
    }

    // ──────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Resolves the appropriate tenant's <see cref="HubClient"/> for a message.
    /// Resolution order:
    /// 1. Discord user → tenant mapping (explicit user link)
    /// 2. Channel → tenant mapping (explicit channel assignment)
    /// 3. Default tenant (fallback)
    /// Note: for Discord DMs, unpaired users are intercepted earlier in
    /// <see cref="OnChannelMessageReceivedAsync"/> and never reach this method.
    /// Returns null if no tenant can be resolved.
    /// </summary>
    private HubClient? ResolveHubClient(string senderId, string? channelId = null)
    {
        // First: check explicit Discord user link
        var tenantId = this.tenantRouter.ResolveDiscordUser(senderId);
        if (tenantId is not null)
        {
            return this.tenantRouter.GetClient(tenantId);
        }

        // Second: check explicit channel assignment
        if (channelId is not null)
        {
            tenantId = this.tenantRouter.ResolveChannel(channelId);
            if (tenantId is not null)
            {
                return this.tenantRouter.GetClient(tenantId);
            }
        }

        // Fallback: default tenant
        return this.tenantRouter.GetDefaultClient();
    }

    /// <summary>
    /// If the conversationId is <c>"discord-dm"</c> and a DM channel snowflake is cached
    /// for the tenant, returns the snowflake as a string so the Discord channel's fallback
    /// routing parses it directly. Otherwise returns the original conversationId unchanged.
    /// </summary>
    private string RewriteDmConversationId(string conversationId, string tenantId)
    {
        if (string.Equals(conversationId, DiscordDmChannelId, StringComparison.OrdinalIgnoreCase)
            && this.tenantDmSnowflakes.TryGetValue(tenantId, out var snowflake))
        {
            this.LogDmConversationIdRewritten(tenantId, snowflake);
            return snowflake.ToString(CultureInfo.InvariantCulture);
        }

        return conversationId;
    }

    private static string HashSenderId(string senderId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(senderId));
        return Convert.ToHexStringLower(bytes);
    }

    // ──────────────────────────────────────────────
    //  LoggerMessage source-generated methods
    // ──────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "Inbound message from channel {ChannelId}, conversation {ConversationId}, senderHash={SenderIdHash}")]
    private partial void LogInboundMessage(string channelId, string conversationId, string senderIdHash);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message sent to agent for conversation {ConversationId}, accepted={Accepted}")]
    private partial void LogMessageSentToAgent(string conversationId, bool accepted);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send message for conversation {ConversationId}: {ErrorMessage}")]
    private partial void LogMessageSendFailed(string conversationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbound response for conversation {ConversationId} to channel {ChannelId}")]
    private partial void LogOutboundResponse(string conversationId, string channelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send outbound message for conversation {ConversationId}: {ErrorMessage}")]
    private partial void LogOutboundSendFailed(string conversationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Streaming chunk for conversation {ConversationId}, sequence={SequenceNumber}")]
    private partial void LogStreamingChunk(string conversationId, int sequenceNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent error forwarded for conversation {ConversationId}, errorCode={ErrorCode}")]
    private partial void LogAgentErrorForwarded(string conversationId, string errorCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Proactive message targeting channel {ChannelId}, conversationId={ConversationId}")]
    private partial void LogProactiveMessage(string channelId, string? conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Proactive message delivered to conversation {ConversationId} on channel {ChannelId}")]
    private partial void LogProactiveMessageDelivered(string conversationId, string channelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Proactive message failed: {ErrorMessage}")]
    private partial void LogProactiveMessageFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Error, Message = "Proactive message delivery failed on channel {ChannelId}: {ErrorMessage}. MessageText: {MessageText}")]
    private partial void LogProactiveMessageDeliveryFailed(string channelId, string errorMessage, string messageText);

    [LoggerMessage(Level = LogLevel.Error, Message = "Proactive message delivery exception on channel {ChannelId}: {ErrorMessage}. MessageText: {MessageText}")]
    private partial void LogProactiveMessageDeliveryException(string channelId, string errorMessage, string messageText, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord user {DiscordUserId} paired to tenant '{TenantId}'")]
    private partial void LogDiscordUserPaired(string discordUserId, string tenantId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discord message from unmapped user {DiscordUserId} discarded (no matching setup code)")]
    private partial void LogDiscordMessageDiscarded(string discordUserId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cached DM snowflake {Snowflake} for tenant '{TenantId}'")]
    private partial void LogDmSnowflakeCached(string tenantId, ulong snowflake);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rewrote DM conversationId to snowflake {Snowflake} for tenant '{TenantId}'")]
    private partial void LogDmConversationIdRewritten(string tenantId, ulong snowflake);
}
