using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Agent.Host.Hubs;

/// <summary><see cref="IRecallHub"/>: the Bridge Recall page (settings, status, purge preview, quarantine review).</summary>
public sealed partial class AgentHub
{
    /// <inheritdoc />
    public Task<RecallSettingsDto> GetRecallSettings() => Task.FromResult(this.Recall.GetSettings());

    /// <inheritdoc />
    public Task<RecallSettingsUpdateResultDto> UpdateRecallSettings(RecallSettingsPatchDto patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return Task.FromResult(this.Recall.Update(patch));
    }

    /// <inheritdoc />
    public Task<RecallStatusDto> GetRecallStatus() => this.Recall.GetStatusAsync(Context.ConnectionAborted);

    /// <inheritdoc />
    public async Task<HistoryPurgePreviewDto> PreviewHistoryPurge(HistoryPurgeRequestDto request)
    {
        try
        {
            return await this.Recall.PreviewPurgeAsync(request, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<QuarantineReviewListDto> ListQuarantineReview(int offset, int limit)
    {
        try
        {
            return await this.Recall.ListQuarantineAsync(offset, limit, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListQuarantineReviewIds() => this.Recall.ListQuarantineIdsAsync(Context.ConnectionAborted);

    /// <inheritdoc />
    public async Task<QuarantineDecisionResultDto> KeepQuarantinedMemories(IReadOnlyList<string> memoryIds)
    {
        try
        {
            return await this.Recall.KeepAsync(memoryIds, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<QuarantineDecisionResultDto> DeleteQuarantinedMemories(IReadOnlyList<string> memoryIds)
    {
        try
        {
            return await this.Recall.DeleteAsync(memoryIds, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    private Recall.RecallControlService Recall =>
        this.recall ?? throw new HubException("Recall settings are not available on this agent.");
}
