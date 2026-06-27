using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace Cortex.Contained.Agent.Host.Tests;

public class TransferSessionToolTests : IAsyncDisposable
{
    private static readonly ToolExecutionContext Context = new()
    {
        ConversationId = "webchat-default",
        ChannelId = "webchat-default",
    };

    private readonly ActiveChannelStore activeChannelStore;
    private readonly AgentSessionStore sessionStore;
    private readonly ILlmClient llmClient;
    private readonly IModelProvider modelProvider;
    private readonly MessageStore messageStore;
    private readonly IProactiveMessageDispatcher dispatcher;
    private readonly TransferSessionTool tool;
    private readonly SubagentSessionStore subagentStore;
    private readonly string subagentTempDir;

    public TransferSessionToolTests()
    {
        this.activeChannelStore = new ActiveChannelStore();
        this.sessionStore = new AgentSessionStore(
            new SessionConfig(),
            new MemorySettingsStore(),
            NullLogger<AgentSessionStore>.Instance);
        this.llmClient = Substitute.For<ILlmClient>();
        this.modelProvider = Substitute.For<IModelProvider>();
        this.modelProvider.MemoryModel.Returns("gpt-4o");
        this.modelProvider.DefaultModel.Returns("gpt-4o");
        this.messageStore = new MessageStore(":memory:", NullLogger<MessageStore>.Instance);

        var slicer = new LlmTopicSlicer(
            this.llmClient,
            this.modelProvider,
            NullLogger<LlmTopicSlicer>.Instance);

        // Stub the agent runtime — in unit tests we don't need the full runtime, just
        // the TransferSessionAsync method to behave like AgentSessionStore.Seed.
        var fakeRuntime = Substitute.For<Cortex.Contained.Agent.Host.Agent.IAgentRuntime>();
        fakeRuntime
            .When(r => r.TransferSessionAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<LlmMessage>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var channelId = (string)call.Args()[0];
                var messages = (IReadOnlyList<LlmMessage>)call.Args()[1];
                var session = this.sessionStore.GetOrCreateWithIdleCheck(channelId);
                _ = session.DrainExtractionBuffer();
                this.sessionStore.Seed(channelId, messages, maxHistory: int.MaxValue);
            });

        // Stub the dispatcher — unit tests verify the call shape; integration tests
        // exercise the real ProactiveMessageDispatcher path end-to-end.
        this.dispatcher = Substitute.For<IProactiveMessageDispatcher>();
        this.dispatcher
            .DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ToolExecutionContext?>(), Arg.Any<CancellationToken>())
            .Returns(new ProactiveDispatchResult { Success = true });

        this.subagentTempDir = Path.Combine(
            Path.GetTempPath(),
            "transfer-tool-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(this.subagentTempDir);
        this.subagentStore = new SubagentSessionStore(
            this.subagentTempDir,
            NullLogger<SubagentSessionStore>.Instance);

        this.tool = new TransferSessionTool(
            this.sessionStore,
            this.activeChannelStore,
            slicer,
            () => fakeRuntime,
            this.dispatcher,
            this.messageStore,
            NullLogger<TransferSessionTool>.Instance,
            new ChannelConversationResolver(),
            this.subagentStore);
    }

    public async ValueTask DisposeAsync()
    {
        await this.messageStore.DisposeAsync().ConfigureAwait(false);
        this.subagentStore.Dispose();
        try { Directory.Delete(this.subagentTempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Name_IsTransferSession()
    {
        Assert.Equal("transfer_session", this.tool.Name);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(this.tool.Description));
    }

    [Fact]
    public void ParametersSchema_IsValidJson_WithTargetChannelRequired()
    {
        var doc = System.Text.Json.JsonDocument.Parse(this.tool.ParametersSchema);
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
        Assert.True(doc.RootElement.GetProperty("properties").TryGetProperty("target_channel", out _));
        var required = doc.RootElement.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("target_channel", required);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJson_ReturnsError()
    {
        var result = await this.tool.ExecuteAsync("not json", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Invalid JSON", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_MissingTargetChannel_ReturnsError()
    {
        var result = await this.tool.ExecuteAsync("""{}""", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("target_channel", result.Error);
        Assert.Contains("required", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyTargetChannel_ReturnsError()
    {
        var result = await this.tool.ExecuteAsync("""{"target_channel":""}""", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("target_channel", result.Error);
        Assert.Contains("required", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_TargetSameAsSource_ReturnsError()
    {
        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"webchat-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cannot be the same", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_TargetSameAsSource_FriendlyName_ReturnsError()
    {
        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"webchat","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cannot be the same", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_BreadcrumbWriteFails_SessionStillSeeded_ResultStillSuccess()
    {
        // If a MessageStore write fails after Seed has already succeeded, we don't roll
        // back the in-memory seed (that's the durable change). Tool returns success with
        // a degradation note. Simulate this by disposing the MessageStore before the call.
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        SetSlicerResponse("""{"boundaryIndex":0,"topicOneLine":"X","priorSummary":null}""");

        await this.messageStore.DisposeAsync(); // any subsequent SaveMessageAsync will throw

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        // The target session should still be seeded — that's the load-bearing state change.
        var targetSession = this.sessionStore.GetOrCreate("voice-default");
        Assert.NotEmpty(targetSession.GetHistory());
        // Result content should note the breadcrumb failure so the agent can mention it if useful.
        Assert.Contains("breadcrumb", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentTransfersToSameTarget_SerializeDeterministically()
    {
        // Two transfers to the same target should serialize. Final target session state
        // is the result of whichever completed last — but BOTH should observe a consistent
        // outcome (no torn state, no doubled marker, no slicer mid-write surprises).
        this.activeChannelStore.Set(["webchat-default", "voice-default", "discord-dm"]);
        SeedSourceHistory(5);

        // Hold the slicer until both transfers have been kicked off, then release.
        var firstCallReceived = new TaskCompletionSource();
        var releaseSlicer = new TaskCompletionSource();
        var callCount = 0;
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var n = Interlocked.Increment(ref callCount);
                if (n == 1)
                {
                    firstCallReceived.TrySetResult();
                    await releaseSlicer.Task.ConfigureAwait(false);
                }

                return new LlmCompletionResult
                {
                    Success = true,
                    Content = $$"""{"boundaryIndex":0,"topicOneLine":"slice {{n}}","priorSummary":null}""",
                };
            });

        // Kick off two concurrent transfers to the same target.
        var t1 = this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        await firstCallReceived.Task;

        var contextB = new ToolExecutionContext
        {
            ConversationId = "discord-dm",
            ChannelId = "discord-dm",
        };
        // Source B needs history too so it passes validation.
        var sessionB = this.sessionStore.GetOrCreate("discord-dm");
        sessionB.AddMessage(new LlmMessage { Role = "user", Content = "b user 0" });
        sessionB.AddMessage(new LlmMessage { Role = "assistant", Content = "b assistant 0" });
        sessionB.AddMessage(new LlmMessage { Role = "user", Content = "b user 1" });
        sessionB.AddMessage(new LlmMessage { Role = "assistant", Content = "b assistant 1" });

        var t2 = this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            contextB,
            CancellationToken.None);

        // Release the slicer — the first call returns, then the second one runs serially.
        releaseSlicer.SetResult();

        var r1 = await t1;
        var r2 = await t2;

        Assert.True(r1.Success, r1.Error);
        Assert.True(r2.Success, r2.Error);

        // Exactly two slicer LLM calls (one per transfer) — concurrency lock means they
        // serialize, but each still gets its own slicer call.
        Assert.Equal(2, callCount);

        // The target session ends up with the second transfer's content (last write wins).
        // The Transfer breadcrumb count in MessageStore should be 2 (one per transfer).
        var targetMessages = await this.messageStore.GetMessagesAsync("voice-default", limit: 10);
        var markers = targetMessages.Where(m => m.Category == MessageCategory.Transfer).ToList();
        Assert.Equal(2, markers.Count);
    }

    [Fact]
    public async Task ExecuteAsync_SourceIsSyntheticConversation_ReturnsError()
    {
        // Synthetic conversation ids (e.g. "scheduled-abc") don't belong to a real channel.
        // Transferring from one would mis-attribute the source on the target's breadcrumb.
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        var syntheticContext = new ToolExecutionContext
        {
            ConversationId = "scheduled-abc123",
            ChannelId = "scheduled-abc123",
        };

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            syntheticContext,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not a transferable conversation", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_TargetNotActive_ReturnsError()
    {
        this.activeChannelStore.Set(["webchat-default"]);

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not currently active", result.Error);
        Assert.Contains("voice-default", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_TargetNotActive_FriendlyName_ReturnsError()
    {
        this.activeChannelStore.Set(["webchat-default"]);

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not currently active", result.Error);
    }

    private void SeedSourceHistory(int userTurns)
    {
        var session = this.sessionStore.GetOrCreate(Context.ConversationId);
        session.ClearHistory();
        for (var i = 0; i < userTurns; i++)
        {
            session.AddMessage(new LlmMessage { Role = "user", Content = $"user message {i}" });
            session.AddMessage(new LlmMessage { Role = "assistant", Content = $"assistant reply {i}" });
        }
    }

    [Fact]
    public async Task ExecuteAsync_SourceEmpty_ReturnsError()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(0);

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no meaningful history", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_SourceHasOnlyOneUserTurn_ReturnsError()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(1);

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no meaningful history", result.Error);
    }

    private void SetSlicerResponse(string jsonContent)
    {
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = jsonContent,
            });
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_CallsLlmExactlyOnce()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        SetSlicerResponse("""
            {
              "boundaryIndex": 0,
              "topicOneLine": "Test topic",
              "priorSummary": null
            }
            """);

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        await this.llmClient.Received(1)
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_UsesMemoryModel()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        this.modelProvider.MemoryModel.Returns("memory-model-name");
        SetSlicerResponse("""{"boundaryIndex":0,"topicOneLine":"X","priorSummary":null}""");

        await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        await this.llmClient.Received(1).CompleteAsync(
            Arg.Is<LlmCompletionRequest>(r => r.Model == "memory-model-name"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_PassesSystemPromptMentioningTransfer()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        SetSlicerResponse("""{"boundaryIndex":0,"topicOneLine":"X","priorSummary":null}""");

        await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        await this.llmClient.Received(1).CompleteAsync(
            Arg.Is<LlmCompletionRequest>(r =>
                r.Messages.Any(m => m.Role == "system" && m.Content != null && m.Content.Contains("transfer", StringComparison.OrdinalIgnoreCase))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SlicerReturnsMalformedJson_FallsBackAndSucceeds()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        SetSlicerResponse("this is not json");

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("degraded", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_SlicerReturnsFailure_ReturnsError()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult { Success = false, ErrorMessage = "quota exceeded" });

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("quota exceeded", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_SlicerThrows_ReturnsError()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("network down"));

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("network down", result.Error);
    }

    // ── BuildSeedPayload unit tests ──────────────────────────────────────────

    [Fact]
    public void BuildSeedPayload_NoPriorSummary_VerbatimOnlyNoMarker()
    {
        var history = new[]
        {
            new LlmMessage { Role = "user", Content = "u0" },
            new LlmMessage { Role = "assistant", Content = "a0" },
            new LlmMessage { Role = "user", Content = "u1" },
            new LlmMessage { Role = "assistant", Content = "a1" },
        };

        var payload = TransferSessionTool.BuildSeedPayload(history, 0, priorSummary: null, sourceChannelId: "webchat-default");

        Assert.Equal(4, payload.Count);
        Assert.All(payload, m => Assert.Equal(LlmMessageType.Normal, m.MessageType));
        Assert.Equal("u0", payload[0].Content);
        Assert.Equal("a1", payload[3].Content);
    }

    [Fact]
    public void BuildSeedPayload_WithPriorSummary_StartsWithCompactionSummaryMarker()
    {
        var history = new[]
        {
            new LlmMessage { Role = "user", Content = "before" },
            new LlmMessage { Role = "assistant", Content = "earlier reply" },
            new LlmMessage { Role = "user", Content = "current topic q" },
            new LlmMessage { Role = "assistant", Content = "current topic a" },
        };

        var payload = TransferSessionTool.BuildSeedPayload(
            history,
            boundaryIndex: 2,
            priorSummary: "## Goal\nDo a thing.",
            sourceChannelId: "webchat-default");

        Assert.Equal(3, payload.Count);
        Assert.Equal(LlmMessageType.CompactionSummary, payload[0].MessageType);
        Assert.Equal("user", payload[0].Role);
        Assert.Contains("transferred from webchat-default", payload[0].Content);
        Assert.Contains("## Goal", payload[0].Content);
        Assert.Contains("do not acknowledge", payload[0].Content);
        Assert.Equal("current topic q", payload[1].Content);
        Assert.Equal("current topic a", payload[2].Content);
    }

    [Fact]
    public void BuildSeedPayload_ExcludesToolPlumbing()
    {
        var history = new[]
        {
            new LlmMessage { Role = "user", Content = "u0" },
            new LlmMessage
            {
                Role = "assistant",
                Content = "calling tool",
                ToolCalls = [new LlmToolCall { Id = "t1", Name = "foo", Arguments = "{}" }],
            },
            new LlmMessage { Role = "tool", Content = "tool result", ToolCallId = "t1" },
            new LlmMessage { Role = "assistant", Content = "a0" },
        };

        var payload = TransferSessionTool.BuildSeedPayload(history, 0, null, "webchat-default");

        Assert.Equal(2, payload.Count);
        Assert.Equal("u0", payload[0].Content);
        Assert.Equal("a0", payload[1].Content);
    }

    [Fact]
    public void BuildSeedPayload_NegativeBoundary_ClampsToZero()
    {
        var history = new[]
        {
            new LlmMessage { Role = "user", Content = "u0" },
            new LlmMessage { Role = "assistant", Content = "a0" },
        };

        var payload = TransferSessionTool.BuildSeedPayload(history, boundaryIndex: -5, null, "webchat-default");

        Assert.Equal(2, payload.Count);
        Assert.Equal("u0", payload[0].Content);
    }

    [Fact]
    public void BuildSeedPayload_BoundaryPastEnd_ClampsToHistoryCount()
    {
        var history = new[]
        {
            new LlmMessage { Role = "user", Content = "u0" },
            new LlmMessage { Role = "assistant", Content = "a0" },
        };

        var payload = TransferSessionTool.BuildSeedPayload(history, boundaryIndex: 999, null, "webchat-default");

        Assert.Empty(payload);
    }

    // ── End-to-end seed into target session ──────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HappyPath_SeedsTargetSession()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        // SeedSourceHistory(5) produces 10 messages: u0,a0,u1,a1,u2,a2,u3,a3,u4,a4
        // (interleaved user messages "user message {i}" and assistant replies "assistant reply {i}").
        SeedSourceHistory(5);
        SetSlicerResponse("""
            {
              "boundaryIndex": 6,
              "topicOneLine": "TTS streaming bug",
              "priorSummary": "## Goal\nDebug TTS."
            }
            """);

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);

        var targetSession = this.sessionStore.GetOrCreate("voice-default");
        var targetHistory = targetSession.GetHistory();
        // Expect 1 marker + 4 verbatim messages from indices 6..9 (u3, a3, u4, a4)
        Assert.Equal(5, targetHistory.Count);
        Assert.Equal(LlmMessageType.CompactionSummary, targetHistory[0].MessageType);
        Assert.Contains("transferred from webchat-default", targetHistory[0].Content);
        Assert.Equal("user message 3", targetHistory[1].Content);
        Assert.Equal("assistant reply 3", targetHistory[2].Content);
        Assert.Equal("user message 4", targetHistory[3].Content);
        Assert.Equal("assistant reply 4", targetHistory[4].Content);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_WritesFriendlyTransferMarkerToTargetMessageStore()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        SetSlicerResponse("""{"boundaryIndex":0,"topicOneLine":"X","priorSummary":null}""");

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);

        var targetMessages = await this.messageStore.GetMessagesAsync("voice-default", limit: 10);
        var marker = targetMessages.SingleOrDefault(m => m.Category == MessageCategory.Transfer);
        Assert.NotNull(marker);
        Assert.Equal("system", marker.Role);
        // Friendly source name, not the canonical channel id
        Assert.Contains("WebChat", marker.Content);
        Assert.DoesNotContain("webchat-default", marker.Content);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_WritesFriendlyBreadcrumbToSourceMessageStore()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        SetSlicerResponse("""{"boundaryIndex":0,"topicOneLine":"X","priorSummary":null}""");

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);

        var sourceMessages = await this.messageStore.GetMessagesAsync("webchat-default", limit: 10);
        var breadcrumb = sourceMessages.SingleOrDefault(m => m.Category == MessageCategory.Transfer);
        Assert.NotNull(breadcrumb);
        Assert.Equal("system", breadcrumb.Role);
        Assert.Contains("→", breadcrumb.Content);
        Assert.Contains("voice", breadcrumb.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_TargetNotActive_NoMarkerWritten()
    {
        this.activeChannelStore.Set(["webchat-default"]); // voice not active
        SeedSourceHistory(5);

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.False(result.Success);

        var targetMessages = await this.messageStore.GetMessagesAsync("voice-default", limit: 10);
        Assert.DoesNotContain(targetMessages, m => m.Category == MessageCategory.Transfer);
        var sourceMessages = await this.messageStore.GetMessagesAsync("webchat-default", limit: 10);
        Assert.DoesNotContain(sourceMessages, m => m.Category == MessageCategory.Transfer);
    }

    [Fact]
    public async Task ExecuteAsync_SlicerThrows_NoMarkerWritten()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        this.llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("network down"));

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.False(result.Success);

        var targetMessages = await this.messageStore.GetMessagesAsync("voice-default", limit: 10);
        Assert.DoesNotContain(targetMessages, m => m.Category == MessageCategory.Transfer);
        var sourceMessages = await this.messageStore.GetMessagesAsync("webchat-default", limit: 10);
        Assert.DoesNotContain(sourceMessages, m => m.Category == MessageCategory.Transfer);
    }

    [Fact]
    public async Task ExecuteAsync_SlicerReturnsFencedJson_ParsesSuccessfullyNoDegradation()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        // LLMs frequently wrap JSON in ``` fences despite prompt instructions.
        // StripToJson should handle this — no fallback should fire.
        SetSlicerResponse("""
            ```json
            {
              "boundaryIndex": 0,
              "topicOneLine": "Wrapped topic",
              "priorSummary": null
            }
            ```
            """);

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("degraded", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Wrapped topic", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_DrainsTargetExtractionBuffer()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);

        var targetSession = this.sessionStore.GetOrCreate("voice-default");
        targetSession.AppendToExtractionBuffer(new ExtractionEntry
        {
            Role = "user",
            Content = "stale",
            Timestamp = DateTimeOffset.UtcNow,
        });
        Assert.Equal(1, targetSession.ExtractionBufferCount);

        SetSlicerResponse("""{"boundaryIndex":0,"topicOneLine":"X","priorSummary":null}""");

        await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.Equal(0, targetSession.ExtractionBufferCount);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_SourceUnchanged()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        SetSlicerResponse("""{"boundaryIndex":2,"topicOneLine":"X","priorSummary":"## Goal\nx"}""");

        var sourceSession = this.sessionStore.GetOrCreate(Context.ConversationId);
        var historyBefore = sourceSession.GetHistory().ToList();

        await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        var historyAfter = sourceSession.GetHistory().ToList();
        Assert.Equal(historyBefore.Count, historyAfter.Count);
        for (var i = 0; i < historyBefore.Count; i++)
        {
            Assert.Equal(historyBefore[i].Content, historyAfter[i].Content);
            Assert.Equal(historyBefore[i].Role, historyAfter[i].Role);
        }
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_DispatchesGreetingViaDispatcher()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        SetSlicerResponse("""{"boundaryIndex":4,"topicOneLine":"TTS streaming bug","priorSummary":null}""");

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);

        await this.dispatcher.Received(1).DispatchAsync(
            "voice-default",
            Arg.Is<string>(s => s.Contains("TTS streaming bug", StringComparison.Ordinal) && s.Contains("WebChat", StringComparison.Ordinal)),
            Arg.Any<ToolExecutionContext?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_EmptyTopicOneLine_SlicerNormalizesToUnspecified_GreetingIncludesPlaceholder()
    {
        // When the LLM returns an empty topic line, LlmTopicSlicer normalizes it to
        // "(unspecified)" — the topic-line-empty branch in TransferSessionTool itself is
        // defensive cover for slicer paths that bypass normalization (the fallback path).
        // Test verifies the realistic end-to-end outcome: greeting includes the placeholder.
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        SetSlicerResponse("""{"boundaryIndex":0,"topicOneLine":"","priorSummary":null}""");

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);

        await this.dispatcher.Received(1).DispatchAsync(
            "voice-default",
            Arg.Is<string>(s =>
                s.StartsWith("Continuing our conversation here from WebChat", StringComparison.Ordinal)
                && s.Contains("(unspecified)", StringComparison.Ordinal)),
            Arg.Any<ToolExecutionContext?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DispatcherFails_TransferStillSucceedsButResultNotesFailure()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        SetSlicerResponse("""{"boundaryIndex":0,"topicOneLine":"X","priorSummary":null}""");

        this.dispatcher
            .DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ToolExecutionContext?>(), Arg.Any<CancellationToken>())
            .Returns(new ProactiveDispatchResult { Success = false, Error = "Bridge is not connected." });

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        // The seed already happened — the transfer is still successful, but the
        // greeting failure is surfaced so the agent can decide whether to mention it.
        Assert.True(result.Success, result.Error);
        Assert.Contains("greeting dispatch failed", result.Content);

        // Source-side breadcrumb must NOT be written when dispatch failed — otherwise
        // the source channel's UI would lie about the conversation having moved.
        var sourceMessages = await this.messageStore.GetMessagesAsync("webchat-default", limit: 10);
        Assert.DoesNotContain(sourceMessages, m => m.Category == MessageCategory.Transfer);
        // Target-side breadcrumb IS written either way — the in-memory seed succeeded.
        var targetMessages = await this.messageStore.GetMessagesAsync("voice-default", limit: 10);
        Assert.Contains(targetMessages, m => m.Category == MessageCategory.Transfer);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ContentMentionsTargetTopicAndCount()
    {
        this.activeChannelStore.Set(["webchat-default", "voice-default"]);
        SeedSourceHistory(5);
        SetSlicerResponse("""
            {
              "boundaryIndex": 4,
              "topicOneLine": "Debugging TTS",
              "priorSummary": "## Goal\nX"
            }
            """);

        var result = await this.tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            Context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("voice-default", result.Content);
        Assert.Contains("Debugging TTS", result.Content);
        Assert.Contains("verbatim messages", result.Content);
        Assert.Contains("summary of prior context", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_TargetIsDiscordVoice_SeedsToTenantSuffixedConversation()
    {
        // Source: discord-dm (no tenant suffix). Target: discord-voice. Expected
        // target conversation id: discord-voice-default (default tenant fallback).
        var activeChannels = new ActiveChannelStore();
        activeChannels.Set(["discord-dm", "discord-voice"]);

        var sessionStore = new AgentSessionStore(
            new SessionConfig(),
            new MemorySettingsStore(),
            NullLogger<AgentSessionStore>.Instance);

        var runtime = Substitute.For<IAgentRuntime>();
        runtime
            .When(r => r.TransferSessionAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<LlmMessage>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var convId = (string)call.Args()[0];
                var messages = (IReadOnlyList<LlmMessage>)call.Args()[1];
                _ = sessionStore.GetOrCreateWithIdleCheck(convId).DrainExtractionBuffer();
                sessionStore.Seed(convId, messages, maxHistory: int.MaxValue);
            });

        var dispatcher = Substitute.For<IProactiveMessageDispatcher>();
        dispatcher
            .DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ToolExecutionContext?>(), Arg.Any<CancellationToken>())
            .Returns(new ProactiveDispatchResult { Success = true });

        var llmClient = Substitute.For<ILlmClient>();
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.MemoryModel.Returns("gpt-4o");
        modelProvider.DefaultModel.Returns("gpt-4o");
        llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = """{"boundaryIndex":0,"topicOneLine":"Test topic","priorSummary":null}""",
            });

        await using var messageStore = new MessageStore(":memory:", NullLogger<MessageStore>.Instance);

        var slicer = new LlmTopicSlicer(llmClient, modelProvider, NullLogger<LlmTopicSlicer>.Instance);

        var tool = new TransferSessionTool(
            sessionStore,
            activeChannels,
            slicer,
            () => runtime,
            dispatcher,
            messageStore,
            NullLogger<TransferSessionTool>.Instance,
            new ChannelConversationResolver(),
            this.subagentStore);

        // Seed source history on discord-dm.
        var sourceContext = new ToolExecutionContext { ChannelId = "discord-dm", ConversationId = "discord-dm" };
        var sourceSession = sessionStore.GetOrCreate("discord-dm");
        for (var i = 0; i < 3; i++)
        {
            sourceSession.AddMessage(new LlmMessage { Role = "user", Content = $"user {i}" });
            sourceSession.AddMessage(new LlmMessage { Role = "assistant", Content = $"assistant {i}" });
        }

        var result = await tool.ExecuteAsync(
            """{"target_channel":"discord-voice","user_confirmed":true}""",
            sourceContext,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        await runtime.Received(1).TransferSessionAsync(
            "discord-voice-default",
            Arg.Any<IReadOnlyList<LlmMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SourceVoiceTargetDm_ExtractsTenantFromSource()
    {
        // Source: discord-voice-acme (carries tenant=acme in its id).
        // Target: discord-dm. Expected target conversation id: discord-dm
        // (no suffix; tenant was correctly extracted from source but dm ignores it).
        var activeChannels = new ActiveChannelStore();
        activeChannels.Set(["discord-voice", "discord-dm"]);

        var sessionStore = new AgentSessionStore(
            new SessionConfig(),
            new MemorySettingsStore(),
            NullLogger<AgentSessionStore>.Instance);

        var runtime = Substitute.For<IAgentRuntime>();
        runtime
            .When(r => r.TransferSessionAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<LlmMessage>>(),
                Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var convId = (string)call.Args()[0];
                var messages = (IReadOnlyList<LlmMessage>)call.Args()[1];
                _ = sessionStore.GetOrCreateWithIdleCheck(convId).DrainExtractionBuffer();
                sessionStore.Seed(convId, messages, maxHistory: int.MaxValue);
            });

        var dispatcher = Substitute.For<IProactiveMessageDispatcher>();
        dispatcher
            .DispatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ToolExecutionContext?>(), Arg.Any<CancellationToken>())
            .Returns(new ProactiveDispatchResult { Success = true });

        var llmClient = Substitute.For<ILlmClient>();
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.MemoryModel.Returns("gpt-4o");
        modelProvider.DefaultModel.Returns("gpt-4o");
        llmClient
            .CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmCompletionResult
            {
                Success = true,
                Content = """{"boundaryIndex":0,"topicOneLine":"Test topic","priorSummary":null}""",
            });

        await using var messageStore = new MessageStore(":memory:", NullLogger<MessageStore>.Instance);

        var slicer = new LlmTopicSlicer(llmClient, modelProvider, NullLogger<LlmTopicSlicer>.Instance);

        var tool = new TransferSessionTool(
            sessionStore,
            activeChannels,
            slicer,
            () => runtime,
            dispatcher,
            messageStore,
            NullLogger<TransferSessionTool>.Instance,
            new ChannelConversationResolver(),
            this.subagentStore);

        // Seed source history on discord-voice-acme.
        var sourceContext = new ToolExecutionContext { ChannelId = "discord-voice", ConversationId = "discord-voice-acme" };
        var sourceSession = sessionStore.GetOrCreate("discord-voice-acme");
        for (var i = 0; i < 3; i++)
        {
            sourceSession.AddMessage(new LlmMessage { Role = "user", Content = $"user {i}" });
            sourceSession.AddMessage(new LlmMessage { Role = "assistant", Content = $"assistant {i}" });
        }

        var result = await tool.ExecuteAsync(
            """{"target_channel":"discord-dm","user_confirmed":true}""",
            sourceContext,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        await runtime.Received(1).TransferSessionAsync(
            "discord-dm",
            Arg.Any<IReadOnlyList<LlmMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RepointsActiveSubagentsWithinCurrentTopic()
    {
        // Seed source history first so we can read its timestamps and place
        // subagent CreatedAt times around the topic boundary deterministically.
        this.activeChannelStore.Set(["webchat-default", "discord-voice"]);
        SeedSourceHistory(6); // 6 user + 6 assistant messages
        var sourceSession = this.sessionStore.GetOrCreate(Context.ConversationId);
        var history = sourceSession.GetHistory();

        // Slicer will claim the topic starts at message 8 (the 5th user turn).
        // Anything spawned at/after that message's timestamp is "current topic."
        const int boundaryIndex = 8;
        var boundaryTimestamp = history[boundaryIndex].Timestamp;

        var srcConvId = Context.ConversationId;

        // (a) Active subagent spawned AFTER boundary — within current topic. Repoint.
        var inTopicActive = new SubagentTask
        {
            TaskId = "sa-in-topic",
            ParentConversation = srcConvId,
            ParentChannel = Context.ChannelId,
            Description = "current topic work",
            Prompt = "investigate X",
            State = SubagentTaskState.Running,
            CreatedAt = boundaryTimestamp.AddSeconds(1),
        };

        // (b) Active subagent spawned BEFORE boundary — prior topic. Leave alone.
        var preTopicActive = new SubagentTask
        {
            TaskId = "sa-pre-topic",
            ParentConversation = srcConvId,
            ParentChannel = Context.ChannelId,
            Description = "prior topic work",
            Prompt = "investigate Y",
            State = SubagentTaskState.Running,
            CreatedAt = boundaryTimestamp.AddSeconds(-1),
        };

        // (c) Completed subagent within the topic window — already delivered. Leave alone.
        var inTopicCompleted = new SubagentTask
        {
            TaskId = "sa-in-topic-done",
            ParentConversation = srcConvId,
            ParentChannel = Context.ChannelId,
            Description = "current topic done",
            Prompt = "investigate Y2",
            State = SubagentTaskState.Completed,
            CreatedAt = boundaryTimestamp.AddSeconds(2),
            CompletedAt = DateTimeOffset.UtcNow,
        };

        // (d) Unrelated active subagent owned by a different conversation. Leave alone.
        var unrelated = new SubagentTask
        {
            TaskId = "sa-unrelated",
            ParentConversation = "some-other-conv",
            ParentChannel = "webchat-default",
            Description = "unrelated",
            Prompt = "investigate Z",
            State = SubagentTaskState.Running,
            CreatedAt = boundaryTimestamp.AddSeconds(1),
        };

        this.subagentStore.Create(inTopicActive);
        this.subagentStore.Create(preTopicActive);
        this.subagentStore.Create(inTopicCompleted);
        this.subagentStore.Create(unrelated);

        SetSlicerResponse($$"""
            {
              "boundaryIndex": {{boundaryIndex}},
              "topicOneLine": "Test topic",
              "priorSummary": "earlier we discussed Y"
            }
            """);

        var args = """{"target_channel":"discord-voice","user_confirmed":true}""";
        var result = await this.tool.ExecuteAsync(args, Context, CancellationToken.None);

        Assert.True(result.Success, result.Error);

        // Voice target gets the tenant suffix via ChannelConversationResolver.
        var expectedTargetConv = "discord-voice-default";

        // (a) In-topic active subagent: repointed.
        var aAfter = this.subagentStore.GetById("sa-in-topic");
        Assert.NotNull(aAfter);
        Assert.Equal(expectedTargetConv, aAfter.ParentConversation);
        Assert.Equal("discord-voice", aAfter.ParentChannel);

        // (b) Pre-topic active subagent: NOT repointed — pinned to source.
        var bAfter = this.subagentStore.GetById("sa-pre-topic");
        Assert.NotNull(bAfter);
        Assert.Equal(srcConvId, bAfter.ParentConversation);
        Assert.Equal(Context.ChannelId, bAfter.ParentChannel);

        // (c) Completed in-topic subagent: NOT repointed — already done.
        var cAfter = this.subagentStore.GetById("sa-in-topic-done");
        Assert.NotNull(cAfter);
        Assert.Equal(srcConvId, cAfter.ParentConversation);

        // (d) Unrelated subagent: untouched.
        var dAfter = this.subagentStore.GetById("sa-unrelated");
        Assert.NotNull(dAfter);
        Assert.Equal("some-other-conv", dAfter.ParentConversation);
        Assert.Equal("webchat-default", dAfter.ParentChannel);
    }

    [Fact]
    public async Task ExecuteAsync_UserConfirmedMissing_ReturnsError()
    {
        this.activeChannelStore.Set(["webchat-default", "discord-voice"]);
        SeedSourceHistory(5);
        SetSlicerResponse("""
            {
              "boundaryIndex": 0,
              "topicOneLine": "Test topic",
              "priorSummary": null
            }
            """);

        var args = """{"target_channel":"discord-voice"}""";
        var result = await this.tool.ExecuteAsync(args, Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("user_confirmed", result.Error, StringComparison.Ordinal);
        Assert.Contains("send_message", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_UserConfirmedFalse_ReturnsError()
    {
        this.activeChannelStore.Set(["webchat-default", "discord-voice"]);
        SeedSourceHistory(5);
        SetSlicerResponse("""
            {
              "boundaryIndex": 0,
              "topicOneLine": "Test topic",
              "priorSummary": null
            }
            """);

        var args = """{"target_channel":"discord-voice","user_confirmed":false}""";
        var result = await this.tool.ExecuteAsync(args, Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("user_confirmed", result.Error, StringComparison.Ordinal);
        Assert.Contains("send_message", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParametersSchema_DeclaresUserConfirmedAsRequired()
    {
        var doc = System.Text.Json.JsonDocument.Parse(this.tool.ParametersSchema);
        var required = doc.RootElement.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("user_confirmed", required);

        Assert.True(doc.RootElement.GetProperty("properties").TryGetProperty("user_confirmed", out var prop));
        Assert.Equal("boolean", prop.GetProperty("type").GetString());
    }
}
