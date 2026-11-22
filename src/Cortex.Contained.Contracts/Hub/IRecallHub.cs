namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Recall settings, status, purge preview and quarantine review exposed by the agent (the Bridge
/// "Recall" page). Part of the composed <see cref="IAgentHub"/> surface.
/// </summary>
public interface IRecallHub
{
    /// <summary>The effective recall settings (configured defaults plus persisted overrides).</summary>
    Task<RecallSettingsDto> GetRecallSettings();

    /// <summary>Validates, persists (agent state volume) and applies a settings change live.</summary>
    Task<RecallSettingsUpdateResultDto> UpdateRecallSettings(RecallSettingsPatchDto patch);

    /// <summary>History index, embedding, reranker, backfill and quarantine state.</summary>
    Task<RecallStatusDto> GetRecallStatus();

    /// <summary>Counts what <see cref="IHistoryHub.PurgeHistory"/> would delete for the same request; deletes nothing.</summary>
    Task<HistoryPurgePreviewDto> PreviewHistoryPurge(HistoryPurgeRequestDto request);

    /// <summary>One page of the quarantined memories (newest first) with the purge that quarantined each one, and the total.</summary>
    Task<QuarantineReviewListDto> ListQuarantineReview(int offset, int limit);

    /// <summary>The id of every quarantined memory in list order (no content), for "select all".</summary>
    Task<IReadOnlyList<string>> ListQuarantineReviewIds();

    /// <summary>Keeps the given quarantined memories (visible to the agent again). Ids not in quarantine are skipped.</summary>
    Task<QuarantineDecisionResultDto> KeepQuarantinedMemories(IReadOnlyList<string> memoryIds);

    /// <summary>Erases the given quarantined memories in one batch. Ids not in quarantine are skipped.</summary>
    Task<QuarantineDecisionResultDto> DeleteQuarantinedMemories(IReadOnlyList<string> memoryIds);
}
