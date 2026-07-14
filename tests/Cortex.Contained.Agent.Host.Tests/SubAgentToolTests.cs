using System.Diagnostics;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class SubAgentToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SubagentSessionStore _store;
    private readonly SubagentRunnerRegistry _registry;
    private readonly ILlmClient _mockLlmClient = Substitute.For<ILlmClient>();
    private readonly ToolExecutionContext _context;
    private readonly List<SubagentExecutionCoordinator> _coordinators = [];

    public SubAgentToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "subagent-tool-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new SubagentSessionStore(_tempDir, NullLogger<SubagentSessionStore>.Instance);
        _registry = new SubagentRunnerRegistry(2, NullLogger<SubagentRunnerRegistry>.Instance);

        _context = new ToolExecutionContext
        {
            ConversationId = "conv-1",
            ChannelId = "webchat-default",
        };
    }

    public void Dispose()
    {
        foreach (var coord in _coordinators)
        {
            try { coord.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { /* teardown */ }
            coord.Dispose();
        }

        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static ToolRegistry CreateToolRegistry(params IAgentTool[] tools)
        => new(tools, new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

    /// <summary>
    /// Builds a coordinator wired to <paramref name="executor"/>. When <paramref name="started"/> is
    /// true the dispatch loop runs and readiness is satisfied so signalled work is actually dispatched;
    /// otherwise the coordinator is inert and queued tasks stay put for store inspection.
    /// </summary>
    private SubagentExecutionCoordinator BuildCoordinator(ISubagentExecutor executor, bool started)
    {
        SubagentRunner RunnerFactory(SubagentTask _) => new(
            _mockLlmClient,
            CreateToolRegistry(),
            10,
            NullLogger<SubagentRunner>.Instance);

        var coordinator = new SubagentExecutionCoordinator(
            _store,
            _registry,
            executor,
            RunnerFactory,
            new AgentMessageChannel(),
            NullLogger<SubagentExecutionCoordinator>.Instance);

        _coordinators.Add(coordinator);

        if (started)
        {
            coordinator.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            coordinator.OnBridgeConnected();
            coordinator.MarkCredentialsReady(true);
            coordinator.MarkMcpCatalogReady();
        }

        return coordinator;
    }

    // ── SubAgentStartTool ────────────────────────────────────────────────

    [Fact]
    public async Task Start_ValidArgs_PersistsNewModeAndSkill()
    {
        var coordinator = BuildCoordinator(new NoopExecutor(), started: false);
        var tool = new SubAgentStartTool(_store, coordinator, NullLogger<SubAgentStartTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"description":"Research topic","prompt":"Do the research","skill":"deep-research"}""",
            _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("sa-", result.Content);

        var active = _store.GetActive();
        var task = Assert.Single(active);
        Assert.Equal(SubagentTaskState.Queued, task.State);
        Assert.Equal(SubagentRunMode.New, task.RunMode);
        Assert.Equal("deep-research", task.SkillName);
        Assert.Equal("Do the research", task.Prompt);
    }

    [Fact]
    public async Task Start_ValidArgs_SignalsCoordinator()
    {
        var executor = new NoopExecutor();
        var coordinator = BuildCoordinator(executor, started: true);
        var tool = new SubAgentStartTool(_store, coordinator, NullLogger<SubAgentStartTool>.Instance);

        // Let the readiness pass drain against the (currently empty) queue so only the tool's own
        // SignalWorkAvailable can wake the loop for the task created below.
        await Task.Delay(150);

        var result = await tool.ExecuteAsync(
            """{"description":"Find TODOs","prompt":"Search for all TODO comments"}""",
            _context, CancellationToken.None);

        Assert.True(result.Success);

        // The coordinator dispatched the task — proof the tool signalled it.
        await WaitUntilAsync(() => executor.CallCount > 0);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task Start_EmptyPrompt_ReturnsError()
    {
        var coordinator = BuildCoordinator(new NoopExecutor(), started: false);
        var tool = new SubAgentStartTool(_store, coordinator, NullLogger<SubAgentStartTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"description":"Test","prompt":""}""",
            _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("prompt", result.Error!);
    }

    // ── SubAgentReadTool ─────────────────────────────────────────────────

    [Fact]
    public async Task Read_ExistingTask_ReturnsDetails()
    {
        _store.Create(new SubagentTask
        {
            TaskId = "sa-read-test",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "Test read",
            Prompt = "Do something",
            State = SubagentTaskState.Completed,
            Result = "Found 5 items.",
        });

        var tool = new SubAgentReadTool(_store, NullLogger<SubAgentReadTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"task_id":"sa-read-test"}""",
            _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("sa-read-test", result.Content);
        Assert.Contains("Test read", result.Content);
        Assert.Contains("completed", result.Content);
        Assert.Contains("Found 5 items.", result.Content);
    }

    [Fact]
    public async Task Read_NonexistentTask_ReturnsError()
    {
        var tool = new SubAgentReadTool(_store, NullLogger<SubAgentReadTool>.Instance);

        var result = await tool.ExecuteAsync(
            """{"task_id":"nonexistent"}""",
            _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("nonexistent", result.Error!);
    }

    [Fact]
    public async Task Read_MissingTaskId_ReturnsError()
    {
        var tool = new SubAgentReadTool(_store, NullLogger<SubAgentReadTool>.Instance);

        var result = await tool.ExecuteAsync("""{}""", _context, CancellationToken.None);

        Assert.False(result.Success);
    }

    // ── SubAgentSendTool ─────────────────────────────────────────────────

    [Fact]
    public async Task Send_ToRunningTask_InjectsMessage()
    {
        _store.Create(new SubagentTask
        {
            TaskId = "sa-send-running",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "Test send",
            Prompt = "Original prompt",
            State = SubagentTaskState.Running,
        });

        var mockRunner = new SubagentRunner(
            _mockLlmClient, CreateToolRegistry(), 10, NullLogger<SubagentRunner>.Instance);
        _registry.TryRegister("sa-send-running", mockRunner, out _);

        var coordinator = BuildCoordinator(new NoopExecutor(), started: false);
        var tool = CreateSendTool(coordinator);

        var result = await tool.ExecuteAsync(
            """{"task_id":"sa-send-running","message":"Also check tests/"}""",
            _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("sent", result.Content);
    }

    [Fact]
    public async Task Send_TerminalTask_QueuesResume()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are a subagent." },
            new() { Role = "user", Content = "Original task" },
            new() { Role = "assistant", Content = "Initial result." },
        };

        _store.Create(new SubagentTask
        {
            TaskId = "sa-send-completed",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "Completed task",
            Prompt = "Original prompt",
            State = SubagentTaskState.Completed,
            Result = "Initial result.",
            Messages = messages,
        });
        _store.UpdateMessages("sa-send-completed", messages, 1);

        var coordinator = BuildCoordinator(new NoopExecutor(), started: false);
        var tool = CreateSendTool(coordinator);

        var result = await tool.ExecuteAsync(
            """{"task_id":"sa-send-completed","message":"Also check tests/"}""",
            _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("resumed", result.Content);

        // The resume must be a durable, guarded transition: queued, RunMode.Resume, message appended.
        var task = _store.GetById("sa-send-completed");
        Assert.NotNull(task);
        Assert.Equal(SubagentTaskState.Queued, task.State);
        Assert.Equal(SubagentRunMode.Resume, task.RunMode);
        Assert.Equal("Also check tests/", task.Messages[^1].Content);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public async Task Send_TerminalTask_DoesNotUseParentCancellationToken()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "Original task" },
            new() { Role = "assistant", Content = "Initial result." },
        };
        _store.Create(new SubagentTask
        {
            TaskId = "sa-send-token",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "Failed task",
            Prompt = "Original prompt",
            State = SubagentTaskState.Failed,
            Result = "boom",
            Messages = messages,
        });
        _store.UpdateMessages("sa-send-token", messages, 1);

        var coordinator = BuildCoordinator(new NoopExecutor(), started: false);
        var tool = CreateSendTool(coordinator);

        // A cancelled parent token must NOT abort the durable resume — the tool ties the resume to
        // the store + coordinator, never to the caller's token.
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();

        var result = await tool.ExecuteAsync(
            """{"task_id":"sa-send-token","message":"keep going"}""",
            _context, alreadyCancelled.Token);

        Assert.True(result.Success);
        var task = _store.GetById("sa-send-token");
        Assert.NotNull(task);
        Assert.Equal(SubagentTaskState.Queued, task.State);
        Assert.Equal(SubagentRunMode.Resume, task.RunMode);
    }

    [Fact]
    public async Task Send_ToQueuedTask_ReturnsError()
    {
        _store.Create(new SubagentTask
        {
            TaskId = "sa-send-queued",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "Queued task",
            Prompt = "Original prompt",
            State = SubagentTaskState.Queued,
        });

        var coordinator = BuildCoordinator(new NoopExecutor(), started: false);
        var tool = CreateSendTool(coordinator);

        var result = await tool.ExecuteAsync(
            """{"task_id":"sa-send-queued","message":"Hello"}""",
            _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("queued", result.Error!);
    }

    [Fact]
    public async Task Send_NonexistentTask_ReturnsError()
    {
        var coordinator = BuildCoordinator(new NoopExecutor(), started: false);
        var tool = CreateSendTool(coordinator);

        var result = await tool.ExecuteAsync(
            """{"task_id":"nonexistent","message":"Hello"}""",
            _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("nonexistent", result.Error!);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private SubAgentSendTool CreateSendTool(SubagentExecutionCoordinator coordinator) => new(
        _store,
        _registry,
        coordinator,
        NullLogger<SubAgentSendTool>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition was not satisfied within the timeout.");
            }

            await Task.Delay(15).ConfigureAwait(false);
        }
    }

    /// <summary>A minimal executor substitute that records invocations and completes immediately.</summary>
    private sealed class NoopExecutor : ISubagentExecutor
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
