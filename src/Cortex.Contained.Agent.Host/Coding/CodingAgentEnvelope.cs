using System.Text;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Coding;

/// <summary>
/// Builds the <c>[coding ...]</c> envelopes that the injection service
/// enqueues onto a channel's <c>AgentMessageChannel</c>. The LLM recognizes these
/// envelopes via the system-prompt addendum and relays them to the user.
/// </summary>
public static class CodingAgentEnvelope
{
    public static string BuildFinalResult(CodingFinalResultEvent evt)
    {
        var sb = new StringBuilder();
        sb.Append("[coding session=").Append(evt.SessionId)
            .Append(" status=ready taskId=").Append(evt.TaskId).AppendLine("]");
        sb.AppendLine("Final:");
        sb.AppendLine(evt.FinalText);

        if (evt.ToolCalls.Count > 0)
        {
            sb.AppendLine("Tools:");
            foreach (var call in evt.ToolCalls)
            {
                sb.Append("  - ").Append(call.Name).Append(' ').Append(call.ArgsSummary).Append(" [").Append(call.Status).AppendLine("]");
            }
        }

        return sb.ToString();
    }

    public static string BuildPermissionRequest(CodingPermissionRequestEvent evt)
    {
        var sb = new StringBuilder();
        sb.Append("[coding session=").Append(evt.SessionId)
            .Append(" status=awaiting-permission requestId=").Append(evt.RequestId).AppendLine("]");
        sb.Append("Tool: ").AppendLine(evt.ToolName);
        sb.Append("Input: ").AppendLine(evt.InputPreview);
        sb.AppendLine("Ask the user to allow_once, allow_always, or deny. Then call coding_session_respond with requestId and their answer.");
        return sb.ToString();
    }

    public static string BuildQuestion(CodingQuestionRequestEvent evt)
    {
        var sb = new StringBuilder();
        sb.Append("[coding session=").Append(evt.SessionId)
            .Append(" status=awaiting-question requestId=").Append(evt.RequestId).AppendLine("]");
        sb.Append("Question: ").AppendLine(evt.Question);
        if (evt.Options.Count > 0)
        {
            sb.Append("Options: ").AppendLine(string.Join(" | ", evt.Options));
        }

        sb.AppendLine("Relay this question to the user, then call coding_session_respond with requestId and their answer.");
        return sb.ToString();
    }

    public static string BuildPlanApproval(CodingPlanApprovalRequestEvent evt)
    {
        var sb = new StringBuilder();
        sb.Append("[coding session=").Append(evt.SessionId)
            .Append(" status=awaiting-plan requestId=").Append(evt.RequestId).AppendLine("]");
        sb.AppendLine("Plan:");
        sb.AppendLine(evt.Plan);
        sb.AppendLine("Relay a short summary to the user (post the full plan to a paired text channel if voice); then call coding_session_respond with requestId and 'approve' or 'reject'.");
        return sb.ToString();
    }

    public static string BuildStalled(CodingStalledEvent evt)
    {
        var sb = new StringBuilder();
        sb.Append("[coding session=").Append(evt.SessionId)
            .Append(" status=stalled idleSeconds=").Append(evt.IdleSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine("]");
        sb.Append("Message: ").AppendLine(evt.Message);
        if (!string.IsNullOrEmpty(evt.StderrTail))
        {
            sb.AppendLine("Stderr (tail):");
            sb.AppendLine(evt.StderrTail);
        }

        sb.AppendLine(
            "Coda went unresponsive mid-turn and was terminated (a stall, not a logic error). Tell the " +
            "user it stalled and offer to resume it (coding_session_resume) or end it. Do NOT immediately " +
            "resend the same instruction or start a duplicate session — check coding_session_status first.");
        return sb.ToString();
    }

    public static string BuildLimitReached(CodingLimitReachedEvent evt)
    {
        var sb = new StringBuilder();
        sb.Append("[coding session=").Append(evt.SessionId)
            .Append(" status=limit-reached kind=").Append(evt.Kind).AppendLine("]");
        sb.Append("Message: ").AppendLine(evt.Message);
        sb.Append("Coda ended this turn early on a recoverable limit (NOT a crash; kind=").Append(evt.Kind)
            .AppendLine("). The turn is finishing; once the session returns to idle, continue the unfinished");
        sb.AppendLine("work by sending the SAME session another message (e.g. \"continue\"). Do NOT start a duplicate session.");
        return sb.ToString();
    }

    public static string BuildError(CodingErrorEvent evt)
    {
        var sb = new StringBuilder();
        sb.Append("[coding session=").Append(evt.SessionId)
            .Append(" status=crashed");
        if (evt.ExitCode is { } code)
        {
            sb.Append(" exitCode=").Append(code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        sb.AppendLine("]");
        sb.Append("Message: ").AppendLine(evt.Message);
        if (!string.IsNullOrEmpty(evt.StderrTail))
        {
            sb.AppendLine("Stderr (tail):");
            sb.AppendLine(evt.StderrTail);
        }

        return sb.ToString();
    }
}
