using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests;

public class SubAgentToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SubagentSessionStore _store;
    private readonly SubagentRunnerRegistry _registry;
    private readonly ILlmClient _mockLlmClient = Substitute.For<ILlmClient>();
    private readonly IModelProvider _mockModelProvider = Substitute.For<IModelProvider>();
    private readonly IOptionsMonitor<AgentConfig> _mockConfig;
    private readonly ToolExecutionContext _context;

    private string? _completionTaskId;
    private string? _completionResult;

    public SubAgentToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "subagent-tool-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new SubagentSessionStore(_tempDir, NullLogger<SubagentSessionStore>.Instance);
        _registry = new SubagentRunnerRegistry(2, NullLogger<SubagentRunnerRegistry>.Instance);
        _mockModelProvider.DefaultModel.Returns("gpt-4o");

        var config = new AgentConfig { MaxSubagentRounds = 10 };
        _mockConfig = Substitute.For<IOptionsMonitor<AgentConfig>>();
        _mockConfig.CurrentValue.Returns(config);

        _context = new ToolExecutionContext
        {
            ConversationId = "conv-1",
            ChannelId = "webchat-default",
        };
    }

    public void Dispose()
    {
        _store.Dispose();
        // registry has no IDisposable
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private Task OnCompletionAsync(string taskId, string result)
    {
        _completionTaskId = taskId;
        _completionResult = result;
        return Task.CompletedTask;
    }

    private static ToolRegistry CreateToolRegistry(params IAgentTool[] tools)
        => new(tools, new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);

    // ── SubAgentStartTool ────────────────────────────────────────────────

    [Fact]
    public async Task Start_ValidArgs_CreatesTaskAndReturnsId()
    {
        // Make LLM return immediately so the background task completes
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Done."));

        var tool = CreateStartTool();

        var result = await tool.ExecuteAsync(
            """{"description":"Find TODOs","prompt":"Search for all TODO comments"}""",
            _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("running", result.Content);
        Assert.Contains("sa-", result.Content);

        // Verify task was created in store
        var active = _store.GetActive();
        Assert.True(active.Count >= 1);
    }

    [Fact]
    public async Task Start_EmptyPrompt_ReturnsError()
    {
        var tool = CreateStartTool();

        var result = await tool.ExecuteAsync(
            """{"description":"Test","prompt":""}""",
            _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("prompt", result.Error!);
    }

    [Fact]
    public async Task Start_ThrottledWhenAllSlotsUsed_ReturnsQueued()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(NeverCompletingStream());

        var tool = CreateStartTool();

        // Fill both slots (registry has max 2)
        await tool.ExecuteAsync(
            """{"description":"Task 1","prompt":"Do task 1"}""",
            _context, CancellationToken.None);
        await tool.ExecuteAsync(
            """{"description":"Task 2","prompt":"Do task 2"}""",
            _context, CancellationToken.None);

        // Third should be queued
        var result = await tool.ExecuteAsync(
            """{"description":"Task 3","prompt":"Do task 3"}""",
            _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("queued", result.Content);
    }

    [Fact]
    public void StartQueuedTasks_FiresQueuedTasksOnStartup()
    {
        // Simulate a queued task from a previous container
        _store.Create(new SubagentTask
        {
            TaskId = "sa-queued-startup",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "Queued from before",
            Prompt = "Do the research",
            State = SubagentTaskState.Queued,
        });

        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Done."));

        var tool = CreateStartTool();
        tool.StartQueuedTasks();

        // Give background task a moment
        Thread.Sleep(500);

        // Task should no longer be queued
        var task = _store.GetById("sa-queued-startup");
        Assert.NotNull(task);
        Assert.NotEqual(SubagentTaskState.Queued, task.State);
    }

    [Fact]
    public void StartQueuedTasks_NoQueuedTasks_DoesNothing()
    {
        _store.Create(new SubagentTask
        {
            TaskId = "sa-completed",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "Already done",
            Prompt = "Nothing",
            State = SubagentTaskState.Completed,
        });

        var tool = CreateStartTool();
        tool.StartQueuedTasks(); // should not throw, should not start anything

        Assert.Equal(0, _registry.ActiveCount);
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

        // Register a mock runner
        var mockRunner = new SubagentRunner(
            _mockLlmClient, CreateToolRegistry(), 10, NullLogger<SubagentRunner>.Instance);
        _registry.TryRegister("sa-send-running", mockRunner);

        var tool = CreateSendTool();

        var result = await tool.ExecuteAsync(
            """{"task_id":"sa-send-running","message":"Also check tests/"}""",
            _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("sent", result.Content);
    }

    [Fact]
    public async Task Send_ToCompletedTask_ResumesSubagent()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Resumed result."));

        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are a subagent." },
            new() { Role = "user", Content = "Original task" },
            new() { Role = "assistant", Content = "Initial result." },
        };

        var task = new SubagentTask
        {
            TaskId = "sa-send-completed",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "Completed task",
            Prompt = "Original prompt",
            State = SubagentTaskState.Completed,
            Messages = messages,
        };
        _store.Create(task);
        // Manually update messages since Create serializes the initial value
        _store.UpdateMessages("sa-send-completed", messages, 1);

        var tool = CreateSendTool();

        var result = await tool.ExecuteAsync(
            """{"task_id":"sa-send-completed","message":"Also check tests/"}""",
            _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("resumed", result.Content);
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

        var tool = CreateSendTool();

        var result = await tool.ExecuteAsync(
            """{"task_id":"sa-send-queued","message":"Hello"}""",
            _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("queued", result.Error!);
    }

    [Fact]
    public async Task Send_NonexistentTask_ReturnsError()
    {
        var tool = CreateSendTool();

        var result = await tool.ExecuteAsync(
            """{"task_id":"nonexistent","message":"Hello"}""",
            _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("nonexistent", result.Error!);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private SubAgentStartTool CreateStartTool() => new(
        _mockLlmClient,
        () => CreateToolRegistry(),
        _mockModelProvider,
        _mockConfig,
        _store,
        _registry,
        OnCompletionAsync,
        NullLogger<SubAgentStartTool>.Instance,
        _tempDir);

    private SubAgentSendTool CreateSendTool() => new(
        _store,
        _registry,
        _mockLlmClient,
        () => CreateToolRegistry(),
        _mockModelProvider,
        _mockConfig,
        OnCompletionAsync,
        NullLogger<SubAgentSendTool>.Instance);

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

    // ── Crash / slot cleanup ───────────────────────────────────────────

    [Fact]
    public async Task Start_SubagentCrash_ReleasesSlot()
    {
        // LLM throws to simulate a crash
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncEnumerable<LlmStreamChunk>>(_ => throw new HttpRequestException("Timeout"));

        var tool = CreateStartTool();

        var result = await tool.ExecuteAsync(
            """{"description":"Crash test","prompt":"Do something"}""",
            _context, CancellationToken.None);

        Assert.True(result.Success); // Tool returns immediately, crash happens in background

        // Wait for background task to crash and clean up
        await Task.Delay(500);

        // Slot should be released — can acquire again
        Assert.True(_registry.HasAvailableSlot);

        // Task should be marked as failed
        var tasks = _store.GetActive();
        Assert.DoesNotContain(tasks, t => t.Description == "Crash test");
    }

    [Fact]
    public async Task Start_SubagentCrash_SlotsEventuallyFree()
    {
        // All subagents will crash immediately
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncEnumerable<LlmStreamChunk>>(_ => throw new HttpRequestException("Timeout"));

        var tool = CreateStartTool();

        // Start 3 subagents (max slots is 2)
        await tool.ExecuteAsync(
            """{"description":"Crash 1","prompt":"Do something"}""",
            _context, CancellationToken.None);
        await tool.ExecuteAsync(
            """{"description":"Crash 2","prompt":"Do something"}""",
            _context, CancellationToken.None);
        await tool.ExecuteAsync(
            """{"description":"Crash 3","prompt":"Do something"}""",
            _context, CancellationToken.None);

        // Wait for all crashes and cleanup
        await Task.Delay(1000);

        // All slots should be free after crashes (0 active out of max 2)
        Assert.Equal(0, _registry.ActiveCount);
    }

    [Fact]
    public async Task Start_SubagentCrash_NotifiesMainAgent()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncEnumerable<LlmStreamChunk>>(_ => throw new InvalidOperationException("LLM error"));

        var tool = CreateStartTool();

        await tool.ExecuteAsync(
            """{"description":"Notify test","prompt":"Do something"}""",
            _context, CancellationToken.None);

        // Wait for crash + notification
        await Task.Delay(500);

        // Completion callback should have been called with crash message
        Assert.NotNull(_completionTaskId);
        Assert.Contains("crashed", _completionResult!);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Stream that never completes — used to test throttling.</summary>
    private static async IAsyncEnumerable<LlmStreamChunk> NeverCompletingStream()
    {
        await Task.Delay(TimeSpan.FromHours(1));
        yield return new LlmStreamChunk { IsComplete = true };
    }
}
