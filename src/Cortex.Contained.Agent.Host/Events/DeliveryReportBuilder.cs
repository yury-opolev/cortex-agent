using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>Builds a <see cref="DeliveryReport"/> from the events of one send operation.</summary>
public sealed partial class DeliveryReportBuilder
{
    private readonly IEventLog log;
    private readonly ILogger<DeliveryReportBuilder> logger;

    public DeliveryReportBuilder(IEventLog log, ILogger<DeliveryReportBuilder> logger)
    {
        this.log = log;
        this.logger = logger;
    }

    /// <summary>
    /// The report for <paramref name="proactive"/>: from its operation's events when recorded,
    /// otherwise a minimal report that states the evidence is unavailable.
    /// </summary>
    public async Task<DeliveryReport> BuildAsync(ProactiveMessageRecord proactive, RunProvenance? run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proactive);
        if (proactive.OperationId is { } operationId)
        {
            try
            {
                var report = await this.FromEventsAsync(operationId, cancellationToken).ConfigureAwait(false);
                if (report is not null)
                {
                    return report;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.LogReadFailed(operationId, ex.Message);
            }
        }

        return Unrecorded(proactive, run);
    }

    /// <summary>The report when no events can back it.</summary>
    public static DeliveryReport Unrecorded(ProactiveMessageRecord proactive, RunProvenance? run)
    {
        ArgumentNullException.ThrowIfNull(proactive);
        return new DeliveryReport
        {
            ChannelId = proactive.ChannelId,
            AttemptedAt = proactive.DeliveredAt ?? DateTimeOffset.UtcNow,
            State = "acknowledged",
            ProducerKind = run?.AuthorKind ?? AuthorKinds.Main,
            ProducerId = run?.AuthorId,
            ProducerLabel = run?.Label,
            RunId = run?.RunId,
            OperationId = proactive.OperationId,
            EvidenceAvailable = false,
        };
    }

    private async Task<DeliveryReport?> FromEventsAsync(string operationId, CancellationToken cancellationToken)
    {
        var events = await this.log.ListOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        var attempted = events.FirstOrDefault(e => e.Type == EventTypes.DeliveryAttempted);
        if (attempted is null)
        {
            return null;
        }

        var acknowledged = events.FirstOrDefault(e => e.Type == EventTypes.DeliveryAcknowledged);
        var state = acknowledged is null
            ? "unknown"
            : (bool?)acknowledged.Payload["delivered"] == true ? "acknowledged" : "rejected";

        var evidence = new List<DeliveryEvidence>();
        foreach (var node in attempted.Payload["evidence"]?.AsArray() ?? [])
        {
            if ((string?)node is not { } id || await this.log.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } result)
            {
                continue;
            }

            evidence.Add(new DeliveryEvidence(
                result.Id,
                (string?)result.Payload["tool"] ?? "?",
                (bool?)result.Payload["ok"] == true,
                (string?)result.Payload["digest"]));
        }

        return new DeliveryReport
        {
            ChannelId = attempted.ChannelId ?? string.Empty,
            AttemptedAt = attempted.Ts,
            State = state,
            ProducerKind = attempted.AuthorKind,
            ProducerId = attempted.AuthorId,
            ProducerLabel = (string?)attempted.Payload["producer"],
            RunId = attempted.RunId,
            OperationId = operationId,
            DeliveryEventId = attempted.Id,
            Evidence = evidence,
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the events of send {OperationId}; the delivery report has no evidence: {Error}")]
    private partial void LogReadFailed(string operationId, string error);
}
