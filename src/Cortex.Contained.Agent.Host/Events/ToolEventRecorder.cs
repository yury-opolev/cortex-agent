using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>Records <c>tool_call</c> and <c>tool_result</c> events around tool execution.</summary>
public interface IToolEventRecorder
{
    /// <summary>Records the call before the tool runs; returns the event ID, or null when recording failed.</summary>
    Task<string?> RecordCallAsync(LlmToolCall toolCall, CancellationToken cancellationToken);

    /// <summary>Records the result (caused by <paramref name="callEventId"/>); returns the evidence ID, or null when recording failed.</summary>
    Task<string?> RecordResultAsync(LlmToolCall toolCall, string? callEventId, AgentToolResult result, string? spillFile, CancellationToken cancellationToken);
}

/// <summary>
/// Writes tool events attributed to the current <see cref="RunScope"/>. Best effort: a failed
/// append is logged and never fails the tool call (the delivery report then states that evidence
/// is unavailable).
/// </summary>
public sealed partial class ToolEventRecorder : IToolEventRecorder
{
    private readonly IEventLog log;
    private readonly ToolEventPayloads payloads;
    private readonly ILogger<ToolEventRecorder> logger;
    private readonly IStorageState? storage;

    public ToolEventRecorder(IEventLog log, ToolEventPayloads payloads, ILogger<ToolEventRecorder> logger, IStorageState? storage = null)
    {
        this.log = log;
        this.payloads = payloads;
        this.logger = logger;
        this.storage = storage;
    }

    /// <inheritdoc />
    public Task<string?> RecordCallAsync(LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        var run = RunScope.Current;
        var draft = EventDraft.ForRun(EventTypes.ToolCall, run) with
        {
            Payload = this.payloads.ForCall(toolCall.Name, toolCall.Id, toolCall.Arguments, run?.ChannelId is not null),
        };
        return this.AppendAsync(draft, toolCall.Name, cancellationToken);
    }

    /// <inheritdoc />
    public Task<string?> RecordResultAsync(LlmToolCall toolCall, string? callEventId, AgentToolResult result, string? spillFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(result);
        var run = RunScope.Current;
        var draft = EventDraft.ForRun(EventTypes.ToolResult, run) with
        {
            CausationId = callEventId,
            Payload = this.payloads.ForResult(toolCall.Name, toolCall.Id, result, spillFile, run?.ChannelId is not null),
        };
        return this.AppendAsync(draft, toolCall.Name, cancellationToken);
    }

    private async Task<string?> AppendAsync(EventDraft draft, string toolName, CancellationToken cancellationToken)
    {
        // Best effort: never make a tool call wait for a storage key that has not arrived.
        if (this.storage is { CanServeWithoutWaiting: false })
        {
            this.LogRecordSkipped(draft.Type, toolName);
            return null;
        }

        try
        {
            return await this.log.AppendAsync(draft, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogRecordFailed(draft.Type, toolName, ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped recording {EventType} for tool {ToolName}: storage is not open")]
    private partial void LogRecordSkipped(string eventType, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record {EventType} for tool {ToolName}: {Error}")]
    private partial void LogRecordFailed(string eventType, string toolName, string error);
}
