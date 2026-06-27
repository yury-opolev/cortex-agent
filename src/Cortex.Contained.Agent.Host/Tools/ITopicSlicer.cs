using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Identifies the latest topic boundary in a conversation and produces a structured
/// summary of the pre-topic context. Used by <c>transfer_session</c> to decide what
/// to carry verbatim into the target channel vs what to compress into a summary.
/// <para>
/// The slicer owns: prompt construction, LLM I/O, JSON parsing, fallback semantics,
/// and boundary clamping. The caller owns: orchestration, validation, target seeding,
/// and breadcrumb writes.
/// </para>
/// </summary>
public interface ITopicSlicer
{
    /// <summary>
    /// Slice <paramref name="history"/> into (pre-topic summary, post-boundary verbatim).
    /// Returns either <see cref="TopicSliceOutcome.Success"/> (with the slice, possibly
    /// in degraded form if the LLM returned malformed output) or
    /// <see cref="TopicSliceOutcome.Failure"/> (LLM call hard-failed and we couldn't
    /// produce even a fallback slice).
    /// </summary>
    /// <param name="history">Source session's conversation history snapshot.</param>
    /// <param name="sourceChannelId">For logging / contextual hints in the prompt.</param>
    /// <param name="conversationId">For cost tracking on the LLM request.</param>
    /// <param name="cancellationToken">Caller's cancellation token.</param>
    Task<TopicSliceOutcome> SliceAsync(
        IReadOnlyList<LlmMessage> history,
        string sourceChannelId,
        string conversationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of a <see cref="ITopicSlicer.SliceAsync"/> call. Three observable shapes:
/// <list type="bullet">
///   <item><see cref="Success"/> with <c>Degraded == false</c> — normal slice; LLM produced
///     parseable JSON and the slice reflects that.</item>
///   <item><see cref="Success"/> with <c>Degraded == true</c> — slicer's fallback path fired
///     (malformed JSON from the LLM); caller still gets a usable slice but quality is reduced.</item>
///   <item><see cref="Failure"/> — LLM call itself failed (threw, returned non-success, returned
///     empty content); no slice produced. Caller should surface the failure.</item>
/// </list>
/// </summary>
public abstract record TopicSliceOutcome
{
    /// <summary>A slice was produced (possibly in fallback / degraded form).</summary>
    public sealed record Success(int BoundaryIndex, string TopicOneLine, string? PriorSummary, bool Degraded) : TopicSliceOutcome;

    /// <summary>The slicer could not produce any slice. <paramref name="Reason"/> is a short, user-presentable string.</summary>
    public sealed record Failure(string Reason) : TopicSliceOutcome;
}
