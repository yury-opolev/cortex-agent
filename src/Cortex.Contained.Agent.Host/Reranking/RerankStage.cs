using System.Globalization;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>The search surface a rerank belongs to (each has its own budget and switch).</summary>
public enum RerankSurface
{
    /// <summary><c>history_search</c>, hybrid relevance order.</summary>
    History,

    /// <summary><c>memory_search</c>, hybrid mode.</summary>
    Memory,
}

/// <summary>What happened to one rerank request.</summary>
public enum RerankStatus
{
    /// <summary>The first candidates were reordered by the reranker.</summary>
    Applied,

    /// <summary>The reranker was not called (nothing to reorder, unavailable, circuit open); fused order kept.</summary>
    Skipped,

    /// <summary>The call failed or timed out; fused order kept.</summary>
    Failed,
}

/// <summary>Result of <see cref="IRerankStage.RerankAsync"/>.</summary>
/// <param name="Order">A permutation of the candidate indices, best first.</param>
/// <param name="Status">Applied, skipped or failed.</param>
/// <param name="Note">The honest coverage text shown to the agent (<c>rerank: …</c>).</param>
/// <param name="Scores">Reranker score per reranked candidate index.</param>
/// <param name="LatencyMs">Time spent in the reranker call (0 when it was not called).</param>
public sealed record RerankOutcome(IReadOnlyList<int> Order, RerankStatus Status, string Note, IReadOnlyDictionary<int, double> Scores, double LatencyMs)
{
    /// <summary>The fused order unchanged, with a reason.</summary>
    public static RerankOutcome Unchanged(int count, RerankStatus status, string note, double latencyMs = 0) =>
        new(Enumerable.Range(0, count).ToList(), status, note, new Dictionary<int, double>(), latencyMs);
}

/// <summary>The <c>reranker</c> block of <c>/health</c>.</summary>
/// <param name="Enabled">The Bridge enabled reranking.</param>
/// <param name="State"><c>off</c>, <c>ok</c>, <c>circuit-open</c>, <c>half-open</c> or <c>unavailable: reason</c>.</param>
/// <param name="ConsecutiveFailures">Failures since the last success.</param>
/// <param name="CircuitOpenUntil">End of the current break, or null.</param>
/// <param name="LastError">Last failure (no text from searches), or null.</param>
/// <param name="LastLatencyMs">Latency of the last call, or null.</param>
/// <param name="LastCallAt">When the reranker was last called, or null.</param>
public sealed record RerankHealth(
    bool Enabled, string State, int ConsecutiveFailures, DateTimeOffset? CircuitOpenUntil, string? LastError, double? LastLatencyMs, DateTimeOffset? LastCallAt);

/// <summary>The reranking stage after fusion, shared by history and memory search.</summary>
public interface IRerankStage
{
    /// <summary>The configured budget of <paramref name="surface"/>, or null when reranking is off for it.</summary>
    RerankBudget? BudgetFor(RerankSurface surface);

    /// <summary>
    /// Reorders the first <see cref="RerankBudget.TopN"/> of <paramref name="candidates"/> (each given as its
    /// raw best-matching lines) by reranker relevance; never throws for reranker failures.
    /// </summary>
    Task<RerankOutcome> RerankAsync(string query, IReadOnlyList<IReadOnlyList<string>> candidates, RerankBudget budget, CancellationToken cancellationToken);
}

/// <summary>
/// The reranking stage: budget (top N candidates, raw best-matching lines capped per candidate),
/// exposure guard, per-call timeout and a consecutive-failure circuit breaker. A skipped or failed
/// rerank keeps the fused order and says so; it never fails the search.
/// </summary>
public sealed partial class RerankStage : IRerankStage
{
    private readonly IReranker reranker;
    private readonly IOptionsMonitor<RerankerOptions> options;
    private readonly RerankerAccess access;
    private readonly ILogger<RerankStage> logger;
    private readonly TimeProvider time;
    private readonly RerankCircuitBreaker breaker;
    private volatile LastCall? last;

    public RerankStage(IReranker reranker, IOptionsMonitor<RerankerOptions> options, RerankerAccess access, ILogger<RerankStage> logger, TimeProvider? time = null)
    {
        this.reranker = reranker;
        this.options = options;
        this.access = access;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
        this.breaker = new RerankCircuitBreaker(this.time);
    }

    /// <inheritdoc />
    public RerankBudget? BudgetFor(RerankSurface surface)
    {
        if (!this.access.Enabled)
        {
            return null;
        }

        var settings = this.options.CurrentValue;
        var surfaceOptions = surface == RerankSurface.History ? settings.History : settings.Memory;
        return surfaceOptions.Enabled ? surfaceOptions.Budget() : null;
    }

    /// <inheritdoc />
    public async Task<RerankOutcome> RerankAsync(
        string query, IReadOnlyList<IReadOnlyList<string>> candidates, RerankBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(budget);
        var count = candidates.Count;
        var take = Math.Min(budget.TopN, count);
        if (take < 2)
        {
            return RerankOutcome.Unchanged(count, RerankStatus.Skipped, "skipped: fewer than two candidates to reorder");
        }

        if (this.reranker.UnavailableReason is { } reason)
        {
            return RerankOutcome.Unchanged(count, RerankStatus.Skipped, "skipped: " + reason);
        }

        if (!this.breaker.TryEnter())
        {
            return RerankOutcome.Unchanged(count, RerankStatus.Skipped, string.Create(CultureInfo.InvariantCulture,
                $"skipped: circuit open after {this.breaker.ConsecutiveFailures} failures (next try after {this.breaker.OpenUntil:HH:mm:ss}Z)"));
        }

        var settings = this.options.CurrentValue;
        var documents = candidates.Take(take).Select(lines => RerankDocument.Build(query, lines, budget.MaxDocChars)).ToList();
        var started = this.time.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.TimeoutMs);
        var settled = false;
        try
        {
            var scores = await this.reranker.RerankAsync(query, documents, timeout.Token).ConfigureAwait(false);
            var latency = this.time.GetElapsedTime(started).TotalMilliseconds;
            settled = true;
            this.breaker.Success();
            this.last = new LastCall(this.time.GetUtcNow(), latency, null);
            var byIndex = scores.ToDictionary(s => s.Index, s => s.Score);
            var head = Enumerable.Range(0, take)
                .OrderByDescending(i => byIndex.TryGetValue(i, out var score) ? score : double.NegativeInfinity)
                .ThenBy(i => i);
            return new RerankOutcome(
                [.. head, .. Enumerable.Range(take, count - take)],
                RerankStatus.Applied,
                string.Create(CultureInfo.InvariantCulture, $"on (top {take} reordered, ≤{budget.MaxDocChars} chars of matching lines each)"),
                byIndex,
                latency);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            settled = true;
            return this.Failed(count, started, string.Create(CultureInfo.InvariantCulture, $"timed out after {settings.TimeoutMs} ms"), settings);
        }
        catch (RerankerException ex)
        {
            settled = true;
            return this.Failed(count, started, ex.Message.TrimEnd('.'), settings);
        }
        finally
        {
            if (!settled)
            {
                // Cancelled (or an unexpected error): neither a success nor a reranker failure, so a half-open trial must not stay claimed.
                this.breaker.Abandon();
            }
        }
    }

    /// <summary>The <c>/health</c> block.</summary>
    public RerankHealth Snapshot()
    {
        var last = this.last;
        var state = !this.access.Enabled ? "off"
            : this.reranker.UnavailableReason is { } reason ? "unavailable: " + reason
            : this.breaker.State == RerankCircuitState.Open ? "circuit-open"
            : this.breaker.State == RerankCircuitState.HalfOpen ? "half-open"
            : "ok";
        return new RerankHealth(this.access.Enabled, state, this.breaker.ConsecutiveFailures, this.breaker.OpenUntil, last?.Error, last?.LatencyMs, last?.At);
    }

    private RerankOutcome Failed(int count, long started, string error, RerankerOptions settings)
    {
        var latency = this.time.GetElapsedTime(started).TotalMilliseconds;
        this.last = new LastCall(this.time.GetUtcNow(), latency, error);
        if (this.breaker.Failure(settings.FailureThreshold, TimeSpan.FromSeconds(settings.BreakSeconds)))
        {
            this.LogCircuitOpened(this.breaker.ConsecutiveFailures, settings.BreakSeconds, error);
        }
        else
        {
            this.LogRerankFailed(error);
        }

        return RerankOutcome.Unchanged(count, RerankStatus.Failed, "failed: " + error + "; fused order kept", latency);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rerank failed, fused order kept: {Error}")]
    private partial void LogRerankFailed(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reranker circuit opened after {Failures} consecutive failures for {BreakSeconds}s: {Error}")]
    private partial void LogCircuitOpened(int failures, int breakSeconds, string error);

    private sealed record LastCall(DateTimeOffset At, double LatencyMs, string? Error);
}
