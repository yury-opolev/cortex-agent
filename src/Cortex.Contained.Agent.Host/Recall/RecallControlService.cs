using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Contracts.Hub;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Recall;

/// <summary>
/// Agent-side facade behind the <see cref="IRecallHub"/> methods: settings (through
/// <see cref="RecallSettingsStore"/>), status, purge preview and quarantine review. The hub stays a thin
/// adapter; every decision here is unit-testable without SignalR.
/// </summary>
public sealed class RecallControlService
{
    private readonly RecallSettingsStore store;
    private readonly IOptionsMonitor<HistoryOptions> history;
    private readonly IOptionsMonitor<RerankerOptions> reranker;
    private readonly IOptionsMonitor<MemoryMcpOptions> memory;
    private readonly HistoryHealthReporter health;
    private readonly Func<RerankHealth> rerankHealth;
    private readonly MemoryQuarantine quarantine;
    private readonly QuarantineReviewService review;
    private readonly HistoryPurgeService purge;
    private readonly EventBackfillService? backfill;

    public RecallControlService(
        RecallSettingsStore store,
        IOptionsMonitor<HistoryOptions> history,
        IOptionsMonitor<RerankerOptions> reranker,
        IOptionsMonitor<MemoryMcpOptions> memory,
        HistoryHealthReporter health,
        Func<RerankHealth> rerankHealth,
        MemoryQuarantine quarantine,
        QuarantineReviewService review,
        HistoryPurgeService purge,
        EventBackfillService? backfill = null)
    {
        this.store = store;
        this.history = history;
        this.reranker = reranker;
        this.memory = memory;
        this.health = health;
        this.rerankHealth = rerankHealth;
        this.quarantine = quarantine;
        this.review = review;
        this.purge = purge;
        this.backfill = backfill;
    }

    /// <summary>The effective settings.</summary>
    public RecallSettingsDto GetSettings() =>
        RecallStatusMapper.Settings(this.history.CurrentValue, this.reranker.CurrentValue, this.memory.CurrentValue.Search, this.store.Current);

    /// <summary>Validates, persists and applies <paramref name="patch"/>; explains consequences.</summary>
    public RecallSettingsUpdateResultDto Update(RecallSettingsPatchDto patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var before = this.GetSettings();
        var applied = this.store.Apply(patch);
        var after = this.GetSettings();
        return new RecallSettingsUpdateResultDto
        {
            Applied = applied.Applied,
            Errors = applied.Errors,
            Notices = applied.Applied ? RecallNotices.For(before, after) : [],
            Settings = after,
        };
    }

    /// <summary>History index, embeddings, reranker, backfill and quarantine state.</summary>
    public async Task<RecallStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var historyHealth = await this.health.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var quarantined = (await this.quarantine.GetQuarantinedIdsAsync(cancellationToken).ConfigureAwait(false)).Count;
        return RecallStatusMapper.Status(this.GetSettings(), historyHealth, this.rerankHealth(), this.backfill?.Status, quarantined);
    }

    /// <summary>What a purge of <paramref name="request"/> would delete.</summary>
    /// <exception cref="ArgumentException">The time range is empty or inverted.</exception>
    public async Task<HistoryPurgePreviewDto> PreviewPurgeAsync(HistoryPurgeRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = new HistoryPurgeScope(string.IsNullOrWhiteSpace(request.ChannelId) ? null : request.ChannelId, request.OlderThan, request.From);
        if (scope.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(request));
        }

        var preview = await this.purge.PreviewAsync(scope, cancellationToken).ConfigureAwait(false);
        return new HistoryPurgePreviewDto
        {
            Messages = preview.Messages,
            DependentMemories = preview.DependentMemories,
            RangeFrom = preview.RangeFrom,
            RangeTo = preview.RangeTo,
        };
    }

    /// <summary>Quarantined memories with their purge source.</summary>
    public Task<QuarantineReviewListDto> ListQuarantineAsync(CancellationToken cancellationToken) => this.review.ListAsync(cancellationToken);

    /// <summary>Bulk keep.</summary>
    public Task<QuarantineDecisionResultDto> KeepAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken) =>
        this.review.KeepAsync(memoryIds, cancellationToken);

    /// <summary>Bulk delete (one batched delete).</summary>
    public Task<QuarantineDecisionResultDto> DeleteAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken) =>
        this.review.DeleteAsync(memoryIds, cancellationToken);
}

/// <summary>Plain-language consequences of a recall settings change, shown after saving.</summary>
public static class RecallNotices
{
    /// <summary>Notices for the change from <paramref name="before"/> to <paramref name="after"/>.</summary>
    public static IReadOnlyList<string> For(RecallSettingsDto before, RecallSettingsDto after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var notices = new List<string>();
        if (!before.HistoryEnabled && after.HistoryEnabled)
        {
            notices.Add("History search is on. The index is being built in the background; until it completes, searches cover only recent messages (see Status).");
        }
        else if (before.HistoryEnabled && !after.HistoryEnabled)
        {
            notices.Add("History search is off. The recall tools are hidden now; the existing index is erased by the next history deletion, and switching back on rebuilds it.");
        }

        if (after.HistoryEnabled && before.HistoryDenseEnabled != after.HistoryDenseEnabled)
        {
            notices.Add(after.HistoryDenseEnabled
                ? "Dense embeddings are on. The history index is rebuilt and then embedded in the background; hybrid search starts once chunks are embedded."
                : "Dense embeddings are off. The history index is rebuilt without vectors; searches are keyword-only.");
        }

        if (after.HistoryEnabled && !after.HistoryDenseEnabled && after.HistorySearchMode != "keyword")
        {
            notices.Add("Hybrid history search needs dense embeddings; until they are on, history searches stay keyword-only.");
        }

        return notices;
    }
}
