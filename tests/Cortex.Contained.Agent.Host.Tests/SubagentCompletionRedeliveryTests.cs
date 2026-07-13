using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Proves the loop-back edge of the at-least-once completion-delivery protocol end to end:
/// <see cref="SubagentExecutionCoordinator"/> claims a Pending notification and enqueues it,
/// <see cref="AgentRuntime"/> fails the first parent turn (transient LLM error) and releases
/// the claim back to Pending — and the release WAKES the coordinator, so the completion is
/// redelivered on the next pass and ends Delivered instead of sitting Pending until an
/// unrelated wake or a restart.
/// </summary>
public sealed class SubagentCompletionRedeliveryTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly SubagentSessionStore _subagentStore;
    private readonly AgentMessageChannel _messageChannel;
    private readonly SubagentExecutionCoordinator _coordinator;
    private readonly IAgentHubClient _mockCaller;
    private readonly ILlmClient _mockLlmClient;
    private readonly AgentRuntime _runtime;
    private int _llmCalls;

    public SubagentCompletionRedeliveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sub-redeliver-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _subagentStore = new SubagentSessionStore(_tempDir, NullLogger<SubagentSessionStore>.Instance);
        _messageChannel = new AgentMessageChannel();

        var registry = new SubagentRunnerRegistry(1, NullLogger<SubagentRunnerRegistry>.Instance);
        SubagentRunner RunnerFactory(SubagentTask _) => new(
            Substitute.For<ILlmClient>(),
            new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance),
            10,
            NullLogger<SubagentRunner>.Instance);

        _coordinator = new SubagentExecutionCoordinator(
            _subagentStore,
            registry,
            new StubExecutor(),
            RunnerFactory,
            _messageChannel,
            NullLogger<SubagentExecutionCoordinator>.Instance);

        var sessionConfig = new SessionConfig();
        var sessions = new AgentSessionStore(sessionConfig, new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        _mockLlmClient = Substitute.For<ILlmClient>();
        var activeChannelStore = new ActiveChannelStore();
        var toolRegistry = new ToolRegistry([], activeChannelStore, NullLogger<ToolRegistry>.Instance);

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        var bridgeAccessor = new BridgeClientAccessor(hubContext);

        _mockCaller = Substitute.For<IAgentHubClient>();
        hubClients.Client(Arg.Any<string>()).Returns(_mockCaller);
        bridgeAccessor.SetConnectionId("test-conn");

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var imageAgingMonitor = Substitute.For<IOptionsMonitor<ImageAgingConfig>>();
        imageAgingMonitor.CurrentValue.Returns(new ImageAgingConfig());

        _runtime = new AgentRuntime(
            sessions,
            _mockLlmClient,
            toolRegistry,
            sessionConfig,
            _messageChannel,
            bridgeAccessor,
            activeChannelStore,
            httpClientFactory,
            Path.GetTempPath(),
            _tempDir,
            NullLogger<AgentRuntime>.Instance,
            new ModelProvider(),
            imageAgingMonitor,
            subagentStore: _subagentStore,
            wakeSubagentCoordinator: _coordinator.SignalWorkAvailable);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _coordinator.StopAsync(CancellationToken.None);
        }
#pragma warning disable CA1031
        catch { /* best-effort teardown */ }
#pragma warning restore CA1031
        _coordinator.Dispose();
        await _runtime.StopProcessingAsync(CancellationToken.None);
        _subagentStore.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task SubagentCompletion_FirstDeliveryFails_IsRedeliveredAndEndsDelivered()
    {
        // First parent turn hits a transient LLM error; every later attempt succeeds.
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref _llmCalls) == 1
                ? ErrorStream("transient provider error")
                : SingleChunkStream("Task finished — result relayed."));

        // A completed subagent run whose durable notification is still Pending.
        _subagentStore.Create(new SubagentTask
        {
            TaskId = "sa-retry",
            ParentConversation = "conv-retry",
            ParentChannel = "conv-retry",
            Description = "background job",
            Prompt = "do it",
            State = SubagentTaskState.Completed,
            Result = "the result",
            CompletedAt = DateTimeOffset.UtcNow,
            NotificationState = SubagentNotificationState.Pending,
        });

        await _runtime.StartProcessingAsync(CancellationToken.None);
        await _coordinator.StartAsync(CancellationToken.None);

        // Full readiness opens the gate; this is the LAST external wake the coordinator
        // gets — redelivery after the failed first turn depends entirely on the release
        // waking the dispatch loop again.
        _coordinator.OnBridgeConnected();
        _coordinator.MarkCredentialsReady(true);
        _coordinator.MarkMcpCatalogReady();

        await WaitUntilAsync(() =>
            _subagentStore.GetById("sa-retry")!.NotificationState == SubagentNotificationState.Delivered);
        Assert.True(Volatile.Read(ref _llmCalls) >= 2, "expected at least one failed and one successful delivery attempt");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Condition was not satisfied within the timeout.");
            }

            await Task.Delay(15);
        }
    }

    private static async IAsyncEnumerable<LlmStreamChunk> SingleChunkStream(string content)
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

    private static async IAsyncEnumerable<LlmStreamChunk> ErrorStream(string errorMessage)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ErrorMessage = errorMessage };
    }

    /// <summary>Never invoked — no Queued tasks exist in these tests.</summary>
    private sealed class StubExecutor : ISubagentExecutor
    {
        public Task<SubagentExecutionResult> ExecuteAsync(SubagentTask task, CancellationToken cancellationToken)
            => Task.FromResult(new SubagentExecutionResult(SubagentTaskState.Completed, "done"));
    }
}
