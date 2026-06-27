using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Manages the context window for LLM calls. Responsible for:
/// <list type="bullet">
///   <item>Pruning old tool results (replacing content with a placeholder, preserving tool_use/tool_result pairing)</item>
///   <item>Stripping media (replacing images with text placeholders)</item>
///   <item>Trimming messages to fit within the context window budget</item>
/// </list>
/// Inspired by OpenCode's compaction model: tool_use blocks are always preserved,
/// only tool_result content is cleared. The most recent turn is always protected.
/// </summary>
internal static class ContextManager
{
    /// <summary>
    /// Maximum content length (characters) for a single tool result message.
    /// Results exceeding this are truncated during pruning.
    /// ~100K chars ≈ ~28K tokens at 3.5 chars/token.
    /// </summary>
    internal const int MaxToolResultChars = 100_000;

    /// <summary>
    /// Placeholder text for pruned tool results. The tool_use/tool_result pairing
    /// is preserved so the LLM sees the tool was called, but the bulky output is gone.
    /// </summary>
    internal const string PrunedToolResultPlaceholder = "[Old tool result content cleared]";

    /// <summary>
    /// Token threshold for protecting recent tool results from pruning.
    /// Walking backwards from the most recent message, the first N estimated tokens
    /// of tool output are protected. Only tool results beyond this are pruned.
    /// Mirrors OpenCode's PRUNE_PROTECT = 40,000.
    /// </summary>
    private const int PruneProtectTokens = 40_000;

    /// <summary>
    /// Minimum estimated tokens of prunable tool output required before actually
    /// performing the prune. Avoids churning history for small savings.
    /// Mirrors OpenCode's PRUNE_MINIMUM = 20,000.
    /// </summary>
    private const int PruneMinimumTokens = 20_000;

    /// <summary>
    /// Prepares messages for an LLM call by stripping images from older messages,
    /// pruning old tool results, and trimming to fit the context window.
    /// Returns a new list — the outer list is fresh, but individual messages may share
    /// references with the input. Image content blocks may be mutated in place to
    /// cache <see cref="LlmContentBlock.ImageDescription"/> the first time they are stripped.
    /// </summary>
    public static async Task<List<LlmMessage>> PrepareMessagesAsync(
        IReadOnlyList<LlmMessage> messages,
        int contextWindow,
        int reserveForResponse,
        ImageAgingConfig imageAging,
        IImageDescriber? describer,
        CancellationToken ct)
    {
        var aged = await AgeImagesAsync(messages, imageAging, describer, ct).ConfigureAwait(false);
        var pruned = PruneToolResults(aged);
        return TokenEstimator.TrimToFit(pruned, contextWindow, reserveForResponse);
    }

    /// <summary>
    /// Strips image content blocks from messages older than the Nth-from-end
    /// user-role message, where N = <c>imageAging.PreserveRecentTurns</c>.
    /// Assistant and tool messages do not consume the budget — tool-heavy turns
    /// with many intermediate messages still count as one turn.
    /// Setting <c>PreserveRecentTurns = 0</c> disables aging entirely (never strip).
    /// If the conversation has fewer than N user turns, nothing is stripped.
    /// </summary>
    internal static async Task<List<LlmMessage>> AgeImagesAsync(
        IReadOnlyList<LlmMessage> messages,
        ImageAgingConfig imageAging,
        IImageDescriber? describer,
        CancellationToken ct)
    {
        var preserve = imageAging.PreserveRecentTurns;

        if (preserve <= 0)
        {
            return [.. messages];
        }

        // Walk backwards counting user-role messages. The cutoff is the index of
        // the Nth user message from the end; messages at indices < cutoff get
        // their images stripped. If fewer than N user turns exist, cutoff stays 0.
        var cutoff = 0;
        var userTurnsSeen = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != "user")
            {
                continue;
            }

            userTurnsSeen++;
            if (userTurnsSeen == preserve)
            {
                cutoff = i;
                break;
            }
        }

        if (cutoff == 0)
        {
            return [.. messages];
        }

        var result = new List<LlmMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (i < cutoff && messages[i].ContentBlocks is { Count: > 0 })
            {
                result.Add(await StripMediaFromMessageAsync(messages[i], imageAging, describer, ct).ConfigureAwait(false));
            }
            else
            {
                result.Add(messages[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Prunes old tool result content, working backwards from the most recent message.
    /// Protects the most recent turn (everything after the last user message) and
    /// the first <see cref="PruneProtectTokens"/> of tool output. Beyond that threshold,
    /// tool result content is replaced with <see cref="PrunedToolResultPlaceholder"/>.
    /// <para>
    /// Tool_use (assistant with ToolCalls) messages are always preserved intact.
    /// Only tool_result Content is cleared, maintaining the pairing required by
    /// Anthropic and OpenAI APIs.
    /// </para>
    /// </summary>
    internal static List<LlmMessage> PruneToolResults(IReadOnlyList<LlmMessage> messages)
    {
        // Find tool result indices that are candidates for pruning.
        // Walk backwards, protect the most recent turn, then protect PruneProtectTokens.
        var toolResultTokens = 0;
        var prunableTokens = 0;
        var toPruneIndices = new HashSet<int>();
        var turns = 0;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];

            // Count user messages as turn boundaries
            if (msg.Role == "user")
            {
                turns++;
            }

            // Protect the most recent turn (everything before the 2nd user message)
            if (turns < 2)
            {
                continue;
            }

            // Only consider tool result messages
            if (msg.Role != "tool")
            {
                continue;
            }

            var estimate = TokenEstimator.EstimateTokens(msg);
            toolResultTokens += estimate;

            // Protect the first PruneProtectTokens of tool output
            if (toolResultTokens <= PruneProtectTokens)
            {
                continue;
            }

            prunableTokens += estimate;
            toPruneIndices.Add(i);
        }

        // Only prune if there's enough to be worth it
        if (prunableTokens < PruneMinimumTokens || toPruneIndices.Count == 0)
        {
            return [.. messages];
        }

        // Build the pruned message list
        var result = new List<LlmMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (toPruneIndices.Contains(i))
            {
                result.Add(messages[i] with { Content = PrunedToolResultPlaceholder });
            }
            else
            {
                result.Add(messages[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Strips media (images) from all messages and truncates oversized tool results.
    /// Used during emergency compaction when the context has overflowed.
    /// <para>
    /// Unlike <see cref="AgeImagesAsync"/>, this strips every message's images —
    /// we've already blown the context window, so there's no budget to keep any
    /// image bytes. Images inside the <see cref="ImageAgingConfig.PreserveRecentTurns"/>
    /// window get a <em>verbose</em> description (so the user can still ask about
    /// a photo they just attached); older images get the cheap brief description.
    /// </para>
    /// </summary>
    internal static async Task<List<LlmMessage>> StripMediaAsync(
        IReadOnlyList<LlmMessage> messages,
        ImageAgingConfig imageAging,
        IImageDescriber? describer,
        CancellationToken ct)
    {
        var verboseFromIndex = ComputeRecentCutoff(messages, imageAging.PreserveRecentTurns);

        var result = new List<LlmMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var useVerbose = i >= verboseFromIndex;
            result.Add(await StripMediaFromMessageAsync(messages[i], imageAging, describer, useVerbose, ct).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>
    /// Finds the index of the first message that belongs to a "recent" user
    /// turn — i.e. the Nth-from-last user-role message where N = <paramref name="preserve"/>.
    /// Returns <c>messages.Count</c> (i.e. no recent window) when
    /// <paramref name="preserve"/> is 0 or the conversation has fewer than N user turns.
    /// Mirrors the cutoff logic in <see cref="AgeImagesAsync"/>.
    /// </summary>
    private static int ComputeRecentCutoff(IReadOnlyList<LlmMessage> messages, int preserve)
    {
        if (preserve <= 0)
        {
            return messages.Count;
        }

        var userTurnsSeen = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != "user")
            {
                continue;
            }

            userTurnsSeen++;
            if (userTurnsSeen == preserve)
            {
                return i;
            }
        }

        // Fewer than `preserve` user turns in the conversation — every message
        // is within the recent window.
        return 0;
    }

    /// <summary>
    /// Returns a copy of the message with images replaced by text placeholders
    /// and oversized tool results truncated. If <paramref name="imageAging"/>.DescribeOnStrip
    /// is true and a describer is provided, the image is described via the describer
    /// and the description is cached on the original block's <see cref="LlmContentBlock.ImageDescription"/>.
    /// </summary>
    internal static Task<LlmMessage> StripMediaFromMessageAsync(
        LlmMessage message,
        ImageAgingConfig imageAging,
        IImageDescriber? describer,
        CancellationToken ct)
        => StripMediaFromMessageAsync(message, imageAging, describer, verbose: false, ct);

    internal static async Task<LlmMessage> StripMediaFromMessageAsync(
        LlmMessage message,
        ImageAgingConfig imageAging,
        IImageDescriber? describer,
        bool verbose,
        CancellationToken ct)
    {
        var changed = false;

        IReadOnlyList<LlmContentBlock>? newBlocks = null;
        if (message.ContentBlocks is { Count: > 0 })
        {
            var blocks = new List<LlmContentBlock>(message.ContentBlocks.Count);
            foreach (var block in message.ContentBlocks)
            {
                if (block.Type == "image" && block.ImageData is not null)
                {
                    var replacementText = await ResolvePlaceholderAsync(block, imageAging, describer, verbose, ct).ConfigureAwait(false);
                    blocks.Add(LlmContentBlock.TextBlock(replacementText));
                    changed = true;
                }
                else
                {
                    blocks.Add(block);
                }
            }

            if (changed)
            {
                newBlocks = blocks;
            }
        }

        string? newContent = null;
        if (message.Role == "tool" && message.Content is not null
            && message.Content.Length > MaxToolResultChars)
        {
            newContent = string.Concat(
                message.Content.AsSpan(0, MaxToolResultChars),
                $"\n\n[Content truncated from {message.Content.Length} to {MaxToolResultChars} characters]");
            changed = true;
        }

        if (!changed)
        {
            return message;
        }

        return message with
        {
            Content = newContent ?? message.Content,
            ContentBlocks = newBlocks ?? message.ContentBlocks,
        };
    }

    private static async ValueTask<string> ResolvePlaceholderAsync(
        LlmContentBlock block,
        ImageAgingConfig imageAging,
        IImageDescriber? describer,
        bool verbose,
        CancellationToken ct)
    {
        var mediaType = block.ImageMediaType ?? "image/unknown";

        if (block.ImageDescription is { Length: > 0 })
        {
            return block.ImageDescription;
        }

        if (!imageAging.DescribeOnStrip || describer is null || block.ImageData is null)
        {
            return $"[Image removed: {mediaType}]";
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(block.ImageData);
        }
        catch (FormatException)
        {
            return $"[Image removed: {mediaType}]";
        }

        var description = verbose
            ? await describer.DescribeVerboseAsync(bytes, mediaType, ct).ConfigureAwait(false)
            : await describer.DescribeAsync(bytes, mediaType, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(description))
        {
            return $"[Image removed: {mediaType}]";
        }

        block.ImageDescription = description;
        return description;
    }

    /// <summary>
    /// Patterns that indicate the LLM rejected the request because the context
    /// window was exceeded. Covers Anthropic, OpenAI, Copilot, Bedrock, Groq,
    /// DeepSeek, and common local model servers.
    /// </summary>
    private static readonly string[] ContextOverflowPatterns =
    [
        "prompt is too long",
        "input is too long",
        "exceeds the context window",
        "context window exceeded",
        "context_length_exceeded",
        "context length exceeded",
        "reduce the length of the messages",
        "maximum context length",
        "exceeds the limit of",
        "exceeds the available context size",
        "greater than the context length",
        "exceeded model token limit",
        "request entity too large",
        "no conversation messages fit within the token budget",
    ];

    /// <summary>
    /// Determines whether an LLM error message indicates a context window overflow.
    /// </summary>
    public static bool IsContextOverflow(string errorMessage)
    {
        foreach (var pattern in ContextOverflowPatterns)
        {
            if (errorMessage.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // HTTP 413 (Request Entity Too Large) in error strings like "HTTP 413: ..."
        return errorMessage.Contains("HTTP 413", StringComparison.OrdinalIgnoreCase);
    }
}
