using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute.ExceptionExtensions;

namespace Cortex.Contained.Agent.Host.Tests;

public class LlmTopicSlicerTests
{
    private readonly ILlmClient llmClient;
    private readonly IModelProvider modelProvider;
    private readonly LlmTopicSlicer slicer;

    public LlmTopicSlicerTests()
    {
        this.llmClient = Substitute.For<ILlmClient>();
        this.modelProvider = Substitute.For<IModelProvider>();
        this.modelProvider.MemoryModel.Returns("memory-model");
        this.modelProvider.DefaultModel.Returns("default-model");
        this.slicer = new LlmTopicSlicer(this.llmClient, this.modelProvider, NullLogger<LlmTopicSlicer>.Instance);
    }

    private static List<LlmMessage> SimpleHistory(int turns)
    {
        var list = new List<LlmMessage>();
        for (var i = 0; i < turns; i++)
        {
            list.Add(new LlmMessage { Role = "user", Content = $"u{i}" });
            list.Add(new LlmMessage { Role = "assistant", Content = $"a{i}" });
        }

        return list;
    }

    [Fact]
    public async Task SliceAsync_NormalJsonResponse_ReturnsSuccessNotDegraded()
    {
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = """{"boundaryIndex":3,"topicOneLine":"Test","priorSummary":"## Goal\nX"}""",
            });

        var outcome = await this.slicer.SliceAsync(SimpleHistory(5), "webchat-default", "webchat-default", default);

        var success = Assert.IsType<TopicSliceOutcome.Success>(outcome);
        Assert.Equal(3, success.BoundaryIndex);
        Assert.Equal("Test", success.TopicOneLine);
        Assert.Equal("## Goal\nX", success.PriorSummary);
        Assert.False(success.Degraded);
    }

    [Fact]
    public async Task SliceAsync_FencedJson_StillParses()
    {
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = """
                    ```json
                    {"boundaryIndex":2,"topicOneLine":"Fenced","priorSummary":null}
                    ```
                    """,
            });

        var outcome = await this.slicer.SliceAsync(SimpleHistory(5), "webchat-default", "webchat-default", default);

        var success = Assert.IsType<TopicSliceOutcome.Success>(outcome);
        Assert.Equal(2, success.BoundaryIndex);
        Assert.Equal("Fenced", success.TopicOneLine);
        Assert.False(success.Degraded);
    }

    [Fact]
    public async Task SliceAsync_MalformedJson_FallsBackDegradedTrue()
    {
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = true, Content = "this is not json" });

        var outcome = await this.slicer.SliceAsync(SimpleHistory(20), "webchat-default", "webchat-default", default);

        var success = Assert.IsType<TopicSliceOutcome.Success>(outcome);
        Assert.True(success.Degraded);
        Assert.Null(success.PriorSummary);
        // History has 40 messages (20 user+assistant pairs); fallback should be max(0, 40-10) == 30
        Assert.Equal(30, success.BoundaryIndex);
    }

    [Fact]
    public async Task SliceAsync_LlmReturnsFailure_ReturnsFailureWithReason()
    {
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = false, ErrorMessage = "quota exceeded" });

        var outcome = await this.slicer.SliceAsync(SimpleHistory(5), "webchat-default", "webchat-default", default);

        var failure = Assert.IsType<TopicSliceOutcome.Failure>(outcome);
        Assert.Contains("quota exceeded", failure.Reason);
    }

    [Fact]
    public async Task SliceAsync_LlmThrows_ReturnsFailureCatchingException()
    {
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("network down"));

        var outcome = await this.slicer.SliceAsync(SimpleHistory(5), "webchat-default", "webchat-default", default);

        var failure = Assert.IsType<TopicSliceOutcome.Failure>(outcome);
        Assert.Contains("network down", failure.Reason);
    }

    [Fact]
    public async Task SliceAsync_OutOfRangeBoundary_ClampsToHistoryCount()
    {
        // History has 10 messages. Slicer returns 999 → should clamp to 10.
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = """{"boundaryIndex":999,"topicOneLine":"X","priorSummary":null}""",
            });

        var outcome = await this.slicer.SliceAsync(SimpleHistory(5), "webchat-default", "webchat-default", default);

        var success = Assert.IsType<TopicSliceOutcome.Success>(outcome);
        Assert.Equal(10, success.BoundaryIndex);
    }

    [Fact]
    public async Task SliceAsync_NegativeBoundary_ClampsToZero()
    {
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = """{"boundaryIndex":-5,"topicOneLine":"X","priorSummary":null}""",
            });

        var outcome = await this.slicer.SliceAsync(SimpleHistory(5), "webchat-default", "webchat-default", default);

        var success = Assert.IsType<TopicSliceOutcome.Success>(outcome);
        Assert.Equal(0, success.BoundaryIndex);
    }

    [Fact]
    public async Task SliceAsync_RequestUsesMemoryModel()
    {
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = """{"boundaryIndex":0,"topicOneLine":"X","priorSummary":null}""",
            });

        await this.slicer.SliceAsync(SimpleHistory(5), "webchat-default", "webchat-default", default);

        await this.llmClient.Received(1).CompleteAsync(
            Arg.Is<LlmCompletionRequest>(r => r.Model == "memory-model"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SliceAsync_OptionsOverrideModel_UsesOverrideInsteadOfMemoryModel()
    {
        var optionsMonitor = Substitute.For<IOptionsMonitor<TransferSessionOptions>>();
        optionsMonitor.CurrentValue.Returns(new TransferSessionOptions
        {
            SlicerModel = "override-model",
            SlicerTemperature = 0.7,
        });

        var slicerWithOptions = new LlmTopicSlicer(
            this.llmClient,
            this.modelProvider,
            NullLogger<LlmTopicSlicer>.Instance,
            optionsMonitor);

        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = """{"boundaryIndex":0,"topicOneLine":"X","priorSummary":null}""",
            });

        await slicerWithOptions.SliceAsync(SimpleHistory(5), "webchat-default", "webchat-default", default);

        await this.llmClient.Received(1).CompleteAsync(
            Arg.Is<LlmCompletionRequest>(r => r.Model == "override-model" && r.Temperature == 0.7),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SliceAsync_OptionsOverridePrompt_UsesOverridePrompt()
    {
        var optionsMonitor = Substitute.For<IOptionsMonitor<TransferSessionOptions>>();
        optionsMonitor.CurrentValue.Returns(new TransferSessionOptions
        {
            SlicerSystemPromptOverride = "ALTERNATIVE_PROMPT_MARKER",
        });

        var slicerWithOptions = new LlmTopicSlicer(
            this.llmClient,
            this.modelProvider,
            NullLogger<LlmTopicSlicer>.Instance,
            optionsMonitor);

        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = """{"boundaryIndex":0,"topicOneLine":"X","priorSummary":null}""",
            });

        await slicerWithOptions.SliceAsync(SimpleHistory(5), "webchat-default", "webchat-default", default);

        await this.llmClient.Received(1).CompleteAsync(
            Arg.Is<LlmCompletionRequest>(r =>
                r.Messages.Any(m => m.Role == "system" && m.Content == "ALTERNATIVE_PROMPT_MARKER")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void BuildConversationText_ExcludesToolPlumbing()
    {
        var history = new[]
        {
            new LlmMessage { Role = "user", Content = "u0" },
            new LlmMessage
            {
                Role = "assistant",
                Content = "calling",
                ToolCalls = [new LlmToolCall { Id = "t1", Name = "foo", Arguments = "{}" }],
            },
            new LlmMessage { Role = "tool", Content = "result", ToolCallId = "t1" },
            new LlmMessage { Role = "assistant", Content = "a0" },
        };

        var text = LlmTopicSlicer.BuildConversationText(history);

        Assert.Contains("u0", text);
        Assert.Contains("a0", text);
        Assert.DoesNotContain("calling", text);
        Assert.DoesNotContain("result", text);
    }
}
