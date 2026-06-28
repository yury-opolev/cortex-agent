using Cortex.Contained.Bridge.Mcp.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpTokenStoreTests
{
    /// <summary>In-memory stand-in for the DPAPI-backed secret store.</summary>
    private sealed class FakeSecretStore : IMcpTokenSecretStore
    {
        public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);

        public string? GetSecret(string secretId) => this.Entries.GetValueOrDefault(secretId);

        public void SetSecret(string secretId, string value) => this.Entries[secretId] = value;

        public void RemoveSecret(string secretId) => this.Entries.Remove(secretId);
    }

    private static McpTokenStore Build(IMcpTokenSecretStore store)
        => new(store, NullLogger<McpTokenStore>.Instance);

    [Fact]
    public void SaveThenGet_RoundTripsAllFields()
    {
        var store = Build(new FakeSecretStore());
        var tokens = new McpOAuthTokens
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            ExpiresAtMs = 1_700_000_000_000,
            ClientId = "client-1",
            ClientSecret = "secret-1",
            TokenEndpoint = "https://auth.example.com/token",
            Scope = "mcp:tools",
        };

        store.Save("github", tokens);
        var loaded = store.Get("github");

        Assert.NotNull(loaded);
        Assert.Equal("access-1", loaded!.AccessToken);
        Assert.Equal("refresh-1", loaded.RefreshToken);
        Assert.Equal(1_700_000_000_000, loaded.ExpiresAtMs);
        Assert.Equal("client-1", loaded.ClientId);
        Assert.Equal("secret-1", loaded.ClientSecret);
        Assert.Equal("https://auth.example.com/token", loaded.TokenEndpoint);
        Assert.Equal("mcp:tools", loaded.Scope);
    }

    [Fact]
    public void Save_StoresUnderServerScopedSecretId()
    {
        var fake = new FakeSecretStore();
        Build(fake).Save("github", new McpOAuthTokens
        {
            AccessToken = "a",
            ClientId = "c",
            TokenEndpoint = "https://auth/token",
        });

        Assert.True(fake.Entries.ContainsKey("mcp/github/oauth"));
    }

    [Fact]
    public void Get_Absent_ReturnsNull()
    {
        var loaded = Build(new FakeSecretStore()).Get("missing");

        Assert.Null(loaded);
    }

    [Fact]
    public void Clear_RemovesEntry()
    {
        var fake = new FakeSecretStore();
        var store = Build(fake);
        store.Save("github", new McpOAuthTokens { AccessToken = "a", ClientId = "c", TokenEndpoint = "https://auth/token" });

        store.Clear("github");

        Assert.Null(store.Get("github"));
        Assert.False(fake.Entries.ContainsKey("mcp/github/oauth"));
    }

    [Fact]
    public void IsExpired_BeyondSkew_True()
    {
        var tokens = new McpOAuthTokens { AccessToken = "a", ClientId = "c", TokenEndpoint = "t", ExpiresAtMs = 1000 };

        // now = 1000, skew 60s → expiry (1000) <= now+skew → expired.
        Assert.True(tokens.IsExpired(nowUnixMs: 1000, skewMs: 60_000));
    }

    [Fact]
    public void IsExpired_WellInFuture_False()
    {
        var tokens = new McpOAuthTokens { AccessToken = "a", ClientId = "c", TokenEndpoint = "t", ExpiresAtMs = 10_000_000 };

        Assert.False(tokens.IsExpired(nowUnixMs: 1000, skewMs: 60_000));
    }

    [Fact]
    public void IsExpired_NoExpirySet_TreatedAsExpired()
    {
        var tokens = new McpOAuthTokens { AccessToken = "a", ClientId = "c", TokenEndpoint = "t", ExpiresAtMs = 0 };

        Assert.True(tokens.IsExpired(nowUnixMs: 1000, skewMs: 0));
    }
}
