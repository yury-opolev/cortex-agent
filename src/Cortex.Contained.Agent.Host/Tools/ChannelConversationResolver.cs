namespace Cortex.Contained.Agent.Host.Tools;

using Cortex.Contained.Agent.Host.SpeakerId;

public sealed class ChannelConversationResolver : IChannelConversationResolver
{
    public string ResolveConversationId(string channelId, string tenantId)
    {
        return channelId switch
        {
            "discord-voice" => VoiceEnrollmentToolHelpers.VoiceConversationPrefix + tenantId,
            _ => channelId,
        };
    }

    public (string ChannelId, string? TenantId) ParseConversationId(string conversationId)
    {
        if (conversationId.StartsWith(VoiceEnrollmentToolHelpers.VoiceConversationPrefix, StringComparison.Ordinal))
        {
            var tenantId = conversationId[VoiceEnrollmentToolHelpers.VoiceConversationPrefix.Length..];
            return ("discord-voice", tenantId);
        }

        return (conversationId, null);
    }
}
