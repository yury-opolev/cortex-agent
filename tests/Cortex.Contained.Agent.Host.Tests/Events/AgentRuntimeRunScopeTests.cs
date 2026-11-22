using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>Each processed message runs inside its own <see cref="RunScope"/>, so its writes carry the run.</summary>
public sealed class AgentRuntimeRunScopeTests : IAsyncLifetime
{
    private readonly AgentRuntime runtime;
    private readonly AgentMessageChannel messageChannel = new();
    private readonly IMessageStore messageStore = Substitute.For<IMessageStore>();
    private readonly List<RunProvenance?> runsAtSave = [];

    public AgentRuntimeRunScopeTests()
    {
        var sessionConfig = new SessionConfig();
        var sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        var activeChannelStore = new ActiveChannelStore();
        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        hubClients.Client(Arg.Any<string>()).Returns(Substitute.For<IAgentHubClient>());
        var bridgeAccessor = new BridgeClientAccessor(hubContext);
        bridgeAccessor.SetConnectionId("test-conn");
        var imageAging = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAging.CurrentValue.Returns(new ImageAgingConfig());

        var llm = Substitute.For<ILlmClient>();
        llm.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Reply("done"));

        this.messageStore
            .SaveMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<string?>(), Arg.Any<MessageCategory>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                lock (this.runsAtSave)
                {
                    this.runsAtSave.Add(RunScope.Current);
                }

                return 1L;
            });

        this.runtime = new AgentRuntime(
            sessions, llm, new ToolRegistry([], activeChannelStore, NullLogger<ToolRegistry>.Instance), sessionConfig, this.messageChannel,
            bridgeAccessor, activeChannelStore, Substitute.For<IHttpClientFactory>(),
            Path.GetTempPath(), Path.GetTempPath(), NullLogger<AgentRuntime>.Instance,
            new ModelProvider(), imageAging, messageStore: this.messageStore);
    }

    public Task InitializeAsync() => this.runtime.StartProcessingAsync(CancellationToken.None);

    public async Task DisposeAsync() => await this.runtime.StopProcessingAsync(CancellationToken.None);

    private static async IAsyncEnumerable<LlmStreamChunk> Reply(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ContentDelta = content, IsComplete = true, FinishReason = "stop" };
    }

    [Fact]
    public async Task ScheduledTaskMessage_SavesInsideScheduledTaskRun()
    {
        Assert.True(this.messageChannel.TryEnqueue(new AgentMessage
        {
            ConversationId = "scheduled-1bffa504",
            ChannelId = "discord-dm",
            Text = "Send the morning brief.",
            Source = AgentMessageSource.ScheduledTask,
            TriggerLabel = "scheduled task 1bffa504 (\"brief\")",
        }));

        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline && this.Saved().Count < 2)
        {
            await Task.Delay(25);
        }

        var saved = this.Saved();
        Assert.True(saved.Count >= 2, "the instruction and the reply were not both saved");
        Assert.All(saved, run =>
        {
            Assert.NotNull(run);
            Assert.Equal(AuthorKinds.ScheduledTask, run.AuthorKind);
            Assert.Equal("1bffa504", run.AuthorId);
        });
        Assert.Single(saved.Select(r => r!.RunId).Distinct());
    }

    private List<RunProvenance?> Saved()
    {
        lock (this.runsAtSave)
        {
            return [.. this.runsAtSave];
        }
    }
}
