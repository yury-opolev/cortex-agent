using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Evals;

/// <summary>
/// Decorating <see cref="ILlmClient"/> that records every LLM request/response
/// for eval analysis. Delegates all work to the inner client.
/// </summary>
public sealed class RecordingLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly List<LlmCallRecord> _calls = [];
    private readonly object _lock = new();

    public RecordingLlmClient(ILlmClient inner)
    {
        _inner = inner;
    }

    /// <summary>All recorded LLM calls (thread-safe snapshot).</summary>
    public IReadOnlyList<LlmCallRecord> GetCalls()
    {
        lock (_lock)
        {
            return [.. _calls];
        }
    }

    /// <summary>Clears all recorded calls.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _calls.Clear();
        }
    }

    public async Task<LlmCompletionResult> CompleteAsync(
        LlmCompletionRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var result = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        var record = new LlmCallRecord
        {
            RequestId = request.RequestId,
            ConversationId = request.ConversationId,
            Model = request.Model,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            Messages = request.Messages.Select(m => new LlmCallMessage
            {
                Role = m.Role,
                Content = m.Content,
            }).ToList(),
            ResponseContent = result.Content,
            ResponseSuccess = result.Success,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
            FinishReason = result.FinishReason,
            Usage = result.Usage is not null ? new LlmCallUsage
            {
                PromptTokens = result.Usage.PromptTokens,
                CompletionTokens = result.Usage.CompletionTokens,
                TotalTokens = result.Usage.TotalTokens,
            } : null,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            Timestamp = DateTimeOffset.UtcNow,
        };

        lock (_lock)
        {
            _calls.Add(record);
        }

        return result;
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamCompleteAsync(
        LlmCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Evals only use CompleteAsync, but implement for completeness.
        // We don't record streaming calls since they're not used in extraction.
        await foreach (var chunk in _inner.StreamCompleteAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }
}

/// <summary>A recorded LLM call with full request/response details.</summary>
public sealed class LlmCallRecord
{
    public required string RequestId { get; init; }
    public required string ConversationId { get; init; }
    public required string Model { get; init; }
    public required double Temperature { get; init; }
    public required int MaxTokens { get; init; }
    public required List<LlmCallMessage> Messages { get; init; }
    public string? ResponseContent { get; init; }
    public required bool ResponseSuccess { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FinishReason { get; init; }
    public LlmCallUsage? Usage { get; init; }
    public required double DurationMs { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

public sealed class LlmCallMessage
{
    public required string Role { get; init; }
    public string? Content { get; init; }
}

public sealed class LlmCallUsage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
}
