using Cortex.Contained.Agent.Host.Storage;
using System.Text.Json.Nodes;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>One send operation in progress.</summary>
/// <param name="OperationId">The operation ID (<c>op_…</c>) shared by all of its events.</param>
/// <param name="AttemptedAt">When the send was attempted.</param>
public sealed record DeliveryAttempt(string OperationId, DateTimeOffset AttemptedAt);

/// <summary>
/// Records sends as attempted → acknowledged | unknown (§4.1). The attempt links the delivery to
/// its run and to the run's <c>tool_result</c> evidence so far (delivery → run → call → result).
/// Best effort: a failed append is logged and never blocks the send.
/// </summary>
public sealed partial class DeliveryRecorder
{
    /// <summary>Most evidence IDs linked from one attempt (newest kept).</summary>
    internal const int MaxEvidence = 50;

    private const int MaxProducerChars = 200;

    private readonly IEventLog log;
    private readonly TimeProvider time;
    private readonly ILogger<DeliveryRecorder> logger;
    private readonly IStorageState? storage;

    public DeliveryRecorder(IEventLog log, TimeProvider time, ILogger<DeliveryRecorder> logger, IStorageState? storage = null)
    {
        this.log = log;
        this.time = time;
        this.logger = logger;
        this.storage = storage;
    }

    /// <summary>Records <c>delivery_attempted</c> to <paramref name="channelId"/>; call before the send.</summary>
    /// <param name="channelId">The target channel.</param>
    /// <param name="via"><c>proactive</c> (send_message, transfer greeting) or <c>reply</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DeliveryAttempt> AttemptAsync(string channelId, string via, CancellationToken cancellationToken)
    {
        var attempt = new DeliveryAttempt(EventIds.NewOperationId(), this.time.GetUtcNow());
        if (this.StorageNotReady(EventTypes.DeliveryAttempted, attempt.OperationId))
        {
            return attempt;
        }

        var run = RunScope.Current;
        try
        {
            var evidence = run is null
                ? []
                : (await this.log.ListRunAsync(run.RunId, cancellationToken).ConfigureAwait(false))
                    .Where(e => e.Type == EventTypes.ToolResult)
                    .Select(e => e.Id)
                    .TakeLast(MaxEvidence)
                    .ToList();
            var payload = new JsonObject
            {
                ["channel"] = channelId,
                ["via"] = via,
                ["evidence"] = new JsonArray([.. evidence.Select(id => (JsonNode?)JsonValue.Create(id))]),
            };
            if (run?.Label is { } label)
            {
                payload["producer"] = label.Length > MaxProducerChars ? label[..MaxProducerChars] : label;
            }

            await this.log.AppendAsync(
                EventDraft.ForRun(EventTypes.DeliveryAttempted, run) with
                {
                    ChannelId = channelId,
                    OperationId = attempt.OperationId,
                    IdempotencyKey = attempt.OperationId + ":attempted",
                    Ts = attempt.AttemptedAt,
                    Payload = payload,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogRecordFailed(EventTypes.DeliveryAttempted, attempt.OperationId, ex.Message);
        }

        return attempt;
    }

    /// <summary>Records <c>delivery_acknowledged</c>: the Bridge answered; <paramref name="delivered"/> says how.</summary>
    public Task AcknowledgedAsync(DeliveryAttempt attempt, string channelId, bool delivered, long? messageRowId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var payload = new JsonObject { ["delivered"] = delivered };
        if (messageRowId is { } rowId)
        {
            payload["messageRowId"] = rowId;
        }

        return this.AppendTerminalAsync(attempt, channelId, EventTypes.DeliveryAcknowledged, payload, cancellationToken);
    }

    /// <summary>Records <c>delivery_unknown</c>: the send failed in transport, so arrival is unknown.</summary>
    public Task UnknownAsync(DeliveryAttempt attempt, string channelId, string errorClass, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return this.AppendTerminalAsync(attempt, channelId, EventTypes.DeliveryUnknown, new JsonObject { ["errorClass"] = errorClass }, cancellationToken);
    }

    private async Task AppendTerminalAsync(DeliveryAttempt attempt, string channelId, string type, JsonObject payload, CancellationToken cancellationToken)
    {
        if (this.StorageNotReady(type, attempt.OperationId))
        {
            return;
        }

        try
        {
            await this.log.AppendAsync(
                EventDraft.ForRun(type, RunScope.Current) with
                {
                    ChannelId = channelId,
                    OperationId = attempt.OperationId,
                    IdempotencyKey = attempt.OperationId + ":" + type,
                    Ts = this.time.GetUtcNow(),
                    Payload = payload,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogRecordFailed(type, attempt.OperationId, ex.Message);
        }
    }

    // Best effort: a send never waits for a storage key that has not arrived.
    private bool StorageNotReady(string eventType, string operationId)
    {
        if (this.storage is not { CanServeWithoutWaiting: false })
        {
            return false;
        }

        this.LogRecordSkipped(eventType, operationId);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped recording {EventType} for send {OperationId}: storage is not open")]
    private partial void LogRecordSkipped(string eventType, string operationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record {EventType} for send {OperationId}: {Error}")]
    private partial void LogRecordFailed(string eventType, string operationId, string error);
}
