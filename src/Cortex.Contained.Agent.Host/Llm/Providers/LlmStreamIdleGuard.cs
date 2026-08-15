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
    internal TimeSpan FirstChunk { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum silence between chunks once generation has started. Raised from 120s after the
    /// 2026-08-15 incident: 120s had no measured basis, while legitimate quiet periods of
    /// 135-183s were already on record, and a breach here is NOT recoverable by the facade
    /// (its retry and failover are pre-content only). Provider heartbeats now re-arm this
    /// budget, so it only has to cover silence that is genuinely unexplained.
    /// </summary>
    internal TimeSpan BetweenChunks { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Absolute wall-clock ceiling on a single provider stream, regardless of how chatty it is.
    /// <para>
    /// The idle budgets alone stopped being a bound once provider heartbeats began re-arming
    /// them: a provider pinging every 30s, or a model stuck emitting thinking deltas, would
    /// otherwise hold the request, the connection and the subagent slot open forever - a worse
    /// failure than the premature kill this guard was loosened to avoid, because it is silent
    /// and never terminates. Generous by design: it is a backstop, not a latency budget.
    /// </para>
    /// </summary>
    internal TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(30);

    internal static LlmStreamTimeouts Default { get; } = new();

    /// <summary>
    /// Builds budgets from configured seconds. A non-positive value disables that budget, which
    /// is the documented escape hatch for an operator chasing a provider-side anomaly.
    /// </summary>
    internal static LlmStreamTimeouts FromSeconds(
        int firstChunkSeconds,
        int betweenChunksSeconds,
        int maxDurationSeconds = 0) => new()
    {
        FirstChunk = firstChunkSeconds > 0
            ? TimeSpan.FromSeconds(firstChunkSeconds)
            : Timeout.InfiniteTimeSpan,
        BetweenChunks = betweenChunksSeconds > 0
            ? TimeSpan.FromSeconds(betweenChunksSeconds)
            : Timeout.InfiniteTimeSpan,
        MaxDuration = maxDurationSeconds > 0
            ? TimeSpan.FromSeconds(maxDurationSeconds)
            : (maxDurationSeconds < 0 ? Timeout.InfiniteTimeSpan : Default.MaxDuration),
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
        [EnumeratorCancellation] CancellationToken cancellationToken,
        LlmStreamContext? context = null,
        ILlmStreamStallObserver? stallObserver = null)
    {
        // The linked source lets a breach cancel the pending provider read; without it the
        // abandoned read would keep the connection and its buffers alive until GC.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = source.GetAsyncEnumerator(linked.Token);
        var started = false;

        // Observability only — never used to decide whether to break the stream.
        var chunksReceived = 0;
        var keepAlivesReceived = 0;
        var contentCharsReceived = 0;

        // Bounds the whole stream, so keep-alives cannot re-arm the idle budgets forever.
        var totalElapsed = System.Diagnostics.Stopwatch.StartNew();

        // An abandoned MoveNextAsync, kept so a breach can settle it before disposal. Calling
        // DisposeAsync while a MoveNextAsync is still in flight is undefined for an async
        // iterator, so the guard must never race the two.
        Task<bool>? pending = null;

        try
        {
            while (true)
            {
                var budget = started ? timeouts.BetweenChunks : timeouts.FirstChunk;

                // Never wait past the absolute deadline: whichever bound expires first wins.
                var deadlineIsBinding = false;
                if (timeouts.MaxDuration != Timeout.InfiniteTimeSpan)
                {
                    var remaining = timeouts.MaxDuration - totalElapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        remaining = TimeSpan.Zero;
                    }

                    if (budget == Timeout.InfiniteTimeSpan || remaining < budget)
                    {
                        budget = remaining;
                        deadlineIsBinding = true;
                    }
                }

                LlmStreamChunk? chunk = null;
                var timedOut = false;
                var idleSince = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    bool hasChunk;
                    if (budget == Timeout.InfiniteTimeSpan)
                    {
                        hasChunk = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        // Materialised once: a ValueTask may only be consumed a single time, and
                        // the Task form is what survives an abandoned WaitAsync.
                        pending = enumerator.MoveNextAsync().AsTask();
                        hasChunk = await pending.WaitAsync(budget, cancellationToken).ConfigureAwait(false);
                        pending = null;
                    }

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
                    // Captured BEFORE the unwind below, which can take up to SettleGrace and
                    // would otherwise be reported as though the provider had been silent for it.
                    var idleElapsed = idleSince.Elapsed;

                    // Cancel the provider read, then give it a bounded moment to unwind so the
                    // pending MoveNextAsync settles before the finally disposes the enumerator.
                    await linked.CancelAsync().ConfigureAwait(false);
                    pending = await SettleAsync(pending).ConfigureAwait(false);

                    var phase = deadlineIsBinding
                        ? LlmStreamStallPhase.MaxDuration
                        : started ? LlmStreamStallPhase.BetweenChunks : LlmStreamStallPhase.FirstChunk;

                    Report(
                        stallObserver,
                        context,
                        phase,
                        deadlineIsBinding ? timeouts.MaxDuration : budget,
                        deadlineIsBinding ? totalElapsed.Elapsed : idleElapsed,
                        started,
                        chunksReceived,
                        keepAlivesReceived,
                        contentCharsReceived);

                    throw new TimeoutException(
                        phase switch
                        {
                            LlmStreamStallPhase.MaxDuration =>
                                $"LLM stream exceeded its maximum duration of {timeouts.MaxDuration.TotalSeconds:0.#}s.",
                            LlmStreamStallPhase.BetweenChunks =>
                                $"LLM stream produced no data for {budget.TotalSeconds:0.#}s.",
                            _ =>
                                $"LLM stream produced no first token within {budget.TotalSeconds:0.#}s.",
                        });
                }

                if (chunk is null)
                {
                    yield break;
                }

                // A keep-alive proves liveness and has already re-armed the budget by arriving.
                // It must NOT flip us onto the tighter between-chunks budget (time-to-first-token
                // is still running), and it must not be forwarded: consumers count any delivered
                // chunk as committed content.
                if (chunk.IsKeepAlive)
                {
                    keepAlivesReceived++;
                    continue;
                }

                chunksReceived++;
                contentCharsReceived += chunk.ContentDelta?.Length ?? 0;
                started = true;
                yield return chunk;
            }
        }
        finally
        {
            // Only dispose when nothing is in flight. A provider read that ignored cancellation
            // is left to the cancelled token and the finalizer rather than risking the undefined
            // concurrent MoveNextAsync/DisposeAsync pair.
            if (pending is null)
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

    /// <summary>
    /// Hands a breach to the observer. A faulty sink must never mask the stall the caller is
    /// about to be told about, so any exception it throws is swallowed.
    /// </summary>
    private static void Report(
        ILlmStreamStallObserver? observer,
        LlmStreamContext? context,
        LlmStreamStallPhase phase,
        TimeSpan budget,
        TimeSpan elapsed,
        bool contentEmitted,
        int chunksReceived,
        int keepAlivesReceived,
        int contentCharsReceived)
    {
        if (observer is null)
        {
            return;
        }

        try
        {
            observer.OnStall(new LlmStreamStallReport
            {
                Phase = phase,
                Budget = budget,
                Elapsed = elapsed,
                ContentEmitted = contentEmitted,
                ChunksReceived = chunksReceived,
                KeepAlivesReceived = keepAlivesReceived,
                ContentCharsReceived = contentCharsReceived,
                ConversationId = context?.ConversationId,
                RequestId = context?.RequestId,
                Model = context?.Model,
                Provider = context?.Provider,
                PromptChars = context?.PromptChars ?? 0,
            });
        }
#pragma warning disable CA1031 // Telemetry must never mask the fault being reported.
        catch (Exception)
#pragma warning restore CA1031
        {
            // ignored
        }
    }

    /// <summary>Grace period for an abandoned read to notice cancellation and unwind.</summary>
    private static readonly TimeSpan SettleGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Waits briefly for an abandoned read to finish, always observing its exception so it never
    /// surfaces as an unobserved task fault. Returns null once settled, or the still-running task
    /// when the provider ignored cancellation.
    /// </summary>
    private static async Task<Task<bool>?> SettleAsync(Task<bool>? pending)
    {
        if (pending is null)
        {
            return null;
        }

        try
        {
            await pending.WaitAsync(SettleGrace).ConfigureAwait(false);
            return null;
        }
        catch (TimeoutException)
        {
            // Still running: observe whenever it does finish, and tell the caller not to dispose.
            _ = pending.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return pending;
        }
        catch (Exception)
        {
            return null; // faulted or cancelled — settled, and now observed
        }
    }
}
