using System.Text.Json;
using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;

/// <summary>
/// Common helpers shared by the <c>coding_*</c> tools.
/// </summary>
internal static class CodingToolBase
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    internal static AgentToolResult Error(string code, string message)
    {
        var payload = JsonSerializer.Serialize(new { error = code, message }, JsonOptions);
        return new AgentToolResult { Success = false, Content = payload, Error = message };
    }

    /// <summary>
    /// Maps an exception thrown while invoking the Bridge into a tool result. A
    /// <see cref="CodingInvokeException"/> carries a stable, state-bearing code
    /// (e.g. <c>coda_start_failed</c> / <c>coda_unreachable</c> / <c>coda_timeout</c>) that must
    /// reach the LLM verbatim; anything else is an <c>internal_error</c>. Every coding tool routes
    /// its catch-all through here so the specific code is never downgraded.
    /// </summary>
    internal static AgentToolResult FromException(Exception ex) => ex switch
    {
        CodingInvokeException cie => Error(cie.Code, cie.Message),
        _ => Error("internal_error", ex.Message),
    };

    internal static AgentToolResult Ok(object payload)
    {
        return AgentToolResult.Ok(JsonSerializer.Serialize(payload, JsonOptions));
    }

    internal static string? ResolveChannelId(ToolExecutionContext context, JsonElement root)
    {
        return !string.IsNullOrEmpty(context.ChannelId) ? context.ChannelId : null;
    }

    /// <summary>
    /// Resolves the session id for an action tool. If <paramref name="explicitSessionId"/> is
    /// provided it is used as-is. Otherwise the channel's active sessions are consulted:
    /// zero → <c>no_active_session</c>; exactly one → that session; more than one →
    /// <c>ambiguous_session</c> (the caller must specify which).
    /// </summary>
    internal static (string? SessionId, AgentToolResult? Error) ResolveSessionId(
        CodingAgentSessionStore store, string channelId, string? explicitSessionId)
    {
        if (!string.IsNullOrWhiteSpace(explicitSessionId))
        {
            return (explicitSessionId, null);
        }

        var active = store.ListActiveByChannel(channelId);
        if (active.Count == 0)
        {
            return (null, Error(CodingBridgeErrorCodes.NoActiveSession, "No active session in this channel."));
        }

        if (active.Count == 1)
        {
            return (active[0].SessionId, null);
        }

        var list = string.Join(", ", active.Select(r =>
            r.SessionName is { Length: > 0 } name ? $"{r.SessionId} ({name})" : r.SessionId));
        return (null, Error(
            CodingBridgeErrorCodes.AmbiguousSession,
            $"This channel has {active.Count} active coding sessions; specify sessionId. Active: {list}"));
    }

    internal static object SnapshotPayload(CodingStatus status)
    {
        return new
        {
            sessionId = status.SessionId,
            channelId = status.ChannelId,
            workingFolder = status.WorkingFolder,
            state = status.State.ToString(),
            policy = status.Policy.ToString(),
            sessionName = status.SessionName,
            createdAt = status.CreatedAt,
            lastActivityAt = status.LastActivityAt,
            currentTaskId = status.CurrentTaskId,
            lastUserMessage = status.LastUserMessage,
            lastAssistantSummary = status.LastAssistantSummary,
            telemetryLogPath = status.TelemetryLogPath,
            lastError = status.LastError,
            inputTokens = status.InputTokens,
            outputTokens = status.OutputTokens,
            isStreaming = status.IsStreaming,
            streamedChars = status.StreamedChars,
            streamedChunks = status.StreamedChunks,
            lastStreamActivityAt = status.LastStreamActivityAt,
            currentActivity = status.CurrentActivity,
            goalStatus = status.GoalStatus is null ? null : new
            {
                outcome = status.GoalStatus.Outcome,
                remaining = status.GoalStatus.Remaining,
                continuations = status.GoalStatus.Continuations,
                elapsedSeconds = status.GoalStatus.ElapsedSeconds,
                escalated = status.GoalStatus.Escalated,
                extensionUsed = status.GoalStatus.ExtensionUsed,
            },
            lastToolCalls = status.LastToolCalls.Select(c => new
            {
                name = c.Name,
                argsSummary = c.ArgsSummary,
                status = c.Status,
                timestampUtc = c.TimestampUtc,
            }),
        };
    }

    internal static CodingAgentSessionRecord ToRecord(CodingStatus status)
    {
        return new CodingAgentSessionRecord
        {
            SessionId = status.SessionId,
            ChannelId = status.ChannelId,
            WorkingFolder = status.WorkingFolder,
            Policy = status.Policy,
            SessionName = status.SessionName,
            State = status.State,
            CreatedAt = status.CreatedAt,
            LastActivityAt = status.LastActivityAt,
            LastUserMessage = status.LastUserMessage,
            LastAssistantSummary = status.LastAssistantSummary,
            LastToolCallsJson = status.LastToolCalls.Count > 0
                ? CodingAgentSessionStore.SerializeToolCalls(status.LastToolCalls)
                : null,
        };
    }
}
