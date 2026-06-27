using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

public sealed class TurnResponseDeliveryTests : IAsyncDisposable
{
    private readonly string tempDir;
    private readonly MessageStore store;
    private readonly IAgentHubClient client = Substitute.For<IAgentHubClient>();

    public TurnResponseDeliveryTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"delivery-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.store = new MessageStore(Path.Combine(this.tempDir, "messages.db"), NullLogger<MessageStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private TurnResponseDelivery NewDelivery(bool proactive = false, MessageStore? store = null, bool noStore = false) =>
        new(this.client, noStore ? NullMessageStore.Instance : (IMessageStore)(store ?? this.store), "reply-conv", "chan-1", "corr-1", proactive, NullLogger.Instance);

    [Fact]
    public async Task StreamChunkAsync_StreamingMode_EmitsChunk()
    {
        var delivery = this.NewDelivery(proactive: false);

        await delivery.StreamChunkAsync("hello", 3);

        await this.client.Received(1).OnResponseChunk(Arg.Is<ResponseChunkMessage>(
            m => m.Text == "hello" && m.SequenceNumber == 3 && !m.IsComplete && m.ConversationId == "reply-conv"));
    }

    [Fact]
    public async Task StreamChunkAsync_ProactiveMode_DoesNotEmit()
    {
        var delivery = this.NewDelivery(proactive: true);

        await delivery.StreamChunkAsync("hello", 0);

        await this.client.DidNotReceive().OnResponseChunk(Arg.Any<ResponseChunkMessage>());
    }

    [Fact]
    public async Task PersistPreToolTextAsync_WithText_PersistsAndReturnsId()
    {
        var delivery = this.NewDelivery();

        var id = await delivery.PersistPreToolTextAsync("pre-tool text", CancellationToken.None);

        Assert.NotNull(id);
        var saved = await this.store.GetMessagesAsync("chan-1");
        Assert.Contains(saved, m => m.Content == "pre-tool text" && m.Role == "assistant");
    }

    [Fact]
    public async Task PersistPreToolTextAsync_EmptyContent_ReturnsNull()
    {
        var withStore = this.NewDelivery();
        Assert.Null(await withStore.PersistPreToolTextAsync("", CancellationToken.None));
    }

    [Fact]
    public async Task PersistPreToolTextAsync_NoStore_ReturnsZeroRecordId()
    {
        // The no-op NullMessageStore persists nothing; SaveMessageAsync returns row id 0,
        // which the tool-call attributor treats as "no real record".
        var noStore = this.NewDelivery(noStore: true);
        Assert.Equal(0L, await noStore.PersistPreToolTextAsync("text", CancellationToken.None));
    }

    [Fact]
    public async Task NotifyTool_EmitsStartedThenCompletedWithStatus()
    {
        var delivery = this.NewDelivery();

        await delivery.NotifyToolStartedAsync("file_read", "{}");
        await delivery.NotifyToolCompletedAsync("file_read", "{}", "output", success: true, TimeSpan.FromMilliseconds(5));

        await this.client.Received(1).OnToolExecution(Arg.Is<ToolExecutionMessage>(
            m => m.ToolName == "file_read" && m.Status == ToolExecutionStatus.Started && m.ConversationId == "reply-conv"));
        await this.client.Received(1).OnToolExecution(Arg.Is<ToolExecutionMessage>(
            m => m.ToolName == "file_read" && m.Status == ToolExecutionStatus.Completed && m.Output == "output" && m.ConversationId == "reply-conv"));
    }

    [Fact]
    public async Task NotifyToolCompletedAsync_Failure_EmitsFailedStatus()
    {
        var delivery = this.NewDelivery();

        await delivery.NotifyToolCompletedAsync("run", "{}", "boom", success: false, TimeSpan.Zero);

        await this.client.Received(1).OnToolExecution(Arg.Is<ToolExecutionMessage>(
            m => m.Status == ToolExecutionStatus.Failed && m.Output == "boom"));
    }

    [Fact]
    public async Task PersistLlmErrorAsync_EmitsCleanMessageAndPersists_NoRawHtml()
    {
        var delivery = this.NewDelivery();
        var raw = "HTTP 502: <!DOCTYPE html><html><title>Unicorn! · GitHub</title></html>";
        var expected = Cortex.Contained.Agent.Host.Llm.LlmErrorPresenter.ToUserMessage(raw);

        await delivery.PersistLlmErrorAsync(raw, CancellationToken.None);

        await this.client.Received(1).OnError(Arg.Is<AgentErrorMessage>(e => e.Message == expected));
        var saved = await this.store.GetMessagesAsync("chan-1");
        Assert.Contains(saved, m => m.Content == $"LLM Error: {expected}");
        // The raw HTML body must never reach the channel/history.
        Assert.DoesNotContain(saved, m => m.Content.Contains("<html", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PersistLlmErrorAsync_NoStore_EmitsCleanErrorOnly()
    {
        var delivery = this.NewDelivery(noStore: true);
        var expected = Cortex.Contained.Agent.Host.Llm.LlmErrorPresenter.ToUserMessage("x");

        await delivery.PersistLlmErrorAsync("x", CancellationToken.None);

        await this.client.Received(1).OnError(Arg.Is<AgentErrorMessage>(e => e.Message == expected));
    }

    [Fact]
    public async Task DeliverDoomLoopAsync_EmitsCompleteChunkAndPersists()
    {
        var delivery = this.NewDelivery();

        await delivery.DeliverDoomLoopAsync("stuck in a loop", CancellationToken.None);

        await this.client.Received(1).OnResponseChunk(Arg.Is<ResponseChunkMessage>(m => m.IsComplete && m.Text == "stuck in a loop"));
        await this.client.Received(1).OnResponseComplete(Arg.Is<ResponseCompleteMessage>(m => m.FullText == "stuck in a loop"));
        var saved = await this.store.GetMessagesAsync("chan-1");
        Assert.Contains(saved, m => m.Content == "stuck in a loop");
    }

    [Fact]
    public async Task Attribution_ToolOnlyRoundThenTextRecord_PatchesTextRecord()
    {
        var delivery = this.NewDelivery();
        // tool-only round (recordId null) buffers entries as "before"
        delivery.RecordRoundTools(null, [new ToolCallSummaryEntry("grep", "{}", true, "after")]);
        // a later text record gets the buffered "before" entry attributed to it
        var textRecordId = await this.store.SaveMessageAsync("assistant", "chan-1", "assistant", "answer", DateTimeOffset.UtcNow);
        delivery.RecordRoundTools(textRecordId, []);

        await delivery.FlushAttributionPatchesAsync(CancellationToken.None);

        var saved = await this.store.GetMessagesAsync("chan-1");
        var rec = Assert.Single(saved, m => m.Content == "answer");
        Assert.False(string.IsNullOrEmpty(rec.ToolCalls));
        Assert.Contains("grep", rec.ToolCalls);
    }

    [Fact]
    public async Task DeliverFinalResponse_StreamingMode_CompletesAndPersists()
    {
        var delivery = this.NewDelivery(proactive: false);
        var session = new AgentSession("conv-x");

        await delivery.DeliverFinalResponseAsync(session, "final answer", instructionText: null, usage: null, sequenceNumber: 7, messageId: "mid-1", CancellationToken.None);

        await this.client.Received(1).OnResponseChunk(Arg.Is<ResponseChunkMessage>(m => m.IsComplete && m.SequenceNumber == 7 && m.Text == string.Empty));
        await this.client.Received(1).OnResponseComplete(Arg.Is<ResponseCompleteMessage>(m => m.MessageId == "mid-1" && m.FullText == "final answer"));
        await this.client.DidNotReceive().OnScheduledTaskComplete(Arg.Any<ScheduledTaskCompleteMessage>());
        var saved = await this.store.GetMessagesAsync("chan-1");
        Assert.Contains(saved, m => m.Content == "final answer" && m.Role == "assistant");
    }

    [Fact]
    public async Task DeliverFinalResponse_ProactiveMode_UsesScheduledTaskCompleteNoStreaming()
    {
        var delivery = this.NewDelivery(proactive: true);
        var session = new AgentSession("conv-x");

        await delivery.DeliverFinalResponseAsync(session, "task result", instructionText: "do the thing", usage: null, sequenceNumber: 0, messageId: "mid-2", CancellationToken.None);

        await this.client.Received(1).OnScheduledTaskComplete(Arg.Is<ScheduledTaskCompleteMessage>(
            m => m.ResponseText == "task result" && m.InstructionText == "do the thing"));
        await this.client.DidNotReceive().OnResponseComplete(Arg.Any<ResponseCompleteMessage>());
        var saved = await this.store.GetMessagesAsync("chan-1");
        Assert.Contains(saved, m => m.Content == "task result");
    }

    [Fact]
    public async Task DeliverFinalResponse_BargeInBeforeSave_PersistsSpokenTextAndClears()
    {
        var delivery = this.NewDelivery(proactive: false);
        var session = new AgentSession("conv-x");
        session.MarkInterrupted("partial spoken…");

        await delivery.DeliverFinalResponseAsync(session, "the full unspoken answer", instructionText: null, usage: null, sequenceNumber: 0, messageId: "mid-3", CancellationToken.None);

        var saved = await this.store.GetMessagesAsync("chan-1");
        // The persisted content is the spoken (interrupted) text, not the full text.
        Assert.Contains(saved, m => m.Content == "partial spoken…");
        Assert.DoesNotContain(saved, m => m.Content == "the full unspoken answer");
        Assert.Null(session.InterruptedPlayedText); // cleared
    }
}
