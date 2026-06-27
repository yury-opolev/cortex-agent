namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Lifecycle state of an external-agent (Claude Code) session.
/// </summary>
public enum CodingSessionState
{
    /// <summary>Session is alive and waiting for the next user message.</summary>
    Idle = 0,

    /// <summary>Session is processing a user message (claude is generating).</summary>
    Working = 1,

    /// <summary>Claude has called the permission-prompt tool and we are awaiting a user decision.</summary>
    AwaitingPermission = 2,

    /// <summary>The coding agent has asked a question (coda request/question) that we are awaiting an answer for.</summary>
    AwaitingQuestion = 3,

    /// <summary>The subprocess exited unexpectedly during a task.</summary>
    Crashed = 4,

    /// <summary>The session was explicitly ended (or auto-expired); the row is kept for history.</summary>
    Ended = 5,

    /// <summary>The coding agent has requested plan approval (coda request/planApproval) that we are awaiting a response for.</summary>
    AwaitingPlan = 6,
}
