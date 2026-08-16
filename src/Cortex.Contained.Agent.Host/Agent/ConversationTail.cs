using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Selects the bounded slice of a conversation handed to a focused composer run.
/// <para>
/// The slice is measured in TURNS — user and agent messages — because that is what "the last few
/// exchanges" means to a person setting the limit. Tool calls and their results belong to the turn
/// that issued them and ride along without consuming budget; counting them would let one
/// tool-heavy exchange crowd out every actual exchange.
/// </para>
/// </summary>
internal static class ConversationTail
{
    /// <summary>
    /// The last <paramref name="turns"/> user/agent messages, with any tool traffic belonging to
    /// them. System messages are excluded — a composer run builds its own system prompt.
    /// </summary>
    public static IReadOnlyList<LlmMessage> SelectLast(IReadOnlyList<LlmMessage> history, int turns)
    {
        if (turns <= 0 || history.Count == 0)
        {
            return [];
        }

        // Walk back until the budget is spent; the index lands on a user/agent message, so the
        // slice can never open on a tool result orphaned from the call that produced it.
        var counted = 0;
        var startIndex = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var role = history[i].Role;
            if (role == "system")
            {
                continue;
            }

            if (role is "user" or "assistant")
            {
                counted++;
                if (counted == turns)
                {
                    startIndex = i;
                    break;
                }
            }
        }

        var tail = new List<LlmMessage>(history.Count - startIndex);
        for (var i = startIndex; i < history.Count; i++)
        {
            if (history[i].Role != "system")
            {
                tail.Add(history[i]);
            }
        }

        // A history holding fewer than `turns` user/agent messages leaves startIndex at 0, which
        // can open on a tool result whose call is not in the slice, so enforce the invariant here
        // rather than trusting the walk above.
        var lead = 0;
        while (lead < tail.Count && tail[lead].Role == "tool")
        {
            lead++;
        }

        if (lead > 0)
        {
            tail.RemoveRange(0, lead);
        }

        // Same invariant at the other end: a turn that died mid-round leaves an assistant message
        // whose tool_use blocks have no results, and appending the intent after it makes the
        // provider reject the whole request.
        while (tail.Count > 0 && tail[^1] is { Role: "assistant", ToolCalls.Count: > 0 })
        {
            tail.RemoveAt(tail.Count - 1);
        }

        return tail;
    }
}
