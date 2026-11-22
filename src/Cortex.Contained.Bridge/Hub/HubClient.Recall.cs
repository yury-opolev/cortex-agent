using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Bridge.Hub;

/// <summary>Recall-page calls into the agent (<see cref="IRecallHub"/> plus the purge and backfill methods).</summary>
public sealed partial class HubClient
{
    /// <summary>The agent's effective recall settings.</summary>
    public async Task<RecallSettingsDto> GetRecallSettingsAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<RecallSettingsDto>(nameof(IAgentHub.GetRecallSettings), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates, persists and applies a recall settings change on the agent.</summary>
    public async Task<RecallSettingsUpdateResultDto> UpdateRecallSettingsAsync(RecallSettingsPatchDto patch, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<RecallSettingsUpdateResultDto>(nameof(IAgentHub.UpdateRecallSettings), patch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>History index, embeddings, reranker, backfill and quarantine state.</summary>
    public async Task<RecallStatusDto> GetRecallStatusAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<RecallStatusDto>(nameof(IAgentHub.GetRecallStatus), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a history deletion would remove.</summary>
    public async Task<HistoryPurgePreviewDto> PreviewHistoryPurgeAsync(HistoryPurgeRequestDto request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<HistoryPurgePreviewDto>(nameof(IAgentHub.PreviewHistoryPurge), request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One page of quarantined memories with the purge that quarantined each.</summary>
    public async Task<QuarantineReviewListDto> ListQuarantineReviewAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<QuarantineReviewListDto>(nameof(IAgentHub.ListQuarantineReview), offset, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every quarantined memory id (no content).</summary>
    public async Task<IReadOnlyList<string>> ListQuarantineReviewIdsAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<IReadOnlyList<string>>(nameof(IAgentHub.ListQuarantineReviewIds), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bulk keep of quarantined memories.</summary>
    public async Task<QuarantineDecisionResultDto> KeepQuarantinedMemoriesAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<QuarantineDecisionResultDto>(nameof(IAgentHub.KeepQuarantinedMemories), memoryIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bulk delete of quarantined memories (one batched delete on the agent).</summary>
    public async Task<QuarantineDecisionResultDto> DeleteQuarantinedMemoriesAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken)
    {
        EnsureConnected();
        return await this.connection!.InvokeAsync<QuarantineDecisionResultDto>(nameof(IAgentHub.DeleteQuarantinedMemories), memoryIds, cancellationToken).ConfigureAwait(false);
    }
}
