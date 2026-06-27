using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Contained.Integration.Tests;

/// <summary>
/// End-to-end integration test for the <c>transfer_session</c> tool: spins up the full
/// Agent Host via <see cref="AgentHostFactory"/>, swaps the <see cref="ILlmClient"/> for
/// a stub, resolves the tool through the real DI graph, and verifies the full pipeline
/// (validation → slicer → session seed → MessageStore breadcrumbs) works against the
/// actual production wiring rather than a hand-rolled fixture.
/// </summary>
public sealed class TransferSessionIntegrationTests : IClassFixture<AgentHostFactory>, IAsyncDisposable
{
    private readonly WebApplicationFactory<Cortex.Contained.Agent.Host.Hubs.AgentHub> factory;
    private readonly StubLlmClient stubLlm = new();

    public TransferSessionIntegrationTests(AgentHostFactory baseFactory)
    {
        // Build a new factory with our stub LLM swapped in. WithWebHostBuilder returns
        // a separate factory; the original AgentHostFactory is shared across tests.
        this.factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                // Remove the production ILlmClient registration and substitute our stub.
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(ILlmClient));
                if (existing is not null)
                {
                    services.Remove(existing);
                }

                services.AddSingleton<ILlmClient>(this.stubLlm);
            });
        });
    }

    public async ValueTask DisposeAsync()
    {
        await this.factory.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Tool_IsRegisteredInDi_AndDiscoverableByName()
    {
        var registry = this.factory.Services.GetRequiredService<ToolRegistry>();

        var definitions = registry.GetDefinitions();

        Assert.Contains(definitions, d => d.Name == "transfer_session");
    }

    [Fact]
    public async Task EndToEnd_TransferAttemptsProactiveDispatch_ViaRealDiGraph()
    {
        // This is the load-bearing test for the voice-fallback story: the tool, resolved
        // through the real DI graph, must route its post-seed greeting through the
        // shared IProactiveMessageDispatcher (which production wires to the Bridge
        // SignalR path that triggers ProactiveVoiceCoordinator for absent-user voice).
        //
        // In the integration test the Bridge isn't connected, so the dispatch will fail
        // gracefully with "Bridge is not connected" — what we're verifying is that the
        // dispatch is ATTEMPTED. If the wiring were broken (e.g. tool not injected with
        // the dispatcher, dispatcher not registered, DI cycle), we'd see no "greeting
        // dispatch failed" annotation in the success content.
        _ = this.factory.CreateClient();

        var sessionStore = this.factory.Services.GetRequiredService<AgentSessionStore>();
        var activeChannels = this.factory.Services.GetRequiredService<ActiveChannelStore>();
        var tools = this.factory.Services.GetServices<IAgentTool>().ToList();
        var transferTool = tools.Single(t => t.Name == "transfer_session");

        var sourceSession = sessionStore.GetOrCreate("webchat-default");
        sourceSession.ClearHistory();
        for (var i = 0; i < 5; i++)
        {
            sourceSession.AddMessage(new LlmMessage { Role = "user", Content = $"u{i}" });
            sourceSession.AddMessage(new LlmMessage { Role = "assistant", Content = $"a{i}" });
        }

        activeChannels.Set(["webchat-default", "voice-default"]);
        this.stubLlm.NextResponseContent =
            """{"boundaryIndex":4,"topicOneLine":"integration-test-topic","priorSummary":null}""";

        var result = await transferTool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            new ToolExecutionContext { ConversationId = "webchat-default", ChannelId = "webchat-default" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        // Bridge isn't connected in this test → dispatch fails → tool result annotates it.
        // This confirms the dispatcher was wired in and reached. The actual voice-ring
        // behavior is tested separately in the Bridge's own test suite.
        Assert.Contains("greeting dispatch failed", result.Content);
        Assert.Contains("Bridge is not connected", result.Content);
    }

    [Fact]
    public async Task ResetSession_DropsTransferSnapshot_SoRevertFails()
    {
        // If the user explicitly clears a channel, the transfer snapshot for that channel
        // must be dropped — otherwise a later revert_transfer call could resurrect content
        // the user deliberately erased.
        _ = this.factory.CreateClient();

        var sessionStore = this.factory.Services.GetRequiredService<AgentSessionStore>();
        var activeChannels = this.factory.Services.GetRequiredService<ActiveChannelStore>();
        var runtime = this.factory.Services.GetRequiredService<Cortex.Contained.Agent.Host.Agent.IAgentRuntime>();
        var tools = this.factory.Services.GetServices<IAgentTool>().ToList();
        var transferTool = tools.Single(t => t.Name == "transfer_session");
        var revertTool = tools.Single(t => t.Name == "revert_transfer");

        var targetSession = sessionStore.GetOrCreate("voice-default");
        targetSession.ClearHistory();
        targetSession.AddMessage(new LlmMessage { Role = "user", Content = "should-not-resurrect" });

        var sourceSession = sessionStore.GetOrCreate("webchat-default");
        sourceSession.ClearHistory();
        for (var i = 0; i < 5; i++)
        {
            sourceSession.AddMessage(new LlmMessage { Role = "user", Content = $"src u{i}" });
            sourceSession.AddMessage(new LlmMessage { Role = "assistant", Content = $"src a{i}" });
        }

        activeChannels.Set(["webchat-default", "voice-default"]);
        this.stubLlm.NextResponseContent =
            """{"boundaryIndex":0,"topicOneLine":"x","priorSummary":null}""";

        var transferResult = await transferTool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            new ToolExecutionContext { ConversationId = "webchat-default", ChannelId = "webchat-default" },
            CancellationToken.None);
        Assert.True(transferResult.Success, transferResult.Error);

        // User clears the voice session via the Bridge — runtime should drop the snapshot.
        await runtime.ResetSessionAsync("voice-default", CancellationToken.None);

        // Now revert should fail — no snapshot to resurrect.
        var revertResult = await revertTool.ExecuteAsync(
            """{}""",
            new ToolExecutionContext { ConversationId = "voice-default", ChannelId = "voice-default" },
            CancellationToken.None);
        Assert.False(revertResult.Success);
        Assert.Contains("No recent transfer snapshot", revertResult.Error);
    }

    [Fact]
    public async Task EndToEnd_RevertTransferRestoresTargetsPreTransferHistory()
    {
        _ = this.factory.CreateClient();

        var sessionStore = this.factory.Services.GetRequiredService<AgentSessionStore>();
        var activeChannels = this.factory.Services.GetRequiredService<ActiveChannelStore>();
        var tools = this.factory.Services.GetServices<IAgentTool>().ToList();
        var transferTool = tools.Single(t => t.Name == "transfer_session");
        var revertTool = tools.Single(t => t.Name == "revert_transfer");

        // Pre-populate the target session with some "old" history that the user
        // will want back after deciding the transfer was a mistake.
        var targetSession = sessionStore.GetOrCreate("voice-default");
        targetSession.ClearHistory();
        targetSession.AddMessage(new LlmMessage { Role = "user", Content = "old voice conversation u0" });
        targetSession.AddMessage(new LlmMessage { Role = "assistant", Content = "old voice conversation a0" });

        // Seed source for the transfer.
        var sourceSession = sessionStore.GetOrCreate("webchat-default");
        sourceSession.ClearHistory();
        for (var i = 0; i < 5; i++)
        {
            sourceSession.AddMessage(new LlmMessage { Role = "user", Content = $"new src u{i}" });
            sourceSession.AddMessage(new LlmMessage { Role = "assistant", Content = $"new src a{i}" });
        }

        activeChannels.Set(["webchat-default", "voice-default"]);

        this.stubLlm.NextResponseContent =
            """{"boundaryIndex":4,"topicOneLine":"transferred-topic","priorSummary":"## Goal\nsome goal"}""";

        var transferResult = await transferTool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            new ToolExecutionContext { ConversationId = "webchat-default", ChannelId = "webchat-default" },
            CancellationToken.None);
        Assert.True(transferResult.Success, transferResult.Error);

        // After transfer: target's history is the transferred slice, not the old conversation.
        var afterTransfer = sessionStore.GetOrCreate("voice-default").GetHistory();
        Assert.DoesNotContain(afterTransfer, m => m.Content == "old voice conversation u0");

        // Revert from the voice channel (which is where the user feels the loss).
        var revertResult = await revertTool.ExecuteAsync(
            """{}""",
            new ToolExecutionContext { ConversationId = "voice-default", ChannelId = "voice-default" },
            CancellationToken.None);
        Assert.True(revertResult.Success, revertResult.Error);

        // After revert: the old voice conversation is back.
        var afterRevert = sessionStore.GetOrCreate("voice-default").GetHistory();
        Assert.Contains(afterRevert, m => m.Content == "old voice conversation u0");
        Assert.Contains(afterRevert, m => m.Content == "old voice conversation a0");
        Assert.Equal(2, afterRevert.Count);

        // A second revert call should fail — snapshot was consumed by the first revert.
        var secondRevert = await revertTool.ExecuteAsync(
            """{}""",
            new ToolExecutionContext { ConversationId = "voice-default", ChannelId = "voice-default" },
            CancellationToken.None);
        Assert.False(secondRevert.Success);
        Assert.Contains("No recent transfer snapshot", secondRevert.Error);
    }

    [Fact]
    public async Task EndToEnd_TransferSeedsTargetSession_AndWritesBreadcrumbsToMessageStore()
    {
        // Force the factory to materialize the test server (which builds the DI graph).
        // Without this, the DI graph isn't ready and Services throws.
        _ = this.factory.CreateClient();

        var sessionStore = this.factory.Services.GetRequiredService<AgentSessionStore>();
        var activeChannels = this.factory.Services.GetRequiredService<ActiveChannelStore>();
        var messageStore = this.factory.Services.GetRequiredService<MessageStore>();
        var registry = this.factory.Services.GetRequiredService<ToolRegistry>();

        // Resolve the tool via the IAgentTool registration so we're testing the
        // DI-wired instance through its public interface (no reach into internals).
        var tools = this.factory.Services.GetServices<IAgentTool>().ToList();
        var tool = tools.Single(t => t.Name == "transfer_session");

        // Seed source history.
        var sourceSession = sessionStore.GetOrCreate("webchat-default");
        sourceSession.ClearHistory();
        for (var i = 0; i < 5; i++)
        {
            sourceSession.AddMessage(new LlmMessage { Role = "user", Content = $"u{i}" });
            sourceSession.AddMessage(new LlmMessage { Role = "assistant", Content = $"a{i}" });
        }

        activeChannels.Set(["webchat-default", "voice-default"]);

        this.stubLlm.NextResponseContent =
            """{"boundaryIndex":4,"topicOneLine":"integration-test-topic","priorSummary":"## Goal\nintegration"}""";

        var result = await tool.ExecuteAsync(
            """{"target_channel":"voice-default","user_confirmed":true}""",
            new ToolExecutionContext
            {
                ConversationId = "webchat-default",
                ChannelId = "webchat-default",
            },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);

        // Target session should have the seeded payload.
        var targetSession = sessionStore.GetOrCreate("voice-default");
        var targetHistory = targetSession.GetHistory();
        Assert.NotEmpty(targetHistory);
        Assert.Equal(LlmMessageType.CompactionSummary, targetHistory[0].MessageType);

        // Target-side breadcrumb is unconditional (seed succeeded, so the breadcrumb
        // is truthful regardless of dispatch outcome).
        var targetMessages = await messageStore.GetMessagesAsync("voice-default", limit: 10);
        Assert.Contains(targetMessages, m => m.Category == MessageCategory.Transfer);

        // Source-side breadcrumb is gated on dispatch success — in this integration test
        // the Bridge isn't connected so dispatch fails, and the source breadcrumb is
        // intentionally skipped to avoid the source channel UI claiming a transfer that
        // didn't deliver. Result content carries the dispatch-failure annotation.
        var sourceMessages = await messageStore.GetMessagesAsync("webchat-default", limit: 10);
        Assert.DoesNotContain(sourceMessages, m => m.Category == MessageCategory.Transfer);
        Assert.Contains("greeting dispatch failed", result.Content);
    }

    /// <summary>
    /// Minimal <see cref="ILlmClient"/> stub for integration tests. Returns whatever
    /// JSON content is set in <see cref="NextResponseContent"/> as a successful response.
    /// </summary>
    private sealed class StubLlmClient : ILlmClient
    {
        public string NextResponseContent { get; set; } =
            """{"boundaryIndex":0,"topicOneLine":"stub","priorSummary":null}""";

        public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new LlmCompletionResult
            {
                Success = true,
                Content = NextResponseContent,
            });
        }

        public async IAsyncEnumerable<LlmStreamChunk> StreamCompleteAsync(
            LlmCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new LlmStreamChunk
            {
                ContentDelta = NextResponseContent,
                IsComplete = true,
            };

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
