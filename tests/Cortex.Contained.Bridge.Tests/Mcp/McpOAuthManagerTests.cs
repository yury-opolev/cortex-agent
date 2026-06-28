using System.Net;
using System.Text;
using System.Web;
using Cortex.Contained.Bridge.Mcp.Auth;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpOAuthManagerTests
{
    private const string ServerUrl = "https://mcp.example.com/mcp";

    /// <summary>Routes requests by (method, path) to canned responses for the OAuth discovery chain.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> TokenGrantTypes { get; } = [];

        public string AccessToken { get; set; } = "access-token-1";

        public string RefreshToken { get; set; } = "refresh-token-1";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method;

            if (method == HttpMethod.Get && request.RequestUri.AbsoluteUri == ServerUrl)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    "Bearer resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource\"");
                return resp;
            }

            if (method == HttpMethod.Get && path == "/.well-known/oauth-protected-resource")
            {
                return Json("""{ "resource": "https://mcp.example.com", "authorization_servers": ["https://auth.example.com"] }""");
            }

            if (method == HttpMethod.Get && path == "/.well-known/oauth-authorization-server")
            {
                return Json("""
                {
                  "issuer": "https://auth.example.com",
                  "authorization_endpoint": "https://auth.example.com/authorize",
                  "token_endpoint": "https://auth.example.com/token",
                  "registration_endpoint": "https://auth.example.com/register",
                  "scopes_supported": ["mcp:tools"]
                }
                """);
            }

            if (method == HttpMethod.Post && path == "/register")
            {
                return Json("""{ "client_id": "dcr-client-id" }""");
            }

            if (method == HttpMethod.Post && path == "/token")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var form = HttpUtility.ParseQueryString(body);
                this.TokenGrantTypes.Add(form["grant_type"] ?? "");
                return Json($$"""
                {
                  "access_token": "{{this.AccessToken}}",
                  "refresh_token": "{{this.RefreshToken}}",
                  "token_type": "Bearer",
                  "expires_in": 3600,
                  "scope": "mcp:tools"
                }
                """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeSecretStore : IMcpTokenSecretStore
    {
        public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);

        public string? GetSecret(string secretId) => this.Entries.GetValueOrDefault(secretId);

        public void SetSecret(string secretId, string value) => this.Entries[secretId] = value;

        public void RemoveSecret(string secretId) => this.Entries.Remove(secretId);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => this.handler = handler;

        public HttpClient CreateClient(string name) => new(this.handler, disposeHandler: false);
    }

    private static McpServerConfig HttpOAuthServer()
        => new() { Key = "github", Transport = McpTransport.Http, Url = ServerUrl, Auth = McpAuthMode.OAuth };

    private static (McpOAuthManager manager, McpTokenStore tokens, FakeSecretStore secrets, FakeTimeProvider time, StubHandler handler) Build()
    {
        var handler = new StubHandler();
        var secrets = new FakeSecretStore();
        var tokens = new McpTokenStore(secrets, NullLogger<McpTokenStore>.Instance);
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000_000_000));
        var manager = new McpOAuthManager(
            new StubHttpClientFactory(handler),
            tokens,
            new McpOAuthOptions(),
            time,
            NullLogger<McpOAuthManager>.Instance);
        return (manager, tokens, secrets, time, handler);
    }

    [Fact]
    public async Task BuildAuthorizationUrlAsync_ProducesUrlWithPkceStateAndScopes()
    {
        var (manager, _, _, _, _) = Build();

        var start = await manager.BuildAuthorizationUrlAsync(HttpOAuthServer(), CancellationToken.None);

        var uri = new Uri(start.AuthorizationUrl);
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("https://auth.example.com/authorize", uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("dcr-client-id", query["client_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        Assert.Equal(start.State, query["state"]);
        Assert.Equal("mcp:tools", query["scope"]);
        Assert.Contains("/mcp/oauth/callback", query["redirect_uri"]);
    }

    [Fact]
    public async Task CompleteAsync_ValidState_ExchangesAndStoresTokens()
    {
        var (manager, tokens, _, _, handler) = Build();
        var start = await manager.BuildAuthorizationUrlAsync(HttpOAuthServer(), CancellationToken.None);

        var completion = await manager.CompleteAsync(start.State, "auth-code-123", CancellationToken.None);

        Assert.True(completion.Success);
        Assert.Equal("github", completion.ServerKey);
        Assert.Contains("authorization_code", handler.TokenGrantTypes);
        var stored = tokens.Get("github");
        Assert.NotNull(stored);
        Assert.Equal("access-token-1", stored!.AccessToken);
        Assert.Equal("refresh-token-1", stored.RefreshToken);
        Assert.Equal("dcr-client-id", stored.ClientId);
        Assert.Equal("https://auth.example.com/token", stored.TokenEndpoint);
    }

    [Fact]
    public async Task CompleteAsync_UnknownState_Fails()
    {
        var (manager, _, _, _, _) = Build();

        var completion = await manager.CompleteAsync("never-issued", "code", CancellationToken.None);

        Assert.False(completion.Success);
        Assert.NotNull(completion.Error);
    }

    [Fact]
    public async Task CompleteAsync_ReplayedState_FailsOnSecondUse()
    {
        var (manager, _, _, _, _) = Build();
        var start = await manager.BuildAuthorizationUrlAsync(HttpOAuthServer(), CancellationToken.None);

        var first = await manager.CompleteAsync(start.State, "code", CancellationToken.None);
        var second = await manager.CompleteAsync(start.State, "code", CancellationToken.None);

        Assert.True(first.Success);
        Assert.False(second.Success);
    }

    [Fact]
    public async Task CompleteAsync_ExpiredState_Fails()
    {
        var (manager, _, _, time, _) = Build();
        var start = await manager.BuildAuthorizationUrlAsync(HttpOAuthServer(), CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(30));
        var completion = await manager.CompleteAsync(start.State, "code", CancellationToken.None);

        Assert.False(completion.Success);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ValidToken_ReturnsItWithoutRefresh()
    {
        var (manager, tokens, _, time, handler) = Build();
        tokens.Save("github", new McpOAuthTokens
        {
            AccessToken = "still-valid",
            RefreshToken = "r",
            ClientId = "c",
            TokenEndpoint = "https://auth.example.com/token",
            ExpiresAtMs = time.GetUtcNow().ToUnixTimeMilliseconds() + 3_600_000,
        });

        var token = await manager.GetAccessTokenAsync(HttpOAuthServer(), CancellationToken.None);

        Assert.Equal("still-valid", token);
        Assert.Empty(handler.TokenGrantTypes);
    }

    [Fact]
    public async Task GetAccessTokenAsync_Expired_RefreshesAndReturnsNew()
    {
        var (manager, tokens, _, time, handler) = Build();
        handler.AccessToken = "refreshed-access";
        tokens.Save("github", new McpOAuthTokens
        {
            AccessToken = "stale",
            RefreshToken = "refresh-token-1",
            ClientId = "c",
            TokenEndpoint = "https://auth.example.com/token",
            ExpiresAtMs = time.GetUtcNow().ToUnixTimeMilliseconds() - 1000,
        });

        var token = await manager.GetAccessTokenAsync(HttpOAuthServer(), CancellationToken.None);

        Assert.Equal("refreshed-access", token);
        Assert.Contains("refresh_token", handler.TokenGrantTypes);
        Assert.Equal("refreshed-access", tokens.Get("github")!.AccessToken);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoTokens_ReturnsNull()
    {
        var (manager, _, _, _, _) = Build();

        var token = await manager.GetAccessTokenAsync(HttpOAuthServer(), CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public void HasTokens_ReflectsStore()
    {
        var (manager, tokens, _, _, _) = Build();
        Assert.False(manager.HasTokens(HttpOAuthServer()));

        tokens.Save("github", new McpOAuthTokens { AccessToken = "a", ClientId = "c", TokenEndpoint = "t" });

        Assert.True(manager.HasTokens(HttpOAuthServer()));
    }

    [Fact]
    public void ClearTokens_RemovesStoredTokens()
    {
        var (manager, tokens, secrets, _, _) = Build();
        tokens.Save("github", new McpOAuthTokens { AccessToken = "a", ClientId = "c", TokenEndpoint = "t" });
        Assert.True(manager.HasTokens(HttpOAuthServer()));

        manager.ClearTokens("GitHub"); // case-insensitive; server keys are lowercased

        Assert.False(manager.HasTokens(HttpOAuthServer()));
        Assert.Null(tokens.Get("github"));
        Assert.False(secrets.Entries.ContainsKey(McpTokenStore.SecretId("github")));
    }
}
