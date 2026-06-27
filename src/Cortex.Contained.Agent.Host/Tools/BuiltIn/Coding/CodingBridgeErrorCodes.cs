namespace Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;

/// <summary>
/// Stable error code constants surfaced from external-agent tools to the LLM,
/// mirroring the codes raised inside the Bridge.
/// </summary>
internal static class CodingBridgeErrorCodes
{
    public const string MaxSessionsReached = "max_sessions_reached";
    public const string ChannelSessionExists = "channel_session_exists";
    public const string FolderNotFound = "folder_not_found";
    public const string FolderNotAllowed = "yolo_folder_not_allowed";
    public const string CodingAgentUnavailable = "external_agent_unavailable";
    public const string NoActiveSession = "no_active_session";
    public const string AmbiguousSession = "ambiguous_session";
    public const string SessionBusy = "session_busy";
    public const string SessionCrashed = "session_crashed";
    public const string SessionUnknown = "session_unknown";
    public const string TaskNotAwaiting = "task_not_awaiting";
    public const string TaskUnknown = "task_unknown";
    public const string CodaTimeout = "coda_timeout";
    public const string CodaStartFailed = "coda_start_failed";
    public const string CodaUnreachable = "coda_unreachable";
    public const string CodaInvalidModel = "coda_invalid_model";
    public const string SessionNotReady = "session_not_ready";
}
