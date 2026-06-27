namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Maps between user-facing channel ids and the canonical conversation ids
/// used by the runtime. Most channels have <c>channelId == conversationId</c>;
/// voice is the exception, carrying a tenant suffix (<c>discord-voice-{tenantId}</c>)
/// so per-tenant tools can recover the tenant from <c>ToolExecutionContext.ConversationId</c>.
/// Centralising this rule eliminates scattered, duplicate translation logic.
/// </summary>
public interface IChannelConversationResolver
{
    /// <summary>
    /// Returns the conversation id used by the runtime for a given channel id and
    /// tenant. For voice channels: <c>"discord-voice-{tenantId}"</c>. For others:
    /// <paramref name="channelId"/> unchanged.
    /// </summary>
    string ResolveConversationId(string channelId, string tenantId);

    /// <summary>
    /// Inverse of <see cref="ResolveConversationId"/>. Returns the canonical channel
    /// id and (for voice) the embedded tenant. Returns a null tenant for channels
    /// that don't embed it.
    /// </summary>
    (string ChannelId, string? TenantId) ParseConversationId(string conversationId);
}
