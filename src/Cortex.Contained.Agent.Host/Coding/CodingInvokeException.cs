namespace Cortex.Contained.Agent.Host.Coding;

/// <summary>
/// A coding-agent failure that carries an explicit, state-bearing message for the LLM.
/// <para>
/// The whole point of this type is to never leave the agent guessing: every instance says
/// what state the session is in (<see cref="SessionTerminated"/>) and includes the real
/// underlying reason — so the agent cannot rationalize a hard failure into "probably still
/// running, just couldn't deliver the message".
/// </para>
/// </summary>
public sealed class CodingInvokeException : Exception
{
    private CodingInvokeException(string code, string message, bool sessionTerminated, string? detail)
        : base(message)
    {
        this.Code = code;
        this.SessionTerminated = sessionTerminated;
        this.Detail = detail;
    }

    /// <summary>Stable error code surfaced to the LLM (e.g. <c>coda_start_failed</c>).</summary>
    public string Code { get; }

    /// <summary>The underlying reason (coda error text, stderr tail, etc.), when available.</summary>
    public string? Detail { get; }

    /// <summary>
    /// True when the session is known to be gone (terminated / never started). When false, the
    /// session state is genuinely unknown and the agent must verify before assuming anything.
    /// </summary>
    public bool SessionTerminated { get; }

    /// <summary>
    /// The coding session failed to start. The session is terminated and not running.
    /// </summary>
    public static CodingInvokeException StartFailed(string reason, string? stderrTail)
    {
        var detail = string.IsNullOrWhiteSpace(stderrTail)
            ? reason
            : $"{reason} (coda output: {stderrTail.Trim()})";
        var message =
            $"The coding session FAILED to start: {detail}. No session is running — do not assume work is in progress.";
        return new CodingInvokeException("coda_start_failed", message, sessionTerminated: true, detail);
    }

    /// <summary>
    /// The Bridge did not respond within the invoke ceiling. Unlike a start failure, the session
    /// state is genuinely unknown, so the agent must verify (e.g. via list/status) before acting.
    /// </summary>
    public static CodingInvokeException Unreachable(int seconds)
    {
        var message =
            $"The Bridge did not respond within {seconds}s, so the coding session state is unknown. "
            + "Verify with coding_session_list / coding_session_status before assuming success or failure.";
        return new CodingInvokeException("coda_unreachable", message, sessionTerminated: false, detail: null);
    }

    /// <summary>
    /// Reconstructs an exception from a code+message decoded off the SignalR wire (see
    /// <c>CodingErrorWire</c>). The session is treated as terminated for codes that mean the
    /// session never started or is gone.
    /// </summary>
    public static CodingInvokeException FromWire(string code, string message)
    {
        var terminated = code is "coda_start_failed" or "coda_start_timeout" or "coda_no_provider"
            or "coda_invalid_model" or "session_crashed" or "session_unknown";
        return new CodingInvokeException(code, message, terminated, detail: message);
    }
}
