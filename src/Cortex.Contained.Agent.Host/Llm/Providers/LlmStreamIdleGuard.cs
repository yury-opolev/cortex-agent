using System.Runtime.CompilerServices;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm.Providers;

/// <summary>
/// Inactivity budgets for a provider stream.
/// <para>
/// The first-chunk budget is separate from — and larger than — the between-chunk budget on
/// purpose. Time-to-first-token grows with prompt size (coda measured 135-183s on large
/// grounding prompts), so a single budget either kills healthy slow starts or is too loose to
/// catch a mid-stream stall. coda's original guard made exactly that mistake: it armed the idle
/// timer before the first token and reported healthy slow turns as hung.
/// </para>
/// </summary>
internal sealed record LlmStreamTimeouts
{
    /// <summary>Maximum silence before the first chunk arrives.</summary>
    internal TimeSpan FirstChunk { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum silence between chunks once generation has started.</summary>
    internal TimeSpan BetweenChunks { get; init; } = TimeSpan.FromSeconds(120);

    internal static LlmStreamTimeouts Default { get; } = new();

    /// <summary>
    /// Builds budgets from configured seconds. A non-positive value disables that budget, which
    /// is the documented escape hatch for an operator chasing a provider-side anomaly.
    /// </summary>
    internal static LlmStreamTimeouts FromSeconds(int firstChunkSeconds, int betweenChunksSeconds) => new()
    {
        FirstChunk = firstChunkSeconds > 0
            ? TimeSpan.FromSeconds(firstChunkSeconds)
            : Timeout.InfiniteTimeSpan,
        BetweenChunks = betweenChunksSeconds > 0
            ? TimeSpan.FromSeconds(betweenChunksSeconds)
            : Timeout.InfiniteTimeSpan,
    };
}

/// <summary>
/// Bounds how long a provider stream may stay silent.
/// <para>
/// This is not redundant with <c>HttpClient.Timeout</c>: when a request is sent with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/> — as every streaming provider call is —
/// that timeout stops applying once the headers arrive, leaving body reads unbounded. A provider
/// that accepts the request and then goes silent would hang the turn indefinitely.
/// </para>
/// <para>
/// A breach throws <see cref="TimeoutException"/>, which <see cref="LlmStreamFault"/> classifies
/// as transient — so a stall engages the same same-provider retry and failover path as a dropped
/// connection instead of escaping the facade.
/// </para>
/// </summary>
internal static class LlmStreamIdleGuard
{
    internal static async IAsyncEnumerable<LlmStreamChunk> Apply(
        IAsyncEnumerable<LlmStreamChunk> source,
        LlmStreamTimeouts timeouts,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The linked source lets a breach cancel the pending provider read; without it the
        // abandoned read would keep the connection and its buffers alive until GC.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = source.GetAsyncEnumerator(linked.Token);
        var started = false;

        try
        {
            while (true)
            {
                var budget = started ? timeouts.BetweenChunks : timeouts.FirstChunk;

                LlmStreamChunk? chunk = null;
                var timedOut = false;
                try
                {
                    var advance = enumerator.MoveNextAsync();
                    var hasChunk = budget == Timeout.InfiniteTimeSpan
                        ? await advance.ConfigureAwait(false)
                        : await advance.AsTask().WaitAsync(budget, cancellationToken).ConfigureAwait(false);

                    if (hasChunk)
                    {
                        chunk = enumerator.Current;
                    }
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                }

                if (timedOut)
                {
                    await linked.CancelAsync().ConfigureAwait(false);
                    throw new TimeoutException(
                        started
                            ? $"LLM stream produced no data for {budget.TotalSeconds:0.#}s."
                            : $"LLM stream produced no first token within {budget.TotalSeconds:0.#}s.");
                }

                if (chunk is null)
                {
                    yield break;
                }

                started = true;
                yield return chunk;
            }
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A disposal fault must not mask the outcome already reported.
            }
        }
    }
}
