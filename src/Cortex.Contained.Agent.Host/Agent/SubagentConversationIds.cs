namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Centralizes the established subagent conversation-id convention so all routing decisions parse
/// the same discriminator before deciding whether a message belongs to the main runtime or a live
/// subagent runner.
/// </summary>
internal static class SubagentConversationIds
{
    /// <summary>Prefix used by subagent conversation IDs.</summary>
    internal const string Prefix = "subagent-";

    internal static bool IsSubagentConversation(string conversationId)
        => conversationId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The conversation/channel id a subagent task owns.</summary>
    internal static string ToConversationId(string taskId) => Prefix + taskId;

    internal static bool TryGetTaskId(string conversationId, out string taskId)
    {
        if (conversationId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && conversationId.Length > Prefix.Length)
        {
            taskId = conversationId[Prefix.Length..];
            return true;
        }

        taskId = string.Empty;
        return false;
    }
}
