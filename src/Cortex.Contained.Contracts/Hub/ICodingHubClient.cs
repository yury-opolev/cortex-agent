using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// External agent (Claude Code) relay callbacks the agent pushes to the Bridge.
/// Agent → Bridge direction: tools running in the Agent Host invoke these methods
/// on the Bridge, which owns the claude subprocesses on the Windows host.
/// Part of the composed <see cref="IAgentHubClient"/> surface — these callbacks
/// share the single SignalR hub connection and route by method name.
/// </summary>
public interface ICodingHubClient
{
    /// <summary>
    /// Agent → Bridge. Start a new Claude Code session in the given working folder.
    /// </summary>
    Task<CodingStatus> StartCodingSession(CodingStartRequest request);

    /// <summary>
    /// Agent → Bridge. Resume an existing Claude Code session by ID.
    /// </summary>
    Task<CodingStatus> ResumeCodingSession(CodingResumeRequest request);

    /// <summary>
    /// Agent → Bridge. Send a user message to a running session. Non-blocking;
    /// the result is pushed back later via <see cref="IAgentHub.NotifyCodingFinalResult"/>.
    /// </summary>
    Task<CodingSendResponse> SendCodingMessage(CodingSendRequest request);

    /// <summary>
    /// Agent → Bridge. Reply to a pending permission ask or clarifying question.
    /// </summary>
    Task RespondCodingPrompt(CodingRespondRequest request);

    /// <summary>
    /// Agent → Bridge. Set, update, or clear a session's autonomous goal and budget in-place.
    /// A null/empty goal clears it. Takes effect from the next message sent to the session.
    /// </summary>
    Task<CodingSetGoalResponse> SetCodingGoal(CodingSetGoalRequest request);

    /// <summary>
    /// Agent → Bridge. Cancel the current task without ending the session.
    /// </summary>
    Task<CodingEndResponse> InterruptCodingSession(string sessionId);

    /// <summary>
    /// Agent → Bridge. End and dispose the session subprocess.
    /// </summary>
    Task<CodingEndResponse> EndCodingSession(string sessionId);

    /// <summary>
    /// Agent → Bridge. Get a status snapshot of a single session.
    /// </summary>
    Task<CodingStatus?> GetCodingStatus(string sessionId);

    /// <summary>
    /// Agent → Bridge. Fetch a session's transcript — full when
    /// <see cref="CodingHistoryRequest.SinceIndex"/> is null, otherwise the messages after that
    /// cursor (plus a <c>nextIndex</c>).
    /// </summary>
    Task<CodingHistory> GetCodingHistory(CodingHistoryRequest request);

    /// <summary>
    /// Agent → Bridge. List all known sessions (live and recently ended).
    /// </summary>
    Task<CodingSessionList> ListCodingSessions();

    /// <summary>
    /// Agent → Bridge. Query whether the given absolute path is a configured coding folder.
    /// </summary>
    Task<bool> IsCodingFolderAllowed(CodingFolderQueryRequest request);

    /// <summary>
    /// Agent → Bridge. List all configured allowed coding folders.
    /// </summary>
    Task<CodingFolderList> ListCodingFolders();
}
