using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Estimates token counts for messages to manage context window limits.
/// Uses a simple heuristic: ~4 characters per token for English text.
/// This is intentionally conservative to avoid exceeding model limits.
/// </summary>
internal static class TokenEstimator
{
    /// <summary>
    /// Approximate characters per token.
    /// OpenAI's tokenizer averages ~4 chars/token for English.
    /// We use 3.5 to be conservative (overestimate token count).
    /// </summary>
    private const double CharsPerToken = 3.5;

    /// <summary>
    /// Fixed overhead per message (role, formatting, separators).
    /// OpenAI charges ~4 tokens per message for formatting.
    /// </summary>
    private const int MessageOverhead = 4;

    /// <summary>
    /// Anthropic image token cost estimate.
    /// Anthropic charges ~1600 tokens per 1MB of base64 data (after decoding).
    /// Base64 encoding inflates size by ~33%, so 1MB base64 ≈ 750KB raw.
    /// We estimate conservatively: base64Length / 4 * 3 / 750 * 1600 ≈ base64Length / 625.
    /// Minimum 85 tokens per image (Anthropic's minimum for tiny images).
    /// </summary>
    private const double Base64CharsPerImageToken = 625.0;

    /// <summary>Minimum token cost for any image (Anthropic charges at least 85 tokens).</summary>
    private const int MinImageTokens = 85;

    /// <summary>Estimate the token count for a single message.</summary>
    public static int EstimateTokens(LlmMessage message)
    {
        var charCount = (message.Content?.Length ?? 0)
                        + (message.Role?.Length ?? 0);

        // Include tool call arguments in the estimate
        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var call in message.ToolCalls)
            {
                charCount += (call.Name?.Length ?? 0) + (call.Arguments?.Length ?? 0);
            }
        }

        var tokens = (int)(charCount / CharsPerToken) + MessageOverhead;

        // Include ContentBlocks: text blocks add to char count, images use
        // Anthropic's image token estimation.
        if (message.ContentBlocks is { Count: > 0 })
        {
            foreach (var block in message.ContentBlocks)
            {
                if (block.Type == "text")
                {
                    tokens += (int)((block.Text?.Length ?? 0) / CharsPerToken);
                }
                else if (block.Type == "image" && block.ImageData is not null)
                {
                    var imageTokens = Math.Max(MinImageTokens, (int)(block.ImageData.Length / Base64CharsPerImageToken));
                    tokens += imageTokens;
                }
            }
        }

        return tokens;
    }

    /// <summary>Estimate the total token count for a list of messages.</summary>
    public static int EstimateTokens(IReadOnlyList<LlmMessage> messages)
    {
        var total = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            total += EstimateTokens(messages[i]);
        }

        // Add a small base overhead for the conversation format
        return total + 3; // Every conversation has a base overhead of ~3 tokens
    }

    /// <summary>
    /// Trim messages to fit within a token budget.
    /// Always keeps the system prompt (first message if role == "system").
    /// Removes oldest non-system messages first.
    /// <para>
    /// Callers should run <see cref="ContextManager.PruneToolResults"/> and
    /// <see cref="ContextManager.StripMedia"/> <em>before</em> calling this method
    /// so that oversized content has already been cleared.
    /// </para>
    /// Returns the trimmed list.
    /// </summary>
    public static List<LlmMessage> TrimToFit(
        IReadOnlyList<LlmMessage> messages,
        int maxTokens,
        int reserveForResponse)
    {
        var budget = maxTokens - reserveForResponse;
        if (budget <= 0)
        {
            return [];
        }

        // Separate system messages from the rest
        var systemMessages = new List<LlmMessage>();
        var conversationMessages = new List<LlmMessage>();

        foreach (var msg in messages)
        {
            if (msg.Role == "system")
            {
                systemMessages.Add(msg);
            }
            else
            {
                conversationMessages.Add(msg);
            }
        }

        // Calculate system prompt cost
        var systemTokens = EstimateTokens(systemMessages);
        var remainingBudget = budget - systemTokens;

        if (remainingBudget <= 0)
        {
            // System prompt alone exceeds budget — return just the system messages
            return [.. systemMessages];
        }

        // Group messages into atomic units: a tool-call group is an assistant
        // message with ToolCalls followed by its tool-result messages. These
        // must be kept or dropped together so tool_result blocks always have a
        // matching tool_use.
        var groups = GroupToolCalls(conversationMessages);

        // Work backwards from most recent group, adding until budget is exhausted.
        // Skip groups that don't fit rather than breaking — older smaller groups
        // may still fit and provide useful context.
        // Collect fitting groups newest-first, then reverse once — avoids the O(N²)
        // InsertRange(0, ...) that shifts the whole list on every accepted group.
        var selectedGroups = new List<IReadOnlyList<LlmMessage>>();
        var usedTokens = 0;

        for (var i = groups.Count - 1; i >= 0; i--)
        {
            var groupTokens = 0;
            foreach (var msg in groups[i])
            {
                groupTokens += EstimateTokens(msg);
            }

            if (usedTokens + groupTokens > remainingBudget)
            {
                continue;
            }

            selectedGroups.Add(groups[i]);
            usedTokens += groupTokens;
        }

        selectedGroups.Reverse();

        // Combine: system messages first, then the selected conversation messages (chronological).
        var result = new List<LlmMessage>(systemMessages);
        foreach (var group in selectedGroups)
        {
            result.AddRange(group);
        }

        return result;
    }

    /// <summary>
    /// Groups conversation messages into atomic units. An assistant message
    /// with <see cref="LlmMessage.ToolCalls"/> and the subsequent tool-result
    /// messages form a single group. All other messages are their own group.
    /// </summary>
    internal static List<List<LlmMessage>> GroupToolCalls(List<LlmMessage> messages)
    {
        var groups = new List<List<LlmMessage>>();
        var i = 0;

        while (i < messages.Count)
        {
            var msg = messages[i];

            if (msg.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
            {
                // Start a tool-call group: assistant + subsequent tool results
                var group = new List<LlmMessage> { msg };
                i++;

                while (i < messages.Count && messages[i].Role == "tool")
                {
                    group.Add(messages[i]);
                    i++;
                }

                groups.Add(group);
            }
            else
            {
                groups.Add([msg]);
                i++;
            }
        }

        return groups;
    }
}
