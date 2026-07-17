using System.Net;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests the GitHub Copilot (<c>github-copilot-api</c>) path in <see cref="AnthropicApiClient"/>:
/// a Copilot bearer provider must POST the Anthropic Messages wire shape to <c>/v1/messages</c>
/// on the Copilot base URL, authenticate with <c>Authorization: Bearer</c> plus the Copilot
/// header set and <c>anthropic-version</c> (no <c>x-api-key</c>, no OAuth beta query/header), and
/// on a 401 refresh the bearer once via the Bridge and retry <c>/v1/messages</c>. Caller
/// cancellation propagates.
/// </summary>
public class AnthropicApiClientCopilotTests
{
    private const string SeededBearer = "seeded-bearer";
    private const string StaleBearer = "stale-bearer";
    private const string RefreshedBearer = "refreshed-bearer";

    private const string AnthropicResponseJson =
        """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""";

    /// <summary>Anthropic Messages SSE: one text delta "hi" then a terminal message_delta.</summary>
    private const string AnthropicStreamSse =
        "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n";

    private static LlmCompletionRequest BuildRequest() => new()
    {
        Model = "gpt-5.6-sol",
        Messages = [new LlmMessage { Role = "user", Content = "hello" }],
        RequestId = "req-1",
        ConversationId = "conv-1",
    };

    private static ProviderState BuildCopilotProvider(string bearer = SeededBearer) => new(new LlmProviderCredential
    {
        Name = "github-copilot",
        Api = "github-copilot-api",
        BaseUrl = "https://api.githubcopilot.com",
        Kind = CredentialKind.GitHubCopilotBearer,
        AccessToken = bearer,
        ApiKey = null,
        AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
        Models = ["gpt-5.6-sol"],
    });

    [Fact]
    public async Task CompleteAsync_GitHubCopilot_PostsMessagesWithCopilotHeadersAndNoApiKey()
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, AnthropicResponseJson));
        using var tokenManager = new OAuthTokenManager(NullLogger.Instance, metrics: null);
        var client = new AnthropicApiClient(
            new RecordingHttpClientFactory(handler), tokenManager, NullLogger.Instance);

        var result = await client.CompleteAsync(
            BuildCopilotProvider(), BuildRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hi", result.Content);

        var sent = Assert.Single(handler.Requests);
        Assert.StartsWith("https://api.githubcopilot.com/", sent.Url);
        Assert.EndsWith("/v1/messages", sent.AbsolutePath);
        Assert.DoesNotContain("beta=true", sent.Url);

        Assert.Equal("Bearer", sent.Authorization?.Scheme);
        Assert.Equal(SeededBearer, sent.Authorization?.Parameter);
        Assert.Equal("conversation-edits", sent.Headers.GetValueOrDefault("Openai-Intent"));
        Assert.Equal("user", sent.Headers.GetValueOrDefault("x-initiator"));
        Assert.Equal("2026-06-01", sent.Headers.GetValueOrDefault("X-GitHub-Api-Version"));
        Assert.Equal("2023-06-01", sent.Headers.GetValueOrDefault("anthropic-version"));
        Assert.False(sent.Headers.ContainsKey("x-api-key"));
        Assert.False(sent.Headers.ContainsKey("anthropic-beta"));
    }

    [Fact]
    public async Task CompleteAsync_GitHubCopilot_401_RefreshesOnceAndRetriesMessagesWithFreshBearer()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.Unauthorized, """{"type":"error","error":{"message":"unauthorized"}}"""),
            (HttpStatusCode.OK, AnthropicResponseJson));
        var count = 0;
        using var tokenManager = new OAuthTokenManager(NullLogger.Instance, metrics: null);
        tokenManager.SetRequestTokenRefreshCallback(_ =>
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(new TokenRefreshResult
            {
                Success = true,
                AccessToken = RefreshedBearer,
                RefreshToken = null,
                ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
            });
        });
        var client = new AnthropicApiClient(
            new RecordingHttpClientFactory(handler), tokenManager, NullLogger.Instance);

        var result = await client.CompleteAsync(
            BuildCopilotProvider(StaleBearer), BuildRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, Volatile.Read(ref count));
        Assert.All(handler.Requests, r => Assert.EndsWith("/v1/messages", r.AbsolutePath));
        Assert.Equal(StaleBearer, handler.Requests[0].Authorization?.Parameter);
        Assert.Equal(RefreshedBearer, handler.Requests[1].Authorization?.Parameter);
    }

    [Fact]
    public async Task CompleteAsync_GitHubCopilot_CallerCancellation_IsRethrownNotDegraded()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler(
            (HttpStatusCode.Unauthorized, """{"type":"error","error":{"message":"unauthorized"}}"""));
        using var tokenManager = new OAuthTokenManager(NullLogger.Instance, metrics: null);
        tokenManager.SetRequestTokenRefreshCallback(_ =>
        {
            cts.Cancel();
            return Task.FromResult(new TokenRefreshResult
            {
                Success = true,
                AccessToken = RefreshedBearer,
                ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
            });
        });
        var client = new AnthropicApiClient(
            new RecordingHttpClientFactory(handler), tokenManager, NullLogger.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CompleteAsync(BuildCopilotProvider(StaleBearer), BuildRequest(), cts.Token));
    }

    [Fact]
    public async Task StreamAsync_GitHubCopilot_401_RefreshesOnceAndRetriesMessagesWithFreshBearer()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.Unauthorized, """{"type":"error","error":{"message":"unauthorized"}}"""),
            (HttpStatusCode.OK, AnthropicStreamSse));
        var count = 0;
        using var tokenManager = new OAuthTokenManager(NullLogger.Instance, metrics: null);
        tokenManager.SetRequestTokenRefreshCallback(_ =>
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(new TokenRefreshResult
            {
                Success = true,
                AccessToken = RefreshedBearer,
                RefreshToken = null,
                ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
            });
        });
        var client = new AnthropicApiClient(
            new RecordingHttpClientFactory(handler), tokenManager, NullLogger.Instance);

        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in client.StreamAsync(
            BuildCopilotProvider(StaleBearer), BuildRequest(), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.DoesNotContain(chunks, c => !string.IsNullOrEmpty(c.ErrorMessage));
        Assert.Contains(chunks, c => c.ContentDelta == "hi");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, Volatile.Read(ref count));
        Assert.All(handler.Requests, r => Assert.EndsWith("/v1/messages", r.AbsolutePath));
        Assert.Equal(StaleBearer, handler.Requests[0].Authorization?.Parameter);
        Assert.Equal(RefreshedBearer, handler.Requests[1].Authorization?.Parameter);

        // No leaked response across the retry branch.
        Assert.All(handler.CreatedResponses, r => Assert.True(r.IsDisposed));
    }
}
