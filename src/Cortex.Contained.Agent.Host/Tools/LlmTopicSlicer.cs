using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// LLM-backed implementation of <see cref="ITopicSlicer"/>. Sends the source history to
/// a single LLM call with the slicer system prompt, parses the structured JSON response,
/// falls back to a safe "last 10 verbatim, no prior summary" slice if the JSON is
/// malformed, and reports hard failures (throws, non-success responses) via
/// <see cref="TopicSliceOutcome.Failure"/>.
/// </summary>
public sealed partial class LlmTopicSlicer : ITopicSlicer
{
    private readonly ILlmClient llmClient;
    private readonly IModelProvider modelProvider;
    private readonly IOptionsMonitor<TransferSessionOptions>? options;
    private readonly ILogger<LlmTopicSlicer> logger;

    public LlmTopicSlicer(
        ILlmClient llmClient,
        IModelProvider modelProvider,
        ILogger<LlmTopicSlicer> logger,
        IOptionsMonitor<TransferSessionOptions>? options = null)
    {
        this.llmClient = llmClient;
        this.modelProvider = modelProvider;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<TopicSliceOutcome> SliceAsync(
        IReadOnlyList<LlmMessage> history,
        string sourceChannelId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var conversationText = BuildConversationText(history);
        var opts = this.options?.CurrentValue;
        var model = !string.IsNullOrWhiteSpace(opts?.SlicerModel)
            ? opts!.SlicerModel
            : this.modelProvider.MemoryModel;
        var systemPrompt = !string.IsNullOrWhiteSpace(opts?.SlicerSystemPromptOverride)
            ? opts!.SlicerSystemPromptOverride
            : SystemPrompt;
        var temperature = opts?.SlicerTemperature ?? 0.3;

        var request = new LlmCompletionRequest
        {
            Model = model,
            Messages =
            [
                new LlmMessage { Role = "system", Content = systemPrompt },
                new LlmMessage { Role = "user", Content = conversationText },
            ],
            RequestId = $"transfer-slicer-{Guid.NewGuid():N}",
            ConversationId = conversationId,
            Temperature = temperature,
        };

        LlmCompletionResult response;
        try
        {
            response = await this.llmClient.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Hard LLM failure is surfaced as TopicSliceOutcome.Failure, not propagated.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogSliceFailed(sourceChannelId, ex.Message);
            return new TopicSliceOutcome.Failure(ex.Message);
        }

        if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
        {
            var reason = response.ErrorMessage ?? "empty response";
            this.LogSliceFailed(sourceChannelId, reason);
            return new TopicSliceOutcome.Failure(reason);
        }

        SlicerJsonShape? parsed = null;
        try
        {
            // Strip ``` fences and trim leading/trailing prose — LLMs frequently wrap JSON
            // even when the prompt says "no fences." Matches the codebase-wide pattern.
            var json = MemoryConsolidationService.StripToJson(response.Content!);
            parsed = JsonSerializer.Deserialize<SlicerJsonShape>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Fall through to fallback below.
        }

        if (parsed is null)
        {
            this.LogSliceFallback(sourceChannelId, "malformed JSON");
            return new TopicSliceOutcome.Success(
                BoundaryIndex: Math.Max(0, history.Count - 10),
                TopicOneLine: "(unspecified — slicer fallback)",
                PriorSummary: null,
                Degraded: true);
        }

        var boundary = Math.Clamp(parsed.BoundaryIndex, 0, history.Count);
        var topic = string.IsNullOrWhiteSpace(parsed.TopicOneLine) ? "(unspecified)" : parsed.TopicOneLine;
        this.LogSliceCompleted(boundary, parsed.PriorSummary is not null);

        return new TopicSliceOutcome.Success(boundary, topic, parsed.PriorSummary, Degraded: false);
    }

    internal const string SystemPrompt = """
        You are preparing to transfer a conversation from one channel to another.
        Identify where the most recent topic begins in the conversation below.

        Return a single JSON object with these fields:
        - boundaryIndex: integer, the 0-indexed position in the conversation where the current topic starts.
          Everything from this index forward will be carried verbatim into the new channel.
        - topicOneLine: string, a short (one-line) description of the current topic in the user's own framing.
        - priorSummary: string or null. A structured summary of everything BEFORE boundaryIndex,
          using these sections (omit any that do not apply):
            ## Goal — the user's overall objective so far.
            ## Instructions and preferences
            ## Discoveries and decisions
            ## Completed actions
            ## Relevant references — file paths, URLs, names, identifiers.

        Rules:
        - The current topic is the most recent semantic shift, not just the last few messages. A topic can span many turns.
        - If the whole conversation is a single topic, set boundaryIndex to 0 and priorSummary to null.
        - If the conversation has fewer than 2 turns of user content, set boundaryIndex to 0 and priorSummary to null.

        Output the JSON object only — no surrounding text, no markdown fences.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Serializes the user/assistant messages of <paramref name="messages"/> into a numbered
    /// transcript for the slicer prompt. Tool plumbing (tool-call and tool-result messages)
    /// is excluded — it isn't meaningful context for the user's topic.
    /// </summary>
    internal static string BuildConversationText(IReadOnlyList<LlmMessage> messages)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Role == "tool" || m.ToolCalls is { Count: > 0 })
            {
                continue;
            }

            if (m.Role == "user" || m.Role == "assistant")
            {
                sb.Append('[').Append(i).Append("] [").Append(m.Role.ToUpperInvariant()).Append("] ");
                sb.AppendLine(m.Content ?? string.Empty);
            }
        }

        return sb.ToString();
    }

    private sealed record SlicerJsonShape
    {
        public int BoundaryIndex { get; init; }
        public string? TopicOneLine { get; init; }
        public string? PriorSummary { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "topic_slicer completed: boundaryIndex={BoundaryIndex} priorSummary={HasPriorSummary}")]
    private partial void LogSliceCompleted(int boundaryIndex, bool hasPriorSummary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "topic_slicer fallback: source={SourceChannel} reason={Reason}")]
    private partial void LogSliceFallback(string sourceChannel, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "topic_slicer failed: source={SourceChannel} reason={Reason}")]
    private partial void LogSliceFailed(string sourceChannel, string reason);
}
