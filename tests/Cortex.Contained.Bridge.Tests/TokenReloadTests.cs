using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Common.Security;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests;

/// <summary>
/// Tests for the token reload logic used when an OAuth token is revoked (403)
/// by another process (e.g. evals rotating the token). The Bridge re-reads
/// secrets.json to pick up the fresher token instead of trying to refresh
/// (which would also fail since the refresh token was rotated too).
/// </summary>
public class TokenReloadTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemorySecretStore _store;
    private readonly SecretManager _secretManager;

    public TokenReloadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cortex-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new InMemorySecretStore();
        _secretManager = new SecretManager(_store, NullLogger<SecretManager>.Instance, _tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetApiKey_ReturnsNull_WhenNoSecretsExist()
    {
        var key = _secretManager.GetApiKey("anthropic");
        Assert.Null(key);
    }

    [Fact]
    public void GetApiKey_ReturnsSavedKey_AfterStoreApiKey()
    {
        _secretManager.StoreApiKey("anthropic", "sk-ant-test123");
        var key = _secretManager.GetApiKey("anthropic");
        Assert.Equal("sk-ant-test123", key);
    }

    [Fact]
    public void GetRefreshToken_ReturnsSavedToken_AfterStoreOAuthTokens()
    {
        _secretManager.StoreOAuthTokens("anthropic", "access-tok", "refresh-tok", 9999999);
        var refreshToken = _secretManager.GetRefreshToken("anthropic");
        Assert.Equal("refresh-tok", refreshToken);
    }

    [Fact]
    public void ReloadDetectsFresherToken_WhenAnotherProcessUpdatedSecrets()
    {
        // Simulate initial state: Bridge stores tokens at startup
        _secretManager.StoreOAuthTokens("anthropic", "old-access", "old-refresh", 1000);

        var provider = new LlmProviderConfig
        {
            Name = "anthropic",
            Api = "anthropic-messages",
            TokenType = "oauth",
            ApiKey = "old-access",
            RefreshToken = "old-refresh",
            TokenExpiresAt = 1000,
        };

        // Simulate another process (evals) updating secrets.json with a new token
        _secretManager.StoreOAuthTokens("anthropic", "new-access-from-evals", "new-refresh-from-evals", 9999);

        // Reload should detect the fresher token
        var reloaded = TryReloadTokenFromSecrets(provider);

        Assert.True(reloaded);
        Assert.Equal("new-access-from-evals", provider.ApiKey);
        Assert.Equal("new-refresh-from-evals", provider.RefreshToken);
    }

    [Fact]
    public void ReloadReturnsFalse_WhenTokenIsUnchanged()
    {
        _secretManager.StoreOAuthTokens("anthropic", "same-access", "same-refresh", 1000);

        var provider = new LlmProviderConfig
        {
            Name = "anthropic",
            Api = "anthropic-messages",
            TokenType = "oauth",
            ApiKey = "same-access",
            RefreshToken = "same-refresh",
            TokenExpiresAt = 1000,
        };

        // No external update — token in secrets is the same as in-memory
        var reloaded = TryReloadTokenFromSecrets(provider);

        Assert.False(reloaded);
        Assert.Equal("same-access", provider.ApiKey); // unchanged
    }

    [Fact]
    public void ReloadReturnsFalse_WhenNoSecretsExist()
    {
        var provider = new LlmProviderConfig
        {
            Name = "anthropic",
            Api = "anthropic-messages",
            ApiKey = "old-access",
        };

        var reloaded = TryReloadTokenFromSecrets(provider);

        Assert.False(reloaded);
        Assert.Equal("old-access", provider.ApiKey); // unchanged
    }

    [Fact]
    public void ReloadResetsExpiry_WhenFresherTokenFound()
    {
        _secretManager.StoreOAuthTokens("anthropic", "old-access", "old-refresh", 5000);

        var provider = new LlmProviderConfig
        {
            Name = "anthropic",
            Api = "anthropic-messages",
            TokenType = "oauth",
            ApiKey = "old-access",
            RefreshToken = "old-refresh",
            TokenExpiresAt = 5000,
        };

        // External update
        _secretManager.StoreOAuthTokens("anthropic", "fresh-access", "fresh-refresh", 99999);

        var reloaded = TryReloadTokenFromSecrets(provider);

        Assert.True(reloaded);
        // Expiry should be reset to 0 since we don't know when the new token was issued
        Assert.Equal(0, provider.TokenExpiresAt);
    }

    /// <summary>
    /// Mirrors the Worker.TryReloadTokenFromSecrets logic.
    /// </summary>
    private bool TryReloadTokenFromSecrets(LlmProviderConfig provider)
    {
        try
        {
            var apiKey = _secretManager.GetApiKey(provider.Name);
            var refreshToken = _secretManager.GetRefreshToken(provider.Name);

            if (string.IsNullOrEmpty(apiKey) || apiKey == provider.ApiKey)
            {
                return false;
            }

            provider.ApiKey = apiKey;
            if (!string.IsNullOrEmpty(refreshToken))
            {
                provider.RefreshToken = refreshToken;
            }

            provider.TokenExpiresAt = 0;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
