using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class SubagentRunnerTests
{
    private readonly ILlmClient _mockLlmClient = Substitute.For<ILlmClient>();

    private static ToolRegistry CreateRegistry(params IAgentTool[] tools)
    {
        return new ToolRegistry(tools, new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);
    }

    [Fact]
    public async Task RunAsync_NoToolCalls_ReturnsFinalText()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("The answer is 42."));

        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(), 0, NullLogger<SubagentRunner>.Instance);

        var result = await runner.RunAsync("gpt-4o", "You are helpful.", "What is the answer?", "conv-1", CancellationToken.None);

        Assert.Equal(SubagentTaskState.Completed, result.TerminalState);
        Assert.Equal("The answer is 42.", result.Result);
    }

    // ── Outcome → terminal state mapping ────────────────────────────────

    [Fact]
    public async Task RunAsync_CompletedOutcome_ReturnsCompleted()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("All done."));

        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(), 0, NullLogger<SubagentRunner>.Instance);

        var result = await runner.RunAsync("gpt-4o", "System", "Prompt", "conv-1", CancellationToken.None);

        Assert.Equal(SubagentTaskState.Completed, result.TerminalState);
        Assert.Equal("All done.", result.Result);
    }

    [Fact]
    public async Task RunAsync_ErrorOutcome_ReturnsFailed()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ErrorStream("Rate limit exceeded"));

        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(), 0, NullLogger<SubagentRunner>.Instance);

        var result = await runner.RunAsync("gpt-4o", "System", "Prompt", "conv-1", CancellationToken.None);

        Assert.Equal(SubagentTaskState.Failed, result.TerminalState);
        Assert.Contains("Rate limit exceeded", result.Result);
    }

    [Fact]
    public async Task RunAsync_DoomLoop_ReturnsFailed()
    {
        var tool = new FakeTool("date_time", "Gets date", "2026-03-25");

        // Same tool call with identical arguments 3+ times consecutively triggers doom-loop detection.
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToolCallStream("call_1", "date_time", "{}"));

        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(tool), 0, NullLogger<SubagentRunner>.Instance);

        var result = await runner.RunAsync("gpt-4o", "System", "Prompt", "conv-1", CancellationToken.None);

        Assert.Equal(SubagentTaskState.Failed, result.TerminalState);
    }

    [Fact]
    public async Task RunAsync_MaxRoundsExceeded_ReturnsFailed()
    {
        var tool = new FakeTool("counter", "Counts", "ok");

        // Distinct arguments each round avoid doom-loop detection so the loop instead hits maxRounds.
        var call = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                call++;
                return ToolCallStream($"call_{call}", "counter", $$"""{"n":{{call}}}""");
            });

        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(tool), 2, NullLogger<SubagentRunner>.Instance);

        var result = await runner.RunAsync("gpt-4o", "System", "Prompt", "conv-1", CancellationToken.None);

        Assert.Equal(SubagentTaskState.Failed, result.TerminalState);
    }

    [Fact]
    public async Task RunAsync_PersistsFinalAssistantMessageForResume()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Final answer for resume."));

        var tempDir = Path.Combine(Path.GetTempPath(), "subagent-runner-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            using var store = new SubagentSessionStore(tempDir, NullLogger<SubagentSessionStore>.Instance);
            store.Create(new SubagentTask
            {
                TaskId = "sa-final-msg",
                ParentConversation = "conv-1",
                ParentChannel = "webchat",
                Description = "Test",
                Prompt = "Do something",
                State = SubagentTaskState.Running,
            });

            var runner = new SubagentRunner(
                _mockLlmClient, CreateRegistry(), 0, NullLogger<SubagentRunner>.Instance,
                store, "sa-final-msg", Substitute.For<IModelProvider>());

            var result = await runner.RunAsync("gpt-4o", "System", "Prompt", "sa-final-msg", CancellationToken.None);

            Assert.Equal(SubagentTaskState.Completed, result.TerminalState);

            // The final assistant response must be persisted so a later sub_agent_send can resume it.
            var task = store.GetById("sa-final-msg");
            Assert.NotNull(task);
            var last = task.Messages[^1];
            Assert.Equal("assistant", last.Role);
            Assert.Equal("Final answer for resume.", last.Content);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RunAsync_WithToolCalls_ExecutesToolsAndReturnsResult()
    {
        var tool = new FakeTool("date_time", "Gets current date", "2026-03-10T12:00:00Z");

        // Round 1: LLM calls the tool
        // Round 2: LLM returns final text
        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? ToolCallStream("call_1", "date_time", "{}")
                    : SingleChunkStream("The current date is March 10, 2026.");
            });

        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(tool), 0, NullLogger<SubagentRunner>.Instance);

        var result = await runner.RunAsync("gpt-4o", "You are helpful.", "What date is it?", "conv-1", CancellationToken.None);

        Assert.Equal("The current date is March 10, 2026.", result.Result);
    }

    [Fact]
    public async Task RunAsync_ToolCallAddsResultToMessages()
    {
        var tool = new FakeTool("date_time", "Gets current date", "2026-03-10");

        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? ToolCallStream("call_1", "date_time", "{}")
                    : SingleChunkStream("Done.");
            });

        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(tool), 0, NullLogger<SubagentRunner>.Instance);
        await runner.RunAsync("gpt-4o", "System", "User prompt", "conv-1", CancellationToken.None);

        // Verify second LLM call includes tool result in messages
        var secondCall = _mockLlmClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILlmClient.StreamCompleteAsync))
            .Skip(1)
            .First();
        var request = (LlmCompletionRequest)secondCall.GetArguments()[0]!;

        // Messages should be: system, user, assistant (with tool calls), tool result
        Assert.Equal(4, request.Messages.Count);
        Assert.Equal("tool", request.Messages[3].Role);
        Assert.Equal("2026-03-10", request.Messages[3].Content);
    }

    [Fact]
    public async Task RunAsync_ErrorChunk_ReturnsErrorMessage()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ErrorStream("Rate limit exceeded"));

        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(), 0, NullLogger<SubagentRunner>.Instance);

        var result = await runner.RunAsync("gpt-4o", "System", "Prompt", "conv-1", CancellationToken.None);

        Assert.Contains("Rate limit exceeded", result.Result);
    }

    [Fact]
    public async Task RunAsync_ExcludesSubagentTools_FromDefinitions()
    {
        var tool = new FakeTool("date_time", "Gets current date", "now");
        var startTool = new FakeTool("sub_agent_start", "Start subagent", "done");
        var readTool = new FakeTool("sub_agent_read", "Read subagent", "done");
        var sendTool = new FakeTool("sub_agent_send", "Send to subagent", "done");

        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Result"));

        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(tool, startTool, readTool, sendTool), 0, NullLogger<SubagentRunner>.Instance);
        await runner.RunAsync("gpt-4o", "System", "Prompt", "conv-1", CancellationToken.None);

        // Verify the LLM request does NOT include any subagent tools
        var llmCall = _mockLlmClient.ReceivedCalls()
            .First(c => c.GetMethodInfo().Name == nameof(ILlmClient.StreamCompleteAsync));
        var request = (LlmCompletionRequest)llmCall.GetArguments()[0]!;

        Assert.NotNull(request.Tools);
        Assert.Single(request.Tools); // Only date_time
        Assert.Equal("date_time", request.Tools[0].Name);
    }

    [Fact]
    public async Task RunAsync_MultipleToolCalls_ExecutesAll()
    {
        var tool1 = new FakeTool("tool_a", "Tool A", "result_a");
        var tool2 = new FakeTool("tool_b", "Tool B", "result_b");

        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? MultiToolCallStream(("call_1", "tool_a", "{}"), ("call_2", "tool_b", "{}"))
                    : SingleChunkStream("Both tools executed.");
            });

        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(tool1, tool2), 0, NullLogger<SubagentRunner>.Instance);

        var result = await runner.RunAsync("gpt-4o", "System", "Prompt", "conv-1", CancellationToken.None);

        Assert.Equal("Both tools executed.", result.Result);

        // Verify second call has both tool results
        var secondCall = _mockLlmClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILlmClient.StreamCompleteAsync))
            .Skip(1)
            .First();
        var request = (LlmCompletionRequest)secondCall.GetArguments()[0]!;

        // system + user + assistant + tool_a_result + tool_b_result = 5
        Assert.Equal(5, request.Messages.Count);
        Assert.Equal("result_a", request.Messages[3].Content);
        Assert.Equal("result_b", request.Messages[4].Content);
    }

    [Fact]
    public async Task RunAsync_ToolContext_ChannelIdIsConversationId_SoEachSubagentGetsOwnCodaChannel()
    {
        // Coda sessions are keyed by ToolExecutionContext.ChannelId. Each subagent must
        // get its OWN channel (its unique conversationId, "subagent-{taskId}") rather than
        // the shared constant "subagent" — otherwise concurrent subagents' coda sessions
        // collide (ambiguous_session).
        string? capturedChannelId = null;
        var probe = new ContextCapturingTool("probe", ctx => capturedChannelId = ctx.ChannelId);

        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? ToolCallStream("call_1", "probe", "{}")
                    : SingleChunkStream("done");
            });

        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(probe), 0, NullLogger<SubagentRunner>.Instance);

        await runner.RunAsync("gpt-4o", "System", "Prompt", "subagent-sa-abc123", CancellationToken.None);

        Assert.Equal("subagent-sa-abc123", capturedChannelId);
    }

    // ── Persistent mode ────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PersistentMode_ReturnsCompletedResult()
    {
        // The runner no longer takes an onCompletion callback — terminal ownership belongs to the
        // coordinator. The runner just returns the terminal outcome (state + result text).
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Done."));

        var tempDir = Path.Combine(Path.GetTempPath(), "subagent-runner-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            using var store = new SubagentSessionStore(tempDir, NullLogger<SubagentSessionStore>.Instance);
            store.Create(new SubagentTask
            {
                TaskId = "sa-test-1",
                ParentConversation = "conv-1",
                ParentChannel = "webchat",
                Description = "Test",
                Prompt = "Do something",
                State = SubagentTaskState.Running,
            });

            var runner = new SubagentRunner(
                _mockLlmClient, CreateRegistry(), 0, NullLogger<SubagentRunner>.Instance,
                store, "sa-test-1", Substitute.For<IModelProvider>());

            var result = await runner.RunAsync("gpt-4o", "System", "Prompt", "sa-test-1", CancellationToken.None);

            Assert.Equal(SubagentTaskState.Completed, result.TerminalState);
            Assert.Equal("Done.", result.Result);

            // The runner must NOT itself transition the store to a terminal state.
            Assert.Equal(SubagentTaskState.Running, store.GetById("sa-test-1")!.State);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RunAsync_PersistentMode_PersistsMessagesAfterRound()
    {
        var tool = new FakeTool("date_time", "Gets date", "2026-03-25");

        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? ToolCallStream("call_1", "date_time", "{}")
                    : SingleChunkStream("Done.");
            });

        var tempDir = Path.Combine(Path.GetTempPath(), "subagent-runner-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            using var store = new SubagentSessionStore(tempDir, NullLogger<SubagentSessionStore>.Instance);
            store.Create(new SubagentTask
            {
                TaskId = "sa-persist",
                ParentConversation = "conv-1",
                ParentChannel = "webchat",
                Description = "Test",
                Prompt = "Check the date",
                State = SubagentTaskState.Running,
            });

            var runner = new SubagentRunner(
                _mockLlmClient, CreateRegistry(tool), 0, NullLogger<SubagentRunner>.Instance,
                store, "sa-persist", Substitute.For<IModelProvider>());

            await runner.RunAsync("gpt-4o", "System", "Check the date", "sa-persist", CancellationToken.None);

            var task = store.GetById("sa-persist");
            Assert.NotNull(task);
            Assert.True(task.Messages.Count >= 4); // system, user, assistant+tool_call, tool_result
            Assert.True(task.Rounds > 0);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task InjectMessage_AppearsInNextRound()
    {
        var callCount = 0;
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                    return ToolCallStream("call_1", "date_time", "{}");

                // On second call, check that injected message is in the messages
                var request = (LlmCompletionRequest)callInfo[0];
                var hasInjected = request.Messages.Any(m => m.Role == "user" && m.Content == "Also check tests/");
                return SingleChunkStream(hasInjected ? "Found injected message." : "No injected message.");
            });

        var tool = new FakeTool("date_time", "Gets date", "2026-03-25");
        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(tool), 0, NullLogger<SubagentRunner>.Instance);

        // Inject before running — will be picked up at the start of round 2
        runner.InjectMessage("Also check tests/");

        var result = await runner.RunAsync("gpt-4o", "System", "Prompt", "conv-1", CancellationToken.None);

        Assert.Equal("Found injected message.", result.Result);
    }

    [Fact]
    public async Task ResumeAsync_ContinuesFromExistingMessages()
    {
        _mockLlmClient.StreamCompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleChunkStream("Resumed successfully."));

        var existingMessages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are a subagent." },
            new() { Role = "user", Content = "Original task" },
            new() { Role = "assistant", Content = "I found some results." },
            new() { Role = "user", Content = "Also check the tests directory" },
        };

        var runner = new SubagentRunner(_mockLlmClient, CreateRegistry(), 0, NullLogger<SubagentRunner>.Instance);
        var result = await runner.ResumeAsync("gpt-4o", existingMessages, "conv-1", CancellationToken.None);

        Assert.Equal("Resumed successfully.", result.Result);

        // Verify the LLM received all existing messages
        var llmCall = _mockLlmClient.ReceivedCalls()
            .First(c => c.GetMethodInfo().Name == nameof(ILlmClient.StreamCompleteAsync));
        var request = (LlmCompletionRequest)llmCall.GetArguments()[0]!;
        Assert.Equal(4, request.Messages.Count);
        Assert.Equal("Also check the tests directory", request.Messages[3].Content);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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

    private static async IAsyncEnumerable<LlmStreamChunk> ErrorStream(string error)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk { ErrorMessage = error };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> ToolCallStream(string callId, string toolName, string args)
    {
        await Task.CompletedTask;
        yield return new LlmStreamChunk
        {
            ToolCallDeltas = [new LlmToolCallDelta { Index = 0, Id = callId, Name = toolName, ArgumentsDelta = args }],
            IsComplete = true,
            FinishReason = "tool_calls",
        };
    }

    private static async IAsyncEnumerable<LlmStreamChunk> MultiToolCallStream(
        params (string Id, string Name, string Args)[] calls)
    {
        await Task.CompletedTask;
        var deltas = calls.Select((c, i) => new LlmToolCallDelta
        {
            Index = i,
            Id = c.Id,
            Name = c.Name,
            ArgumentsDelta = c.Args,
        }).ToList();

        yield return new LlmStreamChunk
        {
            ToolCallDeltas = deltas,
            IsComplete = true,
            FinishReason = "tool_calls",
        };
    }

    private sealed class ContextCapturingTool : IAgentTool
    {
        private readonly Action<ToolExecutionContext> _onExecute;

        public ContextCapturingTool(string name, Action<ToolExecutionContext> onExecute)
        {
            Name = name;
            _onExecute = onExecute;
        }

        public string Name { get; }
        public string Description => "Captures the execution context.";
        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            _onExecute(context);
            return Task.FromResult(new AgentToolResult { Success = true, Content = "ok" });
        }
    }

    private sealed class FakeTool : IAgentTool
    {
        private readonly string _result;

        public FakeTool(string name, string description, string result)
        {
            Name = name;
            Description = description;
            _result = result;
        }

        public string Name { get; }
        public string Description { get; }
        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AgentToolResult { Success = true, Content = _result });
        }
    }
}
