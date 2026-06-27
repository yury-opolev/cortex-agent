using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Tokens;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Tokens;

/// <summary>
/// Unit tests for <see cref="TokenRefreshBackgroundService"/>, driving its
/// <c>RunSweepAsync</c> seam directly (the timed loop in <c>ExecuteAsync</c> is not exercised).
/// A real <see cref="TokenRefreshService"/> is wired with FAKE strategies so the sweep's
/// "is this provider due for refresh?" gating and the "push once if anything refreshed"
/// behaviour are pinned without HTTP or SignalR.
/// </summary>
public sealed class TokenRefreshBackgroundServiceTests : IDisposable
{
    private readonly string tempDir;
    private readonly SecretManager secretManager;

    public TokenRefreshBackgroundServiceTests()
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

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunSweepAsync_ProviderWithinBuffer_RefreshesAndPushesOnce()
    {
        // Expiry inside the 5-minute ahead-of-expiry buffer ⇒ due for refresh.
        var provider = OAuthProvider("anthropic", expiresAtMs: NowMs() + 60_000);
        var strategy = new FakeStrategy { Handles = p => p.Name == "anthropic" };
        var repush = new FakeReplisher();
        var sut = this.BuildService([provider], strategy, repush);

        var refreshed = await sut.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, refreshed);
        Assert.Equal(1, strategy.CallCount);
        Assert.Equal(1, repush.PushCount);
    }

    [Fact]
    public async Task RunSweepAsync_ProviderFarFromExpiry_DoesNotRefreshOrPush()
    {
        // Expiry well beyond the buffer ⇒ not due.
        var provider = OAuthProvider("anthropic", expiresAtMs: NowMs() + (24L * 60 * 60 * 1000));
        var strategy = new FakeStrategy { Handles = _ => true };
        var repush = new FakeReplisher();
        var sut = this.BuildService([provider], strategy, repush);

        var refreshed = await sut.RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, refreshed);
        Assert.Equal(0, strategy.CallCount);
        Assert.Equal(0, repush.PushCount);
    }

    [Fact]
    public async Task RunSweepAsync_ExpiresAtZero_NeverRefreshed()
    {
        // TokenExpiresAt == 0 ⇒ unknown/non-expiring (e.g. Copilot PAT in Phase 2) ⇒ skipped.
        var provider = OAuthProvider("github-copilot-api", expiresAtMs: 0);
        var strategy = new FakeStrategy { Handles = _ => true };
        var repush = new FakeReplisher();
        var sut = this.BuildService([provider], strategy, repush);

        var refreshed = await sut.RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, refreshed);
        Assert.Equal(0, strategy.CallCount);
        Assert.Equal(0, repush.PushCount);
    }

    [Fact]
    public async Task RunSweepAsync_OneProviderThrows_OthersStillProcessedAndSweepDoesNotThrow()
    {
        var failing = OAuthProvider("anthropic-bad", expiresAtMs: NowMs() + 60_000);
        var healthy = OAuthProvider("anthropic-good", expiresAtMs: NowMs() + 60_000);

        // Distinct strategies so one can throw while the other succeeds; the service
        // picks the FIRST matching strategy per provider.
        var failStrategy = new FakeStrategy
        {
            Handles = p => p.Name == "anthropic-bad",
            Throw = new InvalidOperationException("refresh boom"),
        };
        var goodStrategy = new FakeStrategy { Handles = p => p.Name == "anthropic-good" };
        var repush = new FakeReplisher();
        var sut = this.BuildService([failing, healthy], repush, failStrategy, goodStrategy);

        var refreshed = await sut.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, refreshed);
        Assert.Equal(1, goodStrategy.CallCount);
        Assert.Equal(1, repush.PushCount);
    }

    [Fact]
    public async Task RunSweepAsync_MultipleProvidersRefreshed_PushesExactlyOnce()
    {
        var a = OAuthProvider("anthropic-a", expiresAtMs: NowMs() + 60_000);
        var b = OAuthProvider("anthropic-b", expiresAtMs: NowMs() + 60_000);
        var strategy = new FakeStrategy { Handles = _ => true };
        var repush = new FakeReplisher();
        var sut = this.BuildService([a, b], strategy, repush);

        var refreshed = await sut.RunSweepAsync(CancellationToken.None);

        Assert.Equal(2, refreshed);
        Assert.Equal(2, strategy.CallCount);
        Assert.Equal(1, repush.PushCount);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static LlmProviderConfig OAuthProvider(string name, long expiresAtMs) =>
        new()
        {
            Name = name,
            Api = "anthropic-messages",
            TokenType = "oauth",
            ApiKey = "old-access",
            RefreshToken = "old-refresh",
            TokenExpiresAt = expiresAtMs,
        };

    private TokenRefreshBackgroundService BuildService(
        IReadOnlyList<LlmProviderConfig> providers,
        FakeStrategy strategy,
        FakeReplisher repush) =>
        this.BuildService(providers, repush, strategy);

    private TokenRefreshBackgroundService BuildService(
        IReadOnlyList<LlmProviderConfig> providers,
        FakeReplisher repush,
        params ITokenRefreshStrategy[] strategies)
    {
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        var refreshService = new TokenRefreshService(
            strategies, httpFactory, this.secretManager, NullLogger<TokenRefreshService>.Instance);

        var config = new BridgeConfig();
        config.LlmProviders.AddRange(providers);

        return new TokenRefreshBackgroundService(
            config, refreshService, repush, NullLogger<TokenRefreshBackgroundService>.Instance);
    }

    private sealed class FakeStrategy : ITokenRefreshStrategy
    {
        public Func<LlmProviderConfig, bool> Handles { get; init; } = _ => false;
        public Exception? Throw { get; init; }

        private int callCount;
        public int CallCount => Volatile.Read(ref this.callCount);

        public bool CanHandle(LlmProviderConfig provider) => this.Handles(provider);

        public Task<TokenRefreshOutcome> RefreshAsync(
            LlmProviderConfig provider, HttpClient httpClient, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.callCount);
            if (this.Throw is not null)
            {
                throw this.Throw;
            }

            return Task.FromResult(new TokenRefreshOutcome
            {
                AccessToken = "fresh-access",
                RefreshToken = "fresh-refresh",
                ExpiresAtMs = NowMs() + (60 * 60 * 1000),
            });
        }
    }

    private sealed class FakeReplisher : ICredentialReplisher
    {
        private int pushCount;
        public int PushCount => Volatile.Read(ref this.pushCount);

        public Task PushCredentialsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.pushCount);
            return Task.CompletedTask;
        }
    }
}
