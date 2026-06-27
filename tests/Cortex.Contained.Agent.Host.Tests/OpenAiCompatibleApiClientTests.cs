using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests for the <see cref="CredentialKind.GitHubCopilotBearer"/> path in
/// <see cref="OpenAiCompatibleApiClient"/>. The Bridge-minted bearer must be used directly as
/// <c>Authorization: Bearer {AccessToken}</c> with the OpenCode-style header set, and the agent
/// must NEVER call the GitHub PAT→token exchange endpoint (the PAT is not even in the container).
/// </summary>
public class OpenAiCompatibleApiClientTests
{
    private const string CopilotResponseJson =
        """{"id":"x","choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    private static LlmCompletionRequest BuildRequest() => new()
    {
        Model = "claude-opus-4.8",
        Messages = [new LlmMessage { Role = "user", Content = "hello" }],
        RequestId = "req-1",
        ConversationId = "conv-1",
    };

    [Fact]
    public async Task CompleteAsync_GitHubCopilotBearer_UsesBearerDirectlyAndSkipsTokenExchange()
    {
        var handler = new CapturingHandler(CopilotResponseJson);
        var factory = new SingleClientFactory(handler);
        using var tokenManager = new OAuthTokenManager(NullLogger.Instance, metrics: null);
        var client = new OpenAiCompatibleApiClient(factory, tokenManager, NullLogger.Instance);

        var provider = new ProviderState(new LlmProviderCredential
        {
            Name = "github-copilot",
            Api = "github-copilot-api",
            Kind = CredentialKind.GitHubCopilotBearer,
            AccessToken = "minted-bearer-123",
            // PAT is null in the container by construction.
            ApiKey = null,
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
            Models = ["claude-opus-4.8"],
        });

        var result = await client.CompleteAsync(provider, BuildRequest(), CancellationToken.None);

        Assert.True(result.Success);

        // Exactly one request, sent to the chat-completions endpoint (not the token exchange).
        Assert.Single(handler.Requests);
        var sent = handler.Requests[0];
        Assert.DoesNotContain(handler.Requests, r =>
            r.RequestUri!.AbsoluteUri.Contains("copilot_internal", StringComparison.OrdinalIgnoreCase));

        // Authorization: Bearer {minted bearer} — used directly.
        Assert.Equal("Bearer", sent.Headers.Authorization?.Scheme);
        Assert.Equal("minted-bearer-123", sent.Headers.Authorization?.Parameter);

        // OpenCode-style headers (same set the GitHubOAuth branch uses).
        Assert.True(sent.Headers.Contains("Openai-Intent"));
        Assert.Equal("conversation-edits", string.Join(",", sent.Headers.GetValues("Openai-Intent")));
        Assert.True(sent.Headers.Contains("x-initiator"));
        Assert.Equal("user", string.Join(",", sent.Headers.GetValues("x-initiator")));

        // NOT the legacy VS Code PAT headers.
        Assert.False(sent.Headers.Contains("Copilot-Integration-Id"));
        Assert.False(sent.Headers.Contains("Editor-Version"));
    }

    // ── I1: on-401 Bridge-minted bearer retry path ──────────────────────────────
    // A 401 must trigger exactly ONE Bridge round-trip (ForceRefreshAsync), the retry must
    // use the refreshed bearer, and the call must then succeed. The PAT→token exchange
    // endpoint (copilot_internal) must NEVER be hit — the PAT is not in the container.

    private const string StaleBearer = "stale-bearer";
    private const string RefreshedBearer = "refreshed-bearer-456";

    /// <summary>Builds a provider seeded with a stale, near-expired bearer so the on-401 path drives a refresh.</summary>
    private static ProviderState BuildCopilotBearerProvider() => new(new LlmProviderCredential
    {
        Name = "github-copilot",
        Api = "github-copilot-api",
        Kind = CredentialKind.GitHubCopilotBearer,
        AccessToken = StaleBearer,
        ApiKey = null, // PAT not in the container by construction
        AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
        Models = ["claude-opus-4.8"],
    });

    /// <summary>
    /// Wires a refresh callback that mimics the Bridge round-trip: it counts invocations and
    /// returns <see cref="RefreshedBearer"/>. The agent applies it via UpdateOAuthTokens, so the
    /// retry's CreateHttpClient reads the refreshed bearer.
    /// </summary>
    private static (OAuthTokenManager Manager, Func<int> RefreshCount) BuildTokenManagerWithRefresh()
    {
        var count = 0;
        var manager = new OAuthTokenManager(NullLogger.Instance, metrics: null);
        manager.SetRequestTokenRefreshCallback(_ =>
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
        return (manager, () => Volatile.Read(ref count));
    }

    [Fact]
    public async Task CompleteAsync_GitHubCopilotBearer_401_RefreshesOnceAndRetriesWithFreshBearer()
    {
        var handler = new SequenceHandler(
            new HandlerStep(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}"""),
            new HandlerStep(HttpStatusCode.OK, CopilotResponseJson));
        var factory = new SingleClientFactory(handler);
        var (tokenManager, refreshCount) = BuildTokenManagerWithRefresh();
        using (tokenManager)
        {
            var client = new OpenAiCompatibleApiClient(factory, tokenManager, NullLogger.Instance);

            var result = await client.CompleteAsync(
                BuildCopilotBearerProvider(), BuildRequest(), CancellationToken.None);

            // Retry succeeded.
            Assert.True(result.Success);

            // Exactly two chat-completions attempts: the 401 then the 200 retry.
            Assert.Equal(2, handler.Requests.Count);

            // Exactly ONE Bridge refresh round-trip.
            Assert.Equal(1, refreshCount());

            // First attempt used the stale bearer; the retry used the refreshed one.
            Assert.Equal(StaleBearer, handler.Requests[0].Authorization?.Parameter);
            Assert.Equal(RefreshedBearer, handler.Requests[1].Authorization?.Parameter);

            // The PAT→token exchange endpoint was never hit.
            Assert.DoesNotContain(handler.Requests, r =>
                r.Url.Contains("copilot_internal", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task CompleteAsync_GitHubCopilotBearer_CallerCancellation_IsRethrownNotDegraded()
    {
        using var cts = new CancellationTokenSource();
        var handler = new SequenceHandler(
            // 401 triggers the refresh; the caller cancels mid-refresh so the retry send observes it.
            new HandlerStep(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}"""));
        var factory = new SingleClientFactory(handler);
        using var tokenManager = new OAuthTokenManager(NullLogger.Instance, metrics: null);
        tokenManager.SetRequestTokenRefreshCallback(_ =>
        {
            // The caller cancels while the Bridge refresh is in flight. The refresh itself
            // returns a fresh bearer, but the subsequent retry send must observe the cancelled
            // token and throw — that OCE is a caller-cancellation and must NOT be degraded.
            cts.Cancel();
            return Task.FromResult(new TokenRefreshResult
            {
                Success = true,
                AccessToken = RefreshedBearer,
                ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
            });
        });
        var client = new OpenAiCompatibleApiClient(factory, tokenManager, NullLogger.Instance);

        // The caller-cancellation must propagate, NOT be swallowed into a 401 error result.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CompleteAsync(BuildCopilotBearerProvider(), BuildRequest(), cts.Token));

        // The token exchange endpoint was never hit.
        Assert.DoesNotContain(handler.Requests, r =>
            r.Url.Contains("copilot_internal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StreamAsync_GitHubCopilotBearer_401_RefreshesOnceAndRetriesWithFreshBearer()
    {
        var handler = new SequenceHandler(
            new HandlerStep(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}"""),
            new HandlerStep(HttpStatusCode.OK, CopilotStreamSse));
        var factory = new SingleClientFactory(handler);
        var (tokenManager, refreshCount) = BuildTokenManagerWithRefresh();
        using (tokenManager)
        {
            var client = new OpenAiCompatibleApiClient(factory, tokenManager, NullLogger.Instance);

            var chunks = new List<LlmStreamChunk>();
            await foreach (var chunk in client.StreamAsync(
                BuildCopilotBearerProvider(), BuildRequest(), CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            // The stream completed without surfacing an error chunk (the retry succeeded).
            Assert.DoesNotContain(chunks, c => !string.IsNullOrEmpty(c.ErrorMessage));
            Assert.Contains(chunks, c => c.ContentDelta == "hi");

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(1, refreshCount());
            Assert.Equal(StaleBearer, handler.Requests[0].Authorization?.Parameter);
            Assert.Equal(RefreshedBearer, handler.Requests[1].Authorization?.Parameter);
            Assert.DoesNotContain(handler.Requests, r =>
                r.Url.Contains("copilot_internal", StringComparison.OrdinalIgnoreCase));
        }

        // No leaked/undisposed response across the retry branch: both responses were consumed
        // and disposed by the client; the SequenceHandler tracks every response it created.
        Assert.All(handler.CreatedResponses, r => Assert.True(r.IsDisposed));
    }

    [Fact]
    public async Task StreamAsync_GitHubCopilotBearer_CallerCancellation_IsRethrownNotDegraded()
    {
        using var cts = new CancellationTokenSource();
        var handler = new SequenceHandler(
            new HandlerStep(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}"""));
        var factory = new SingleClientFactory(handler);
        using var tokenManager = new OAuthTokenManager(NullLogger.Instance, metrics: null);
        tokenManager.SetRequestTokenRefreshCallback(_ =>
        {
            // Caller cancels mid-refresh; the retry send must observe it and throw an OCE
            // that the stream path re-throws (after disposing the retry client/response).
            cts.Cancel();
            return Task.FromResult(new TokenRefreshResult
            {
                Success = true,
                AccessToken = RefreshedBearer,
                ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
            });
        });
        var client = new OpenAiCompatibleApiClient(factory, tokenManager, NullLogger.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(
                BuildCopilotBearerProvider(), BuildRequest(), cts.Token))
            {
            }
        });

        Assert.DoesNotContain(handler.Requests, r =>
            r.Url.Contains("copilot_internal", StringComparison.OrdinalIgnoreCase));

        // The 401 response created before cancellation was disposed (no leak on the cancel path).
        Assert.All(handler.CreatedResponses, r => Assert.True(r.IsDisposed));
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler handler;

        public SingleClientFactory(HttpMessageHandler handler) => this.handler = handler;

        // Do not dispose the shared handler when the HttpClient is disposed.
        public HttpClient CreateClient(string name) => new(this.handler, disposeHandler: false);
    }

    /// <summary>An SSE stream body that yields one content delta then a stop + [DONE].</summary>
    private const string CopilotStreamSse =
        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    private sealed record HandlerStep(HttpStatusCode Status, string Body);

    /// <summary>Immutable snapshot of a sent request — captured before the client disposes it.</summary>
    private sealed record SentRequest(string Url, AuthenticationHeaderValue? Authorization);

    /// <summary>
    /// Returns a scripted sequence of responses (one per call), capturing each request's URL and
    /// Authorization header and tracking every <see cref="TrackedContent"/> it hands out so tests
    /// can assert responses were disposed (no leak across the retry branch).
    /// </summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly HandlerStep[] steps;
        private int index;

        public SequenceHandler(params HandlerStep[] steps) => this.steps = steps;

        public List<SentRequest> Requests { get; } = [];

        public List<TrackedContent> CreatedResponses { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Snapshot the request now — the client may dispose it before the test inspects it.
            this.Requests.Add(new SentRequest(
                request.RequestUri!.AbsoluteUri, request.Headers.Authorization));

            // Read the request body so cancellation requested during the call is observed
            // the same way the real handler would observe it.
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Content is not null)
            {
                await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            var step = this.steps[Math.Min(this.index, this.steps.Length - 1)];
            this.index++;

            var content = new TrackedContent(step.Body);
            this.CreatedResponses.Add(content);
            return new HttpResponseMessage(step.Status) { Content = content };
        }
    }

    /// <summary>String content that records when it is disposed, to detect leaked responses.</summary>
    private sealed class TrackedContent : StringContent
    {
        public TrackedContent(string body)
            : base(body, Encoding.UTF8, "application/json")
        {
        }

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            this.IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string responseBody;

        public CapturingHandler(string responseBody) => this.responseBody = responseBody;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(this.responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
