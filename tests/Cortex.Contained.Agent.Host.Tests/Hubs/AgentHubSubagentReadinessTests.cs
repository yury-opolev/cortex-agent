using System.Reflection;
using System.Runtime.CompilerServices;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Mcp;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Hubs;

/// <summary>
/// Proves the readiness wiring between <see cref="AgentHub"/> lifecycle events and
/// <see cref="SubagentExecutionCoordinator"/>: queued/recovered subagent work and durable
/// completion notifications only move once Bridge connection + credentials + MCP catalog
/// are all signaled by the hub — and a Bridge reconnect requires fresh pushes.
/// </summary>
public sealed class AgentHubSubagentReadinessTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hub-ready-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SubagentSessionStore _store;
    private readonly SubagentRunnerRegistry _registry;
    private readonly RecordingExecutor _executor = new();
    private readonly AgentMessageChannel _messageChannel = new();
    private readonly SubagentExecutionCoordinator _coordinator;
    private readonly DirectLlmClient _llmClient;
    private readonly AgentHub _hub;

    public AgentHubSubagentReadinessTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SubagentSessionStore(_dir, NullLogger<SubagentSessionStore>.Instance);
        _registry = new SubagentRunnerRegistry(2, NullLogger<SubagentRunnerRegistry>.Instance);

        SubagentRunner RunnerFactory(SubagentTask _) => new(
            Substitute.For<ILlmClient>(),
            new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance),
            10,
            NullLogger<SubagentRunner>.Instance);

        _coordinator = new SubagentExecutionCoordinator(
            _store,
            _registry,
            _executor,
            RunnerFactory,
            _messageChannel,
            NullLogger<SubagentExecutionCoordinator>.Instance);
        _coordinator.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        _llmClient = new DirectLlmClient(
            Substitute.For<IHttpClientFactory>(),
            NullLogger<DirectLlmClient>.Instance);

        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        var hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(hubClients);
        hubClients.Client(Arg.Any<string>()).Returns(Substitute.For<IAgentHubClient>());

        // The hub's readiness surface only needs these collaborators — bypass the full ctor.
        _hub = (AgentHub)RuntimeHelpers.GetUninitializedObject(typeof(AgentHub));
        SetPrivateField(_hub, "subagentCoordinator", _coordinator);
        SetPrivateField(_hub, "mcpToolStore", new McpToolStore(Substitute.For<IMcpGateway>()));
        SetPrivateField(_hub, "logger", NullLogger<AgentHub>.Instance);
        SetPrivateField(_hub, "bridgeClientAccessor", new BridgeClientAccessor(hubContext));
        SetPrivateField(_hub, "llmClient", _llmClient);
        SetPrivateField(_hub, "runtime", Substitute.For<IAgentRuntime>());
        _hub.Clients = Substitute.For<IHubCallerClients<IAgentHubClient>>();

        var callerContext = Substitute.For<HubCallerContext>();
        callerContext.ConnectionId.Returns("bridge-conn-1");
        _hub.Context = callerContext;
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
        _llmClient.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Gating: each missing signal blocks dispatch ──────────────────────

    [Fact]
    public async Task QueuedWork_BridgeDisconnected_DoesNotDispatch()
    {
        SeedQueued("sa-nobridge");
        SeedTerminalWithPendingNotification("sa-nobridge-note");

        // Credentials + catalog ready, but the Bridge never connected.
        _coordinator.MarkCredentialsReady(true);
        _coordinator.MarkMcpCatalogReady();
        _coordinator.SignalWorkAvailable();

        await AssertNeverAsync(() => _executor.CallCount > 0);
        Assert.Equal(SubagentTaskState.Queued, _store.GetById("sa-nobridge")!.State);

        // Completion notifications are equally gated: nothing may hit the message queue.
        Assert.False(_messageChannel.TryRead(out _));
    }

    [Fact]
    public async Task QueuedWork_CredentialsMissing_DoesNotDispatch()
    {
        SeedQueued("sa-nocreds");
        await _hub.OnConnectedAsync();
        await _hub.UpdateMcpToolCatalog(new McpToolCatalog { Tools = [] });

        // A push with zero providers applies, but HasCredentials stays false.
        await _hub.ProvideCredentials(new LlmCredentials { Providers = [] });
        _coordinator.SignalWorkAvailable();

        await AssertNeverAsync(() => _executor.CallCount > 0);

        await _hub.ProvideCredentials(OneProviderCredentials());
        await WaitUntilAsync(() => _executor.CallCount > 0);
    }

    [Fact]
    public async Task QueuedWork_McpCatalogMissing_DoesNotDispatch()
    {
        SeedQueued("sa-nomcp");
        await _hub.OnConnectedAsync();
        await _hub.ProvideCredentials(OneProviderCredentials());
        _coordinator.SignalWorkAvailable();

        await AssertNeverAsync(() => _executor.CallCount > 0);

        await _hub.UpdateMcpToolCatalog(new McpToolCatalog { Tools = [] });
        await WaitUntilAsync(() => _executor.CallCount > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadinessSignals_InEitherOrder_DispatchAfterAll(bool credentialsFirst)
    {
        SeedQueued("sa-order");
        await _hub.OnConnectedAsync();

        if (credentialsFirst)
        {
            await _hub.ProvideCredentials(OneProviderCredentials());
            await AssertNeverAsync(() => _executor.CallCount > 0);
            await _hub.UpdateMcpToolCatalog(new McpToolCatalog { Tools = [] });
        }
        else
        {
            await _hub.UpdateMcpToolCatalog(new McpToolCatalog { Tools = [] });
            await AssertNeverAsync(() => _executor.CallCount > 0);
            await _hub.ProvideCredentials(OneProviderCredentials());
        }

        await WaitUntilAsync(() => _executor.CallCount > 0);
        await WaitUntilAsync(() => _store.GetById("sa-order")!.State == SubagentTaskState.Completed);
    }

    // ── Reconnect semantics ──────────────────────────────────────────────

    [Fact]
    public async Task BridgeReconnect_RequiresFreshCredentialAndCatalogPush()
    {
        await ConnectAndMarkAllReadyAsync();
        SeedQueued("sa-first");
        _coordinator.SignalWorkAvailable();
        await WaitUntilAsync(() => _executor.CallCount == 1);

        await _hub.OnDisconnectedAsync(null);
        await _hub.OnConnectedAsync();

        // Stale readiness from the previous connection must NOT reopen the gate.
        SeedQueued("sa-second");
        _coordinator.SignalWorkAvailable();
        await AssertNeverAsync(() => _executor.CallCount > 1);

        await _hub.ProvideCredentials(OneProviderCredentials());
        await _hub.UpdateMcpToolCatalog(new McpToolCatalog { Tools = [] });
        await WaitUntilAsync(() => _executor.CallCount == 2);
    }

    [Fact]
    public async Task UpdateMcpToolCatalog_EmptyCatalog_MarksReady()
    {
        SeedQueued("sa-empty-catalog");
        await _hub.OnConnectedAsync();
        await _hub.ProvideCredentials(OneProviderCredentials());
        await AssertNeverAsync(() => _executor.CallCount > 0);

        // An EMPTY catalog is a fully-initialized state — it must open the gate.
        await _hub.UpdateMcpToolCatalog(new McpToolCatalog { Tools = [] });

        await WaitUntilAsync(() => _executor.CallCount > 0);
    }

    [Fact]
    public async Task OnDisconnected_ClosesRecoveryGate()
    {
        await ConnectAndMarkAllReadyAsync();

        await _hub.OnDisconnectedAsync(null);

        SeedQueued("sa-after-disconnect");
        _coordinator.SignalWorkAvailable();
        await AssertNeverAsync(() => _executor.CallCount > 0);
        Assert.Equal(SubagentTaskState.Queued, _store.GetById("sa-after-disconnect")!.State);
    }

    // ── Durable notification enqueue gating ──────────────────────────────

    [Fact]
    public async Task PendingNotification_NotEnqueuedUntilReady_ThenCarriesTaskId()
    {
        SeedTerminalWithPendingNotification("sa-note");
        _coordinator.SignalWorkAvailable();

        await AssertNeverAsync(() => _messageChannel.TryRead(out _));

        await ConnectAndMarkAllReadyAsync();

        AgentMessage? message = null;
        await WaitUntilAsync(() => _messageChannel.TryRead(out message));

        // The coordinator stamps the correlation id; terminal state was durable BEFORE
        // the synthetic message existed, and the claim is left Enqueued for the runtime.
        Assert.NotNull(message);
        Assert.Equal("sa-note", message!.SubagentTaskId);
        Assert.Equal(AgentMessageSource.SubagentCompletion, message.Source);
        Assert.Equal("conv-1", message.ConversationId);
        var task = _store.GetById("sa-note")!;
        Assert.Equal(SubagentTaskState.Completed, task.State);
        Assert.Equal(SubagentNotificationState.Enqueued, task.NotificationState);
    }

    [Fact]
    public async Task EnqueueFailure_ReleasesNotificationForRetry()
    {
        // A closed message channel makes the awaited EnqueueAsync throw — the claimed
        // notification must be RELEASED back to Pending, never silently dropped.
        _messageChannel.Complete();
        SeedTerminalWithPendingNotification("sa-enqueue-fail");

        await ConnectAndMarkAllReadyAsync();
        _coordinator.SignalWorkAvailable();

        await WaitUntilAsync(() =>
        {
            var task = _store.GetById("sa-enqueue-fail")!;
            return task.NotificationAttempts >= 1
                && task.NotificationState == SubagentNotificationState.Pending;
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task ConnectAndMarkAllReadyAsync()
    {
        await _hub.OnConnectedAsync();
        await _hub.ProvideCredentials(OneProviderCredentials());
        await _hub.UpdateMcpToolCatalog(new McpToolCatalog { Tools = [] });
    }

    private void SeedQueued(string taskId)
    {
        _store.Create(new SubagentTask
        {
            TaskId = taskId,
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "d",
            Prompt = "p",
            State = SubagentTaskState.Queued,
        });
    }

    private void SeedTerminalWithPendingNotification(string taskId)
    {
        _store.Create(new SubagentTask
        {
            TaskId = taskId,
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "d",
            Prompt = "p",
            State = SubagentTaskState.Completed,
            Result = "done",
            CompletedAt = DateTimeOffset.UtcNow,
            NotificationState = SubagentNotificationState.Pending,
        });
    }

    private static LlmCredentials OneProviderCredentials() => new()
    {
        Providers =
        [
            new LlmProviderCredential
            {
                Name = "test-openai",
                Api = "openai-completions",
                BaseUrl = "http://localhost:9999/v1",
                Kind = CredentialKind.ApiKey,
                ApiKey = "test-key",
                Models = ["test-model"],
                DefaultModel = "test-model",
            },
        ],
    };

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = typeof(AgentHub).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    /// <summary>Polls for the whole window and fails as soon as the condition becomes true.</summary>
    private static async Task AssertNeverAsync(Func<bool> condition, int windowMs = 300)
    {
        var deadline = Environment.TickCount64 + windowMs;
        while (Environment.TickCount64 < deadline)
        {
            Assert.False(condition());
            await Task.Delay(15);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
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

    /// <summary>Hand-written <see cref="ISubagentExecutor"/> substitute that records dispatches.</summary>
    private sealed class RecordingExecutor : ISubagentExecutor
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref this.callCount);

        public Task<SubagentExecutionResult> ExecuteAsync(SubagentTask task, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.callCount);
            return Task.FromResult(new SubagentExecutionResult(SubagentTaskState.Completed, "done"));
        }
    }
}
