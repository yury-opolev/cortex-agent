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

    /// <summary>Quarantined memories with the purge that quarantined each one.</summary>
    Task<QuarantineReviewListDto> ListQuarantineReview();

    /// <summary>Keeps the given quarantined memories (visible to the agent again). Ids not in quarantine are skipped.</summary>
    Task<QuarantineDecisionResultDto> KeepQuarantinedMemories(IReadOnlyList<string> memoryIds);

    /// <summary>Erases the given quarantined memories in one batch. Ids not in quarantine are skipped.</summary>
    Task<QuarantineDecisionResultDto> DeleteQuarantinedMemories(IReadOnlyList<string> memoryIds);
}
