using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Coding;

/// <summary>
/// Abstraction over the coding agent (Claude Code) running in the Bridge process.
/// MVP implementation forwards every call through the SignalR connection to Bridge.
/// A future ACP-based implementation can drop in here without touching tools or injection.
/// </summary>
public interface ICodingAgent
{
    Task<CodingStatus> StartSessionAsync(CodingStartRequest request, CancellationToken cancellationToken);

    Task<CodingStatus> ResumeSessionAsync(CodingResumeRequest request, CancellationToken cancellationToken);

    Task<CodingSendResponse> SendMessageAsync(CodingSendRequest request, CancellationToken cancellationToken);

    Task RespondAsync(CodingRespondRequest request, CancellationToken cancellationToken);

    Task<CodingSetGoalResponse> SetGoalAsync(CodingSetGoalRequest request, CancellationToken cancellationToken);

    Task<CodingEndResponse> InterruptAsync(string sessionId, CancellationToken cancellationToken);

    Task<CodingEndResponse> EndSessionAsync(string sessionId, CancellationToken cancellationToken);

    Task<CodingStatus?> GetStatusAsync(string sessionId, CancellationToken cancellationToken);

    Task<CodingHistory> GetHistoryAsync(string sessionId, int? sinceIndex, CancellationToken cancellationToken);

    Task<IReadOnlyList<CodingStatus>> ListSessionsAsync(CancellationToken cancellationToken);

    Task<bool> IsFolderAllowedAsync(string absolutePath, CancellationToken cancellationToken);

    Task<CodingFolderList> ListAllowedFoldersAsync(CancellationToken cancellationToken);
}
