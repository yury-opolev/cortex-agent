using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Recall;

/// <summary>The agent calls the Recall endpoints make (a seam over <see cref="HubClient"/> for tests).</summary>
public interface IRecallAgentClient
{
    Task<RecallSettingsDto> GetSettingsAsync(CancellationToken cancellationToken);

    Task<RecallSettingsUpdateResultDto> UpdateSettingsAsync(RecallSettingsPatchDto patch, CancellationToken cancellationToken);

    Task<RecallStatusDto> GetStatusAsync(CancellationToken cancellationToken);

    Task<HistoryPurgePreviewDto> PreviewPurgeAsync(HistoryPurgeRequestDto request, CancellationToken cancellationToken);

    Task<HistoryPurgeResultDto> PurgeAsync(HistoryPurgeRequestDto request, CancellationToken cancellationToken);

    Task<QuarantineReviewListDto> ListQuarantineAsync(CancellationToken cancellationToken);

    Task<QuarantineDecisionResultDto> KeepAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken);

    Task<QuarantineDecisionResultDto> DeleteAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken);
}

/// <summary>Adapts a connected <see cref="HubClient"/> to <see cref="IRecallAgentClient"/>.</summary>
public sealed class HubRecallAgentClient(HubClient client) : IRecallAgentClient
{
    /// <summary>The adapter for <paramref name="client"/>, or null when it is missing or disconnected.</summary>
    public static IRecallAgentClient? For(HubClient? client) => client is { IsConnected: true } ? new HubRecallAgentClient(client) : null;

    public Task<RecallSettingsDto> GetSettingsAsync(CancellationToken cancellationToken) => client.GetRecallSettingsAsync(cancellationToken);

    public Task<RecallSettingsUpdateResultDto> UpdateSettingsAsync(RecallSettingsPatchDto patch, CancellationToken cancellationToken) =>
        client.UpdateRecallSettingsAsync(patch, cancellationToken);

    public Task<RecallStatusDto> GetStatusAsync(CancellationToken cancellationToken) => client.GetRecallStatusAsync(cancellationToken);

    public Task<HistoryPurgePreviewDto> PreviewPurgeAsync(HistoryPurgeRequestDto request, CancellationToken cancellationToken) =>
        client.PreviewHistoryPurgeAsync(request, cancellationToken);

    public Task<HistoryPurgeResultDto> PurgeAsync(HistoryPurgeRequestDto request, CancellationToken cancellationToken) =>
        client.PurgeHistoryAsync(request, cancellationToken);

    public Task<QuarantineReviewListDto> ListQuarantineAsync(CancellationToken cancellationToken) => client.ListQuarantineReviewAsync(cancellationToken);

    public Task<QuarantineDecisionResultDto> KeepAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken) =>
        client.KeepQuarantinedMemoriesAsync(memoryIds, cancellationToken);

    public Task<QuarantineDecisionResultDto> DeleteAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken) =>
        client.DeleteQuarantinedMemoriesAsync(memoryIds, cancellationToken);
}
