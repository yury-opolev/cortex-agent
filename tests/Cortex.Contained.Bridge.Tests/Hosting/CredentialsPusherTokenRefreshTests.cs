using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Tokens;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Hosting;

/// <summary>
/// Tests that <see cref="CredentialsPusher.HandleTokenRefreshRequestAsync"/> performs the
/// provider lookup (returning "Unknown provider" for misses) and otherwise delegates to
/// <see cref="TokenRefreshService"/>, returning the service's result unchanged. A real
/// <see cref="TokenRefreshService"/> with a fake strategy stands in for the refresh path —
/// router/HTTP deps are <c>null!</c> since this path doesn't touch them.
/// </summary>
public sealed class CredentialsPusherTokenRefreshTests : IDisposable
{
    private readonly string tempDir;
    private readonly SecretManager secretManager;

    public CredentialsPusherTokenRefreshTests()
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

    private TokenRefreshService BuildService(ITokenRefreshStrategy strategy)
    {
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        return new TokenRefreshService(
            [strategy], httpFactory, this.secretManager, NullLogger<TokenRefreshService>.Instance);
    }

    private static CredentialsPusher BuildPusher(BridgeConfig config, TokenRefreshService tokenRefreshService) =>
        new(
            tenantRouter: null!,
            config: config,
            httpClientFactory: null!,
            secretManager: null!,
            modelCatalog: null!,
            resolver: null!,
            tokenRefreshService: tokenRefreshService,
            logger: NullLogger<CredentialsPusher>.Instance);

    [Fact]
    public async Task HandleTokenRefreshRequest_UnknownProvider_ReturnsFailure()
    {
        var service = this.BuildService(new FakeStrategy { Handles = true });
        var pusher = BuildPusher(new BridgeConfig(), service);

        var result = await pusher.HandleTokenRefreshRequestAsync("nope", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unknown provider", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleTokenRefreshRequest_KnownAnthropicOAuthProvider_DelegatesToService()
    {
        var provider = new LlmProviderConfig
        {
            Name = "anthropic",
            Api = "anthropic-messages",
            TokenType = "oauth",
            ApiKey = "old-access",
            RefreshToken = "old-refresh",
        };
        var config = new BridgeConfig { LlmProviders = [provider] };

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
        var pusher = BuildPusher(config, service);

        var result = await pusher.HandleTokenRefreshRequestAsync("anthropic", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("fresh-access", result.AccessToken);
        Assert.Equal(1, strategy.CallCount);

        // Provider config mutated by the service (delegation reached it).
        Assert.Equal("fresh-access", provider.ApiKey);
    }

    private sealed class FakeStrategy : ITokenRefreshStrategy
    {
        public bool Handles { get; init; }
        public TokenRefreshOutcome? Outcome { get; init; }

        private int callCount;
        public int CallCount => Volatile.Read(ref this.callCount);

        public bool CanHandle(LlmProviderConfig provider) => this.Handles;

        public Task<TokenRefreshOutcome> RefreshAsync(
            LlmProviderConfig provider, HttpClient httpClient, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.callCount);
            return Task.FromResult(this.Outcome!);
        }
    }
}
