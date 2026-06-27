using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Tokens;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Tokens;

/// <summary>
/// Unit tests for <see cref="TokenRefreshService"/> — the owner of the per-provider
/// refresh cache, single-flight lock, strategy dispatch, persistence, and the
/// reload-from-secrets fallback. Uses FAKE <see cref="ITokenRefreshStrategy"/> stubs so
/// no HTTP is involved; the HttpClientFactory is a substitute. These pin the behaviour
/// ported verbatim from the old inline block in <c>CredentialsPusher</c>.
/// </summary>
public sealed class TokenRefreshServiceTests : IDisposable
{
    private readonly string tempDir;
    private readonly SecretManager secretManager;

    public TokenRefreshServiceTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"cortex-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.secretManager = new SecretManager(
            new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* cleanup */ }
        GC.SuppressFinalize(this);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static LlmProviderConfig OAuthProvider(long expiresAtMs = 0) =>
        new()
        {
            Name = "anthropic",
            Api = "anthropic-messages",
            TokenType = "oauth",
            ApiKey = "old-access",
            RefreshToken = "old-refresh",
            TokenExpiresAt = expiresAtMs,
        };

    private static LlmProviderConfig CopilotPatProvider(long expiresAtMs = 0) =>
        new()
        {
            Name = "github-copilot",
            Api = "github-copilot-api",
            TokenType = "pat",
            // ApiKey holds the durable PAT used for the next exchange — must never be overwritten.
            ApiKey = "ghp_durable_pat",
            RefreshToken = null,
            TokenExpiresAt = expiresAtMs,
        };

    private TokenRefreshService BuildService(params ITokenRefreshStrategy[] strategies)
    {
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        return new TokenRefreshService(
            strategies,
            httpFactory,
            this.secretManager,
            NullLogger<TokenRefreshService>.Instance);
    }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_DispatchesToMatchingStrategy()
    {
        var strategy = new FakeStrategy
        {
            Handles = true,
            Outcome = new TokenRefreshOutcome
            {
                AccessToken = "fresh-access",
                RefreshToken = "fresh-refresh",
                ExpiresAtMs = NowMs() + (60 * 60 * 1000),
            },
        };
        var service = this.BuildService(strategy);

        var result = await service.RefreshAsync(OAuthProvider(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("fresh-access", result.AccessToken);
        Assert.Equal("fresh-refresh", result.RefreshToken);
        Assert.Equal(1, strategy.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_NoMatchingStrategy_FailsWithoutCallingAnyStrategy()
    {
        var strategy = new FakeStrategy { Handles = false };
        var service = this.BuildService(strategy);

        var result = await service.RefreshAsync(OAuthProvider(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal(0, strategy.CallCount);
    }

    // ── Single-flight ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_ConcurrentCallers_StrategyInvokedOnce()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var strategy = new FakeStrategy
        {
            Handles = true,
            // Hold the first caller inside the lock while the others pile up; refresh
            // well past the 60s buffer so every queued caller short-circuits on the cache.
            BeforeReturn = async () => await gate.Task.ConfigureAwait(false),
            Outcome = new TokenRefreshOutcome
            {
                AccessToken = "fresh-access",
                RefreshToken = "fresh-refresh",
                ExpiresAtMs = NowMs() + (60 * 60 * 1000),
            },
        };
        var service = this.BuildService(strategy);
        var provider = OAuthProvider();

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => service.RefreshAsync(provider, CancellationToken.None))
            .ToArray();

        gate.SetResult(true);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, strategy.CallCount);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.All(results, r => Assert.Equal("fresh-access", r.AccessToken));
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_CacheHitWithinBuffer_DoesNotReCallStrategy()
    {
        var strategy = new FakeStrategy
        {
            Handles = true,
            Outcome = new TokenRefreshOutcome
            {
                AccessToken = "fresh-access",
                RefreshToken = "fresh-refresh",
                ExpiresAtMs = NowMs() + (60 * 60 * 1000),
            },
        };
        var service = this.BuildService(strategy);
        var provider = OAuthProvider();

        await service.RefreshAsync(provider, CancellationToken.None);
        await service.RefreshAsync(provider, CancellationToken.None);

        Assert.Equal(1, strategy.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_CachedResultPastExpiry_ReCallsStrategy()
    {
        var strategy = new FakeStrategy
        {
            Handles = true,
            // Expires inside the 60s buffer ⇒ the cache is not considered valid.
            Outcome = new TokenRefreshOutcome
            {
                AccessToken = "fresh-access",
                RefreshToken = "fresh-refresh",
                ExpiresAtMs = NowMs() + 1000,
            },
        };
        var service = this.BuildService(strategy);
        var provider = OAuthProvider();

        await service.RefreshAsync(provider, CancellationToken.None);
        await service.RefreshAsync(provider, CancellationToken.None);

        Assert.Equal(2, strategy.CallCount);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_Success_PersistsTokensAndMutatesProvider()
    {
        var expiresAt = NowMs() + (60 * 60 * 1000);
        var strategy = new FakeStrategy
        {
            Handles = true,
            Outcome = new TokenRefreshOutcome
            {
                AccessToken = "fresh-access",
                RefreshToken = "fresh-refresh",
                ExpiresAtMs = expiresAt,
            },
        };
        var service = this.BuildService(strategy);
        var provider = OAuthProvider();

        var result = await service.RefreshAsync(provider, CancellationToken.None);

        Assert.True(result.Success);

        // Persisted to secrets store.
        Assert.Equal("fresh-access", this.secretManager.GetApiKey("anthropic"));
        Assert.Equal("fresh-refresh", this.secretManager.GetRefreshToken("anthropic"));
        Assert.Equal(expiresAt, this.secretManager.GetTokenExpiry("anthropic"));

        // In-memory config mutated.
        Assert.Equal("fresh-access", provider.ApiKey);
        Assert.Equal("fresh-refresh", provider.RefreshToken);
        Assert.Equal(expiresAt, provider.TokenExpiresAt);
    }

    // ── Copilot (null refresh token) — INVARIANT 1: PAT stays on the Bridge ─────

    [Fact]
    public async Task RefreshAsync_CopilotNullRefreshToken_DoesNotPersistAndPreservesPat()
    {
        var expiresAt = NowMs() + (15 * 60 * 1000);
        var strategy = new FakeStrategy
        {
            Handles = true,
            Outcome = new TokenRefreshOutcome
            {
                AccessToken = "minted-bearer",
                // Copilot has no rotating refresh token — it is re-minted from the PAT.
                RefreshToken = null,
                ExpiresAtMs = expiresAt,
            },
        };
        var service = this.BuildService(strategy);
        var provider = CopilotPatProvider();

        var result = await service.RefreshAsync(provider, CancellationToken.None);

        // The minted bearer is returned (the push will read result.AccessToken).
        Assert.True(result.Success);
        Assert.Equal("minted-bearer", result.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Equal(expiresAt, result.ExpiresAtMs);

        // INVARIANT 1a: StoreOAuthTokens was NOT called — nothing persisted for this provider.
        Assert.Null(this.secretManager.GetApiKey("github-copilot"));
        Assert.Null(this.secretManager.GetRefreshToken("github-copilot"));

        // INVARIANT 1b: the durable PAT in provider.ApiKey was NOT overwritten by the bearer.
        Assert.Equal("ghp_durable_pat", provider.ApiKey);
        // RefreshToken stays null; only the bearer expiry is tracked for the proactive sweep.
        Assert.Null(provider.RefreshToken);
        Assert.Equal(expiresAt, provider.TokenExpiresAt);
    }

    [Fact]
    public async Task RefreshAsync_CopilotNullRefreshToken_CachesMintedBearer()
    {
        var strategy = new FakeStrategy
        {
            Handles = true,
            Outcome = new TokenRefreshOutcome
            {
                AccessToken = "minted-bearer",
                RefreshToken = null,
                ExpiresAtMs = NowMs() + (15 * 60 * 1000),
            },
        };
        var service = this.BuildService(strategy);
        var provider = CopilotPatProvider();

        await service.RefreshAsync(provider, CancellationToken.None);
        var second = await service.RefreshAsync(provider, CancellationToken.None);

        // Single-flight cache: the bearer is cached and served without re-exchanging.
        Assert.Equal("minted-bearer", second.AccessToken);
        Assert.Equal(1, strategy.CallCount);
    }

    // ── Reload fallback ───────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_StrategyThrowsButFresherSecretExists_SucceedsViaReload()
    {
        // A fresher token was written to secrets by another process.
        this.secretManager.StoreOAuthTokens("anthropic", "reloaded-access", "reloaded-refresh", 0);

        var strategy = new FakeStrategy
        {
            Handles = true,
            Throw = new InvalidOperationException("invalid_grant"),
        };
        var service = this.BuildService(strategy);
        var provider = OAuthProvider();

        var result = await service.RefreshAsync(provider, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("reloaded-access", result.AccessToken);
        Assert.Equal("reloaded-access", provider.ApiKey);
    }

    [Fact]
    public async Task RefreshAsync_StrategyThrowsAndNoFresherSecret_Fails()
    {
        var strategy = new FakeStrategy
        {
            Handles = true,
            Throw = new InvalidOperationException("invalid_grant"),
        };
        var service = this.BuildService(strategy);
        var provider = OAuthProvider();

        var result = await service.RefreshAsync(provider, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_grant", result.Error);
    }

    // ── C1: the reload-from-secrets fallback must NEVER leak the durable PAT ─────
    // For a no-rotating-refresh-token scheme (Copilot PAT exchange) a strategy
    // failure is transient and must surface as a clean failure — never the PAT read
    // back from secrets and handed out as the "bearer".

    [Fact]
    public async Task RefreshAsync_CopilotStrategyThrows_NeverReloadsPatAsBearer()
    {
        // A different PAT sits in secrets (e.g. the durable PAT, rotated by setup).
        // The reload fallback, if it ran, would read this and try to return it as the bearer.
        const string durablePat = "ghp_durable_pat";
        const string differentStoredPat = "ghp_different_stored_pat";
        this.secretManager.StoreApiKey("github-copilot", differentStoredPat);

        var strategy = new FakeStrategy
        {
            Handles = true,
            Throw = new InvalidOperationException("Copilot token exchange failed: HTTP 503"),
        };
        var service = this.BuildService(strategy);
        var provider = CopilotPatProvider();
        provider.ApiKey = durablePat;

        var result = await service.RefreshAsync(provider, CancellationToken.None);

        // Clean transient failure — NOT a success carrying a leaked credential.
        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));

        // The PAT must never be returned as the (bearer) access token.
        Assert.Null(result.AccessToken);
        Assert.NotEqual(durablePat, result.AccessToken);
        Assert.NotEqual(differentStoredPat, result.AccessToken);

        // Nothing was persisted for this provider (StoreOAuthTokens never called).
        Assert.Null(this.secretManager.GetRefreshToken("github-copilot"));

        // The in-memory durable PAT was not overwritten by the reloaded secret.
        Assert.Equal(durablePat, provider.ApiKey);
    }

    private sealed class FakeStrategy : ITokenRefreshStrategy
    {
        public bool Handles { get; init; }
        public TokenRefreshOutcome? Outcome { get; init; }
        public Exception? Throw { get; init; }
        public Func<Task>? BeforeReturn { get; init; }

        private int callCount;
        public int CallCount => Volatile.Read(ref this.callCount);

        public bool CanHandle(LlmProviderConfig provider) => this.Handles;

        public async Task<TokenRefreshOutcome> RefreshAsync(
            LlmProviderConfig provider, HttpClient httpClient, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.callCount);
            if (this.BeforeReturn is not null)
            {
                await this.BeforeReturn().ConfigureAwait(false);
            }
            if (this.Throw is not null)
            {
                throw this.Throw;
            }
            return this.Outcome!;
        }
    }
}
