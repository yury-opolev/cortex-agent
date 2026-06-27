using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// External agent (Claude Code) push-backs exposed by the agent.
/// Bridge → Agent direction: events from running claude sessions flow up to the
/// agent for injection into the channel's conversation. Part of the composed
/// <see cref="IAgentHub"/> surface — these methods share the single SignalR hub
/// connection and route by method name.
/// </summary>
public interface ICodingHub
{
    /// <summary>
    /// Bridge → Agent. Final assistant message + tool-call summary for a completed task.
    /// </summary>
    Task NotifyCodingFinalResult(CodingFinalResultEvent evt);

    /// <summary>
    /// Bridge → Agent. Claude has asked permission for a tool; the session is paused
    /// awaiting <see cref="IAgentHubClient.RespondCodingPrompt"/>.
    /// </summary>
    Task NotifyCodingPermissionRequest(CodingPermissionRequestEvent evt);

    /// <summary>
    /// Bridge → Agent. The coding agent has asked a question (coda request/question).
    /// </summary>
    Task NotifyCodingQuestion(CodingQuestionRequestEvent evt);

    /// <summary>
    /// Bridge → Agent. The coding agent has requested plan approval (coda request/planApproval).
    /// </summary>
    Task NotifyCodingPlanApproval(CodingPlanApprovalRequestEvent evt);

    /// <summary>
    /// Bridge → Agent. The session crashed or hit a hard error.
    /// </summary>
    Task NotifyCodingError(CodingErrorEvent evt);

    /// <summary>
    /// Bridge → Agent. The idle watchdog detected coda went unresponsive mid-turn and terminated
    /// it (a stall, not a logic error). Relayed distinctly so the agent can offer to resume.
    /// </summary>
    Task NotifyCodingStalled(CodingStalledEvent evt);

    /// <summary>
    /// Bridge → Agent. Coda ended a turn early on a recoverable limit (output <c>max_tokens</c> or the
    /// tool-iteration backstop). Not a crash — the session is idle and the run can be continued.
    /// </summary>
    Task NotifyCodingLimitReached(CodingLimitReachedEvent evt);
}
