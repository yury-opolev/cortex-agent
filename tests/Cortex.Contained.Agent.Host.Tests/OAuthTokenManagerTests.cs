using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Focused unit tests for <see cref="OAuthTokenManager"/>, the centralised token-refresh
/// state machine extracted from <see cref="DirectLlmClient"/>. The proactive-expiry guard
/// (<see cref="OAuthTokenManager.EnsureFreshTokenAsync"/>) and the on-401 force-refresh
/// (<see cref="OAuthTokenManager.ForceRefreshAsync"/>) are kind-agnostic: they drive the same
/// single-flight + Bridge round-trip for both <see cref="CredentialKind.AnthropicOAuth"/> and
/// <see cref="CredentialKind.GitHubCopilotBearer"/>. These cover both kinds using a fake refresh
/// callback — no HTTP is involved on the refresh path.
/// </summary>
public class OAuthTokenManagerTests
{
    private static OAuthTokenManager CreateManager() =>
        new(
            NullLogger.Instance,
            metrics: null);

    private static ProviderState CreateOAuthProvider(long expiresAtMs) =>
        new(new LlmProviderCredential
        {
            Name = "anthropic",
            Api = "anthropic-messages",
            Kind = CredentialKind.AnthropicOAuth,
            AccessToken = "old-access",
            RefreshToken = "old-refresh",
            AccessTokenExpiresAt = expiresAtMs,
            Models = ["claude-x"],
        });

    private static ProviderState CreateCopilotBearerProvider(long expiresAtMs) =>
        new(new LlmProviderCredential
        {
            Name = "github-copilot",
            Api = "github-copilot-api",
            Kind = CredentialKind.GitHubCopilotBearer,
            AccessToken = "old-bearer",
            // Copilot bearer carries no rotating refresh token — the Bridge re-mints from the PAT.
            RefreshToken = null,
            AccessTokenExpiresAt = expiresAtMs,
            Models = ["claude-opus-4.8"],
        });

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ── Anthropic OAuth ─────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureFreshTokenAsync_AnthropicFresh_DoesNotInvokeCallback()
    {
        var manager = CreateManager();
        // Expires far in the future → well outside the 5-minute refresh buffer.
        var provider = CreateOAuthProvider(NowMs() + (60 * 60 * 1000));

        var callCount = 0;
        manager.SetRequestTokenRefreshCallback(_ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new TokenRefreshResult { Success = true, AccessToken = "new" });
        });

        await manager.EnsureFreshTokenAsync(provider, CancellationToken.None);

        Assert.Equal(0, callCount);
        Assert.Equal("old-access", provider.CurrentAccessToken);
    }

    [Fact]
    public async Task EnsureFreshTokenAsync_AnthropicExpiring_RefreshesAndAppliesNewTokens()
    {
        var manager = CreateManager();
        // Already expired → inside the refresh buffer.
        var provider = CreateOAuthProvider(NowMs() - 1000);

        manager.SetRequestTokenRefreshCallback(_ =>
            Task.FromResult(new TokenRefreshResult
            {
                Success = true,
                AccessToken = "fresh-access",
                RefreshToken = "fresh-refresh",
                ExpiresAtMs = NowMs() + (60 * 60 * 1000),
            }));

        await manager.EnsureFreshTokenAsync(provider, CancellationToken.None);

        Assert.Equal("fresh-access", provider.CurrentAccessToken);
        Assert.Equal("fresh-refresh", provider.CurrentRefreshToken);
    }

    [Fact]
    public async Task EnsureFreshTokenAsync_ConcurrentCallers_RefreshHappensOnce()
    {
        var manager = CreateManager();
        var provider = CreateOAuthProvider(NowMs() - 1000);

        var callCount = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        manager.SetRequestTokenRefreshCallback(async _ =>
        {
            Interlocked.Increment(ref callCount);
            // Hold the first caller inside the lock while the others pile up.
            await gate.Task.ConfigureAwait(false);
            return new TokenRefreshResult
            {
                Success = true,
                AccessToken = "fresh-access",
                // Refresh well past the buffer so the post-lock re-check short-circuits
                // every queued caller after the first.
                ExpiresAtMs = NowMs() + (60 * 60 * 1000),
            };
        });

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => manager.EnsureFreshTokenAsync(provider, CancellationToken.None))
            .ToArray();

        // Let the queued callers reach the lock, then release the in-flight refresh.
        gate.SetResult(true);
        await Task.WhenAll(tasks);

        // Single-flight: rotating refresh tokens are single-use, so exactly one
        // refresh signal must be sent even under concurrent load.
        Assert.Equal(1, callCount);
        Assert.Equal("fresh-access", provider.CurrentAccessToken);
    }

    [Fact]
    public async Task EnsureFreshTokenAsync_NonOAuthKind_NoOp()
    {
        var manager = CreateManager();
        var provider = new ProviderState(new LlmProviderCredential
        {
            Name = "openai",
            Api = "openai-completions",
            Kind = CredentialKind.ApiKey,
            ApiKey = "sk-test",
            Models = ["gpt-x"],
        });

        var callCount = 0;
        manager.SetRequestTokenRefreshCallback(_ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new TokenRefreshResult { Success = true });
        });

        await manager.EnsureFreshTokenAsync(provider, CancellationToken.None);

        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task EnsureFreshTokenAsync_NoCallback_LeavesTokenUnchanged()
    {
        var manager = CreateManager();
        var provider = CreateOAuthProvider(NowMs() - 1000);

        // No callback wired (Bridge disconnected) → proceed with stale token.
        await manager.EnsureFreshTokenAsync(provider, CancellationToken.None);

        Assert.Equal("old-access", provider.CurrentAccessToken);
    }

    // ── GitHub Copilot bearer ────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureFreshTokenAsync_CopilotFresh_DoesNotInvokeCallback()
    {
        var manager = CreateManager();
        var provider = CreateCopilotBearerProvider(NowMs() + (60 * 60 * 1000));

        var callCount = 0;
        manager.SetRequestTokenRefreshCallback(_ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new TokenRefreshResult { Success = true, AccessToken = "new" });
        });

        await manager.EnsureFreshTokenAsync(provider, CancellationToken.None);

        Assert.Equal(0, callCount);
        Assert.Equal("old-bearer", provider.CurrentAccessToken);
    }

    [Fact]
    public async Task EnsureFreshTokenAsync_CopilotExpiring_RefreshesAndAppliesNewBearer()
    {
        var manager = CreateManager();
        // Already expired → inside the refresh buffer.
        var provider = CreateCopilotBearerProvider(NowMs() - 1000);

        var sawProvider = (string?)null;
        manager.SetRequestTokenRefreshCallback(name =>
        {
            sawProvider = name;
            return Task.FromResult(new TokenRefreshResult
            {
                Success = true,
                AccessToken = "fresh-bearer",
                // A Copilot refresh has no rotating refresh token; the Bridge re-mints.
                RefreshToken = null,
                ExpiresAtMs = NowMs() + (20 * 60 * 1000),
            });
        });

        await manager.EnsureFreshTokenAsync(provider, CancellationToken.None);

        Assert.Equal("github-copilot", sawProvider);
        Assert.Equal("fresh-bearer", provider.CurrentAccessToken);
        // Null rotating refresh token is fine for Copilot — left unchanged (still null).
        Assert.Null(provider.CurrentRefreshToken);
    }

    [Fact]
    public async Task EnsureFreshTokenAsync_CopilotConcurrentCallers_RefreshHappensOnce()
    {
        var manager = CreateManager();
        var provider = CreateCopilotBearerProvider(NowMs() - 1000);

        var callCount = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        manager.SetRequestTokenRefreshCallback(async _ =>
        {
            Interlocked.Increment(ref callCount);
            await gate.Task.ConfigureAwait(false);
            return new TokenRefreshResult
            {
                Success = true,
                AccessToken = "fresh-bearer",
                ExpiresAtMs = NowMs() + (60 * 60 * 1000),
            };
        });

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => manager.EnsureFreshTokenAsync(provider, CancellationToken.None))
            .ToArray();

        gate.SetResult(true);
        await Task.WhenAll(tasks);

        Assert.Equal(1, callCount);
        Assert.Equal("fresh-bearer", provider.CurrentAccessToken);
    }

    [Fact]
    public async Task EnsureFreshTokenAsync_CopilotNoCallback_LeavesBearerUnchanged()
    {
        var manager = CreateManager();
        var provider = CreateCopilotBearerProvider(NowMs() - 1000);

        // No callback wired (Bridge disconnected) → proceed with stale bearer.
        await manager.EnsureFreshTokenAsync(provider, CancellationToken.None);

        Assert.Equal("old-bearer", provider.CurrentAccessToken);
    }

    [Fact]
    public async Task ForceRefreshAsync_CopilotExpiresAndRefreshes()
    {
        var manager = CreateManager();
        // A still-valid bearer that just hit a 401 (server-side rotation): the force path
        // must expire it and drive a refresh even though the proactive guard wouldn't fire.
        var provider = CreateCopilotBearerProvider(NowMs() + (60 * 60 * 1000));

        var callCount = 0;
        manager.SetRequestTokenRefreshCallback(_ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new TokenRefreshResult
            {
                Success = true,
                AccessToken = "fresh-bearer",
                ExpiresAtMs = NowMs() + (20 * 60 * 1000),
            });
        });

        await manager.ForceRefreshAsync(provider, CancellationToken.None);

        Assert.Equal(1, callCount);
        Assert.Equal("fresh-bearer", provider.CurrentAccessToken);
    }
}
