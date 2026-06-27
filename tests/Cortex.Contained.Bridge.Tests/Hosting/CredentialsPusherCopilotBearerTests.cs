using Cortex.Contained.Bridge;
using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Tokens;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Hosting;

/// <summary>
/// Tests that <see cref="CredentialsPusher"/> mints and pushes a short-lived Copilot bearer
/// instead of the durable PAT. INVARIANT 2: the pushed credential for a Copilot provider has
/// <see cref="CredentialKind.GitHubCopilotBearer"/>, carries the minted bearer in
/// <c>AccessToken</c>+<c>AccessTokenExpiresAt</c>, and has <c>ApiKey == null</c> (the PAT never
/// enters the container). The credential build is exercised directly via the
/// <see cref="CredentialsPusher.BuildProviderCredentialsAsync"/> seam so the sealed
/// TenantRouter/HubClient fan-out is not required.
/// </summary>
public sealed class CredentialsPusherCopilotBearerTests : IDisposable
{
    private readonly string tempDir;
    private readonly SecretManager secretManager;

    public CredentialsPusherCopilotBearerTests()
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

    private CredentialsPusher BuildPusher(BridgeConfig config, TokenRefreshService tokenRefreshService)
    {
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        // A real (uninitialized) catalog enriches nothing — safe for the build path under test.
        var modelCatalog = new ModelCatalog(httpFactory, NullLogger<ModelCatalog>.Instance);
        return new CredentialsPusher(
            tenantRouter: null!,
            config: config,
            httpClientFactory: httpFactory,
            secretManager: this.secretManager,
            modelCatalog: modelCatalog,
            resolver: null!,
            tokenRefreshService: tokenRefreshService,
            logger: NullLogger<CredentialsPusher>.Instance);
    }

    [Fact]
    public void ResolveCredentialKind_CopilotPat_IsGitHubCopilotBearer()
    {
        var kind = CredentialsPusher.ResolveCredentialKind(new LlmProviderConfig
        {
            Name = "github-copilot",
            Api = "github-copilot-api",
            TokenType = "pat",
            ApiKey = "ghp_durable_pat",
        });

        Assert.Equal(CredentialKind.GitHubCopilotBearer, kind);
    }

    [Fact]
    public async Task BuildProviderCredentialsAsync_CopilotProvider_PushesMintedBearerNotPat()
    {
        var expiresAt = NowMs() + (15 * 60 * 1000);
        var strategy = new FakeStrategy
        {
            Handles = true,
            Outcome = new TokenRefreshOutcome
            {
                AccessToken = "minted-bearer-xyz",
                RefreshToken = null,
                ExpiresAtMs = expiresAt,
            },
        };
        var service = this.BuildService(strategy);

        var provider = new LlmProviderConfig
        {
            Name = "github-copilot",
            Api = "github-copilot-api",
            TokenType = "pat",
            ApiKey = "ghp_durable_pat",
            Models = ["claude-opus-4.8"],
        };
        var config = new BridgeConfig { LlmProviders = [provider] };
        var pusher = this.BuildPusher(config, service);

        var creds = await pusher.BuildProviderCredentialsAsync(CancellationToken.None);

        var copilot = Assert.Single(creds);
        Assert.Equal(CredentialKind.GitHubCopilotBearer, copilot.Kind);
        Assert.Equal("minted-bearer-xyz", copilot.AccessToken);
        Assert.Equal(expiresAt, copilot.AccessTokenExpiresAt);

        // INVARIANT 2: the agent never receives the PAT.
        Assert.Null(copilot.ApiKey);
        Assert.Null(copilot.RefreshToken);

        // INVARIANT 1: the durable PAT in the Bridge's provider config is untouched.
        Assert.Equal("ghp_durable_pat", provider.ApiKey);
    }

    [Fact]
    public async Task BuildProviderCredentialsAsync_CopilotMintFails_SkipsProviderGracefully()
    {
        var strategy = new FakeStrategy
        {
            Handles = true,
            Throw = new InvalidOperationException("GitHub offline"),
        };
        var service = this.BuildService(strategy);

        var provider = new LlmProviderConfig
        {
            Name = "github-copilot",
            Api = "github-copilot-api",
            TokenType = "pat",
            ApiKey = "ghp_durable_pat",
            Models = ["claude-opus-4.8"],
        };
        var config = new BridgeConfig { LlmProviders = [provider] };
        var pusher = this.BuildPusher(config, service);

        // A failed mint must not crash the whole push — the provider is skipped.
        var creds = await pusher.BuildProviderCredentialsAsync(CancellationToken.None);

        Assert.Empty(creds);
        // PAT still safe on the Bridge.
        Assert.Equal("ghp_durable_pat", provider.ApiKey);
    }

    private sealed class FakeStrategy : ITokenRefreshStrategy
    {
        public bool Handles { get; init; }
        public TokenRefreshOutcome? Outcome { get; init; }
        public Exception? Throw { get; init; }

        public bool CanHandle(LlmProviderConfig provider) => this.Handles;

        public Task<TokenRefreshOutcome> RefreshAsync(
            LlmProviderConfig provider, HttpClient httpClient, CancellationToken cancellationToken)
        {
            if (this.Throw is not null)
            {
                throw this.Throw;
            }
            return Task.FromResult(this.Outcome!);
        }
    }
}
