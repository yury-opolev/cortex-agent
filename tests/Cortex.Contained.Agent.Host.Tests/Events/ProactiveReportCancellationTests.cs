using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>A cancelled generation must not drop a message that was already sent from the target conversation.</summary>
public sealed class ProactiveReportCancellationTests : IAsyncLifetime
{
    private const string Delivered = "Rain at five, bring a coat.";

    private readonly AgentSessionStore sessions;
    private readonly AgentRuntime runtime;

    public ProactiveReportCancellationTests()
    {
        var sessionConfig = new SessionConfig();
        this.sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);

        var sendTool = Substitute.For<IAgentTool>();
        sendTool.Name.Returns("send_message");
        sendTool.Description.Returns("Sends a message");
        sendTool.ParametersSchema.Returns("""{"type":"object","properties":{}}""");
        sendTool.ExecuteAsync(Arg.Any<string>(), Arg.Any<ToolExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.Arg<ToolExecutionContext>().ProactiveMessages.Add(
                    new ProactiveMessageRecord { ChannelId = "discord-dm", Text = Delivered, OperationId = "op_1" });
                return AgentToolResult.Ok("sent");
            });

        var llm = Substitute.For<ILlmClient>();
        var round = 0;
        llm.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++round == 1 ? ToolCallStream() : TextStream("sent"));

        var activeChannelStore = new ActiveChannelStore();
        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        var bridgeAccessor = new BridgeClientAccessor(hubContext);
        var caller = Substitute.For<IAgentHubClient>();
        hubClients.Client(Arg.Any<string>()).Returns(caller);
        bridgeAccessor.SetConnectionId("test-conn");
        caller.OnStatusChanged(Arg.Any<AgentStatusInfo>()).Returns(Task.CompletedTask);
        caller.OnResponseChunk(Arg.Any<ResponseChunkMessage>()).Returns(Task.CompletedTask);
        caller.OnToolExecution(Arg.Any<ToolExecutionMessage>()).Returns(Task.CompletedTask);
        caller.OnResponseComplete(Arg.Any<ResponseCompleteMessage>()).Returns(_ =>
        {
            // The generation is cancelled right after the final answer is delivered.
            this.runtime!.AbortGenerationAsync("conv-cancel");
            return Task.CompletedTask;
        });

        var log = Substitute.For<IEventLog>();
        log.ListOperationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<EventRecord>>([]);
        });

        var imageAging = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAging.CurrentValue.Returns(new ImageAgingConfig());
        this.runtime = new AgentRuntime(
            this.sessions,
            llm,
            new ToolRegistry([sendTool], activeChannelStore, NullLogger<ToolRegistry>.Instance),
            sessionConfig,
            new AgentMessageChannel(),
            bridgeAccessor,
            activeChannelStore,
            Substitute.For<IHttpClientFactory>(),
            Path.GetTempPath(),
            Path.GetTempPath(),
            NullLogger<AgentRuntime>.Instance,
            new ModelProvider(),
            imageAging,
            deliveryReports: new DeliveryReportBuilder(log, NullLogger<DeliveryReportBuilder>.Instance));
    }

    public Task InitializeAsync() => this.runtime.StartProcessingAsync(CancellationToken.None);

    public Task DisposeAsync() => this.runtime.StopProcessingAsync(CancellationToken.None);

    [Fact]
    public async Task Turn_CancelledAfterSend_StillInjectsTheDeliveredText()
    {
        await this.runtime.HandleMessageAsync(
            new HubInboundMessage
            {
                ConversationId = "conv-cancel",
                ChannelId = "webchat-default",
                SenderIdHash = "hash",
                Text = "tell me the weather on discord",
                Timestamp = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && !this.sessions.GetAll().SelectMany(s => s.GetHistory()).Any(m => m.Content?.Contains(Delivered, StringComparison.Ordinal) == true && m.Role == "assistant" && m.MessageType == LlmMessageType.Proactive))
        {
            await Task.Delay(25);
        }

        Assert.Contains(
            this.sessions.GetAll().SelectMany(s => s.GetHistory()),
            m => m.MessageType == LlmMessageType.ProactiveDeliveryTrace);
        Assert.Contains(
            this.sessions.GetAll().SelectMany(s => s.GetHistory()),
            m => m.MessageType == LlmMessageType.Proactive && m.Content!.Contains(Delivered, StringComparison.Ordinal));
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ToolCallStream()
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ToolCallDeltas = [new LlmToolCallDelta { Index = 0, Id = "call_1", Name = "send_message", ArgumentsDelta = "{}" }] };
        yield return new LlmStreamChunk { IsComplete = true, FinishReason = "tool_calls" };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> TextStream(string content)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ContentDelta = content,
            IsComplete = true,
            FinishReason = "stop",
            Usage = new LlmTokenUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 },
        };
    }
}
