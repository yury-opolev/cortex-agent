using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests that <see cref="ProviderState"/> seeds its mutable OAuth token fields uniformly for
/// the kinds that drive the Bridge-side refresh round-trip — <see cref="CredentialKind.AnthropicOAuth"/>
/// and <see cref="CredentialKind.GitHubCopilotBearer"/>. For Copilot, the durable PAT never reaches
/// the container: the agent receives only a Bridge-minted bearer in <c>AccessToken</c>/<c>AccessTokenExpiresAt</c>
/// (no refresh token), and the proactive-expiry guard + <see cref="ProviderState.UpdateOAuthTokens"/>
/// then work identically for both kinds.
/// </summary>
public class ProviderStateTests
{
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void Ctor_GitHubCopilotBearer_SeedsAccessTokenAndExpiry()
    {
        var expiresAt = NowMs() + (15 * 60 * 1000);
        var state = new ProviderState(new LlmProviderCredential
        {
            Name = "github-copilot",
            Api = "github-copilot-api",
            Kind = CredentialKind.GitHubCopilotBearer,
            AccessToken = "minted-bearer",
            AccessTokenExpiresAt = expiresAt,
            Models = ["claude-opus-4.8"],
        });

        Assert.Equal("minted-bearer", state.CurrentAccessToken);
        Assert.Equal(expiresAt, state.CurrentAccessTokenExpiresAtMs);
        // Copilot bearer has no rotating refresh token — the Bridge re-mints from the PAT.
        Assert.Null(state.CurrentRefreshToken);
    }

    [Fact]
    public void Ctor_AnthropicOAuth_SeedsAllOAuthFields()
    {
        var expiresAt = NowMs() + (60 * 60 * 1000);
        var state = new ProviderState(new LlmProviderCredential
        {
            Name = "anthropic",
            Api = "anthropic-messages",
            Kind = CredentialKind.AnthropicOAuth,
            AccessToken = "access",
            RefreshToken = "refresh",
            AccessTokenExpiresAt = expiresAt,
            Models = ["claude-x"],
        });

        Assert.Equal("access", state.CurrentAccessToken);
        Assert.Equal("refresh", state.CurrentRefreshToken);
        Assert.Equal(expiresAt, state.CurrentAccessTokenExpiresAtMs);
    }

    [Fact]
    public void UpdateOAuthTokens_GitHubCopilotBearer_UpdatesBearerInPlace()
    {
        var state = new ProviderState(new LlmProviderCredential
        {
            Name = "github-copilot",
            Api = "github-copilot-api",
            Kind = CredentialKind.GitHubCopilotBearer,
            AccessToken = "old-bearer",
            AccessTokenExpiresAt = NowMs() - 1000,
            Models = ["claude-opus-4.8"],
        });

        var newExpiry = NowMs() + (20 * 60 * 1000);
        state.UpdateOAuthTokens("new-bearer", refreshToken: null, newExpiry);

        Assert.Equal("new-bearer", state.CurrentAccessToken);
        Assert.Equal(newExpiry, state.CurrentAccessTokenExpiresAtMs);
        Assert.Null(state.CurrentRefreshToken);
    }

    [Fact]
    public void FindModelMetadata_ExactMatch_ReturnsEntry()
    {
        var state = new ProviderState(new LlmProviderCredential
        {
            Name = "github-copilot",
            Api = "github-copilot-api",
            Kind = CredentialKind.GitHubCopilotBearer,
            Models = ["gpt-5.6-sol"],
            ModelMetadata =
            [
                new LlmModelMetadata { Id = "gpt-5.6-sol", SupportedEndpoints = ["/responses"] },
            ],
        });

        var metadata = state.FindModelMetadata("gpt-5.6-sol");

        Assert.NotNull(metadata);
        Assert.Equal("gpt-5.6-sol", metadata.Id);
        Assert.Equal(["/responses"], metadata.SupportedEndpoints);
    }

    [Fact]
    public void FindModelMetadata_CaseInsensitive_ReturnsEntry()
    {
        var state = new ProviderState(new LlmProviderCredential
        {
            Name = "github-copilot",
            Api = "github-copilot-api",
            Kind = CredentialKind.GitHubCopilotBearer,
            Models = ["gpt-5.6-sol"],
            ModelMetadata =
            [
                new LlmModelMetadata { Id = "gpt-5.6-sol", SupportedEndpoints = ["/responses"] },
            ],
        });

        var metadata = state.FindModelMetadata("GPT-5.6-SOL");

        Assert.NotNull(metadata);
        Assert.Equal("gpt-5.6-sol", metadata.Id);
    }

    [Fact]
    public void FindModelMetadata_MissingModel_ReturnsNull()
    {
        var state = new ProviderState(new LlmProviderCredential
        {
            Name = "github-copilot",
            Api = "github-copilot-api",
            Kind = CredentialKind.GitHubCopilotBearer,
            Models = ["gpt-5.6-sol"],
            ModelMetadata =
            [
                new LlmModelMetadata { Id = "gpt-5.6-sol", SupportedEndpoints = ["/responses"] },
            ],
        });

        Assert.Null(state.FindModelMetadata("nonexistent-model"));
    }

    [Fact]
    public void FindModelMetadata_NoMetadataPushed_ReturnsNull()
    {
        var state = new ProviderState(new LlmProviderCredential
        {
            Name = "github-copilot",
            Api = "github-copilot-api",
            Kind = CredentialKind.GitHubCopilotBearer,
            Models = ["gpt-5.6-sol"],
        });

        Assert.Null(state.FindModelMetadata("gpt-5.6-sol"));
    }
}
