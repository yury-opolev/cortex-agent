namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Stable error code constants thrown by the external-agent subsystem and surfaced
/// up to tools so the LLM can choose corrective action.
/// </summary>
public static class CodingAgentErrorCodes
{
    public const string MaxSessionsReached = "max_sessions_reached";
    public const string ChannelSessionExists = "channel_session_exists";
    public const string FolderNotFound = "folder_not_found";
    public const string FolderNotAllowed = "yolo_folder_not_allowed";
    public const string CodingAgentUnavailable = "external_agent_unavailable";
    public const string NoActiveSession = "no_active_session";
    public const string SessionBusy = "session_busy";
    public const string SessionCrashed = "session_crashed";
    public const string SessionUnknown = "session_unknown";
    public const string TaskNotAwaiting = "task_not_awaiting";
    public const string TaskUnknown = "task_unknown";
    public const string StartTimeout = "coda_start_timeout";
    public const string StartFailed = "coda_start_failed";
    public const string SessionNotReady = "session_not_ready";
    public const string CodaTimeout = "coda_timeout";
}

/// <summary>
/// Domain exception raised by the session manager so transport layers can surface
/// the stable <see cref="ErrorCode"/> without leaking implementation details.
/// </summary>
public sealed class CodingAgentException : Exception
{
    public CodingAgentException(string errorCode, string message)
        : base(message)
    {
        this.ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
