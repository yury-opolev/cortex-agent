using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>The delivery report is rendered from the events of the send and its run.</summary>
public sealed class DeliveryReportBuilderTests : IAsyncDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"delivery-report-{Guid.NewGuid():N}");
    private readonly MessageStore store;
    private readonly DeliveryRecorder recorder;
    private readonly DeliveryReportBuilder builder;

    public DeliveryReportBuilderTests()
    {
        this.store = new MessageStore(Path.Combine(this.tempDir, "messages.db"), NullLogger<MessageStore>.Instance);
        this.recorder = new DeliveryRecorder(this.store, TimeProvider.System, NullLogger<DeliveryRecorder>.Instance);
        this.builder = new DeliveryReportBuilder(this.store, NullLogger<DeliveryReportBuilder>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task BuildAsync_RecordedSend_CarriesProducerStateAndEvidenceDigests()
    {
        var run = new RunProvenance("run_r", AuthorKinds.ScheduledTask, "t1", "ops", "scheduled task t1 (\"weather\")");
        DeliveryAttempt attempt;
        string evidenceId;
        using (RunScope.Begin(run))
        {
            evidenceId = await this.store.AppendAsync(
                EventDraft.ForRun(EventTypes.ToolResult, run) with { Payload = new JsonObject { ["tool"] = "date_time", ["ok"] = true, ["digest"] = "Friday" } },
                CancellationToken.None);
            attempt = await this.recorder.AttemptAsync("discord-dm", "proactive", CancellationToken.None);
            await this.recorder.AcknowledgedAsync(attempt, "discord-dm", delivered: true, messageRowId: 5, CancellationToken.None);
        }

        var report = await this.builder.BuildAsync(
            new ProactiveMessageRecord { ChannelId = "discord-dm", Text = "x", OperationId = attempt.OperationId },
            run,
            CancellationToken.None);

        Assert.Equal("acknowledged", report.State);
        Assert.Equal(AuthorKinds.ScheduledTask, report.ProducerKind);
        Assert.Equal("scheduled task t1 (\"weather\")", report.ProducerLabel);
        Assert.Equal("run_r", report.RunId);
        var evidence = Assert.Single(report.Evidence);
        Assert.Equal(new DeliveryEvidence(evidenceId, "date_time", true, "Friday"), evidence);
        Assert.Contains("\"Friday\"", DeliveryReportRenderer.Render(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_NoTerminalEvent_StateIsUnknown()
    {
        var attempt = await this.recorder.AttemptAsync("discord-dm", "proactive", CancellationToken.None);

        var report = await this.builder.BuildAsync(
            new ProactiveMessageRecord { ChannelId = "discord-dm", Text = "x", OperationId = attempt.OperationId },
            null,
            CancellationToken.None);

        Assert.Equal("unknown", report.State);
    }

    [Fact]
    public async Task BuildAsync_NoOperation_FallsBackToUnrecordedReport()
    {
        var run = new RunProvenance("run_t", AuthorKinds.Timer, null, "ch", "timer 7c4008bc");

        var report = await this.builder.BuildAsync(new ProactiveMessageRecord { ChannelId = "ch", Text = "x" }, run, CancellationToken.None);

        Assert.False(report.EvidenceAvailable);
        Assert.Contains("timer 7c4008bc", DeliveryReportRenderer.Render(report), StringComparison.Ordinal);
        Assert.Contains("not recorded", DeliveryReportRenderer.Render(report), StringComparison.Ordinal);
    }
}
