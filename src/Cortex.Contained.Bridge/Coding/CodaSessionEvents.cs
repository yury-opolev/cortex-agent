using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Coding;

public sealed record CodaFinalResultEvent(string SessionId, string TaskId, string FinalText, IReadOnlyList<CodingToolCall> ToolCalls);

public sealed record CodaErrorEvent(string SessionId, int? ExitCode, string? StderrTail, string Message);

/// <summary>
/// Raised when the idle watchdog detects coda has gone unresponsive mid-turn (a stall, not a
/// logic error). Carries liveness context so the orchestrator can relay it as <c>status=stalled</c>
/// and decide to resume rather than treat the task as a hard failure.
/// </summary>
public sealed record CodaStalledEvent(string SessionId, int IdleSeconds, bool WasStreaming, long? StreamedChars, string? StderrTail, string Message);

/// <summary>
/// Raised when coda ended a turn early on a recoverable limit (output <c>max_tokens</c> or the
/// tool-iteration backstop). NOT a crash — the session returns to idle and the run can be continued.
/// <c>Kind</c> is a stable machine-readable reason (e.g. <c>max_tokens</c>, <c>max_tool_iterations</c>).
/// </summary>
public sealed record CodaLimitReachedEvent(string SessionId, string Kind, string Message);

public sealed record CodaPermissionRequestEvent(string SessionId, string RequestId, string ToolName, string InputPreview);

public sealed record CodaQuestionEvent(string SessionId, string RequestId, string Question, IReadOnlyList<string> Options, bool MultiSelect);

public sealed record CodaPlanApprovalEvent(string SessionId, string RequestId, string Plan);
