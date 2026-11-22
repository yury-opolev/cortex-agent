using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>Tool execution records call → result evidence attributed to the current run.</summary>
public sealed class ToolRegistryEventTests : IAsyncDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"tool-events-{Guid.NewGuid():N}");
    private readonly MessageStore store;
    private readonly KnownSecrets secrets = new();

    public ToolRegistryEventTests()
    {
        this.store = new MessageStore(Path.Combine(this.tempDir, "messages.db"), NullLogger<MessageStore>.Instance);
        this.secrets.Load(new LlmCredentials { Providers = [] });
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static IAgentTool Tool(string name, AgentToolResult result)
    {
        var tool = Substitute.For<IAgentTool>();
        tool.Name.Returns(name);
        tool.ExecuteAsync(Arg.Any<string>(), Arg.Any<ToolExecutionContext>(), Arg.Any<CancellationToken>()).Returns(result);
        return tool;
    }

    private ToolRegistry Registry(IAgentTool tool, IEventLog? log = null) =>
        new(
            [tool],
            new ActiveChannelStore(),
            NullLogger<ToolRegistry>.Instance,
            eventRecorder: new ToolEventRecorder(log ?? this.store, new ToolEventPayloads(this.secrets), NullLogger<ToolEventRecorder>.Instance));

    private static ToolExecutionContext Context() => new() { ConversationId = "ch", ChannelId = "ch" };

    [Fact]
    public async Task ExecuteAsync_InRun_RecordsCallThenResultWithEvidenceId()
    {
        var registry = this.Registry(Tool("date_time", AgentToolResult.Ok("2026-11-21 09:00")));
        var run = new RunProvenance("run_t", AuthorKinds.ScheduledTask, "task1", "ch", "scheduled task task1");

        AgentToolResult result;
        using (RunScope.Begin(run))
        {
            result = await registry.ExecuteAsync(new LlmToolCall { Id = "c1", Name = "date_time", Arguments = "{}" }, Context(), CancellationToken.None);
        }

        var events = await this.store.ListRunAsync("run_t", CancellationToken.None);
        Assert.Equal([EventTypes.ToolCall, EventTypes.ToolResult], events.Select(e => e.Type));
        Assert.Equal(events[1].Id, result.EvidenceId);
        Assert.Equal(events[0].Id, events[1].CausationId);
        Assert.Equal("2026-11-21 09:00", (string?)events[1].Payload["digest"]);
        Assert.All(events, e => Assert.Equal(AuthorKinds.ScheduledTask, e.AuthorKind));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_StillRecordsFailedResult()
    {
        var registry = this.Registry(Tool("date_time", AgentToolResult.Ok("x")));

        AgentToolResult result;
        using (RunScope.Begin(new RunProvenance("run_u", AuthorKinds.Main, null, "ch", null)))
        {
            result = await registry.ExecuteAsync(new LlmToolCall { Id = "c1", Name = "nope", Arguments = "{}" }, Context(), CancellationToken.None);
        }

        var resultEvent = (await this.store.ListRunAsync("run_u", CancellationToken.None))[^1];
        Assert.False(result.Success);
        Assert.Equal("unknown_tool", (string?)resultEvent.Payload["errorClass"]);
    }

    [Fact]
    public async Task ExecuteAsync_RecorderFails_ToolResultStillReturned()
    {
        var failing = Substitute.For<IEventLog>();
        failing.AppendAsync(Arg.Any<EventDraft>(), Arg.Any<CancellationToken>()).Returns<string>(_ => throw new InvalidOperationException("disk full"));
        var registry = this.Registry(Tool("date_time", AgentToolResult.Ok("ok")), failing);

        var result = await registry.ExecuteAsync(new LlmToolCall { Id = "c1", Name = "date_time", Arguments = "{}" }, Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.EvidenceId);
    }
}
