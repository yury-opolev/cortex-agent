using Cortex.Contained.Agent.Host.Llm.Providers;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// The LLM error-context log line dumps the outgoing request headers. Credential-bearing
/// headers (a Copilot <c>Authorization: Bearer gho_…</c>, an Anthropic <c>x-api-key</c>) must
/// be masked so a provider 4xx never writes a live token into the container logs.
/// </summary>
public class ProviderClientHelpersHeaderMaskingTests
{
    private const string GitHubToken = "gho_0123456789abcdefghijklmnopqrstuvwxyz";
    private const string ApiKey = "sk-ant-api03-0123456789abcdefghijklmnopqrstuvwxyz";

    [Fact]
    public void FormatHeadersForLog_AuthorizationBearer_IsMasked()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/v1/messages");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {GitHubToken}");

        var formatted = ProviderClientHelpers.FormatHeadersForLog(request.Headers);

        Assert.DoesNotContain(GitHubToken, formatted);
        Assert.Contains("Authorization=", formatted);
    }

    [Fact]
    public void FormatHeadersForLog_ApiKeyHeader_IsMasked()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);

        var formatted = ProviderClientHelpers.FormatHeadersForLog(request.Headers);

        Assert.DoesNotContain(ApiKey, formatted);
        Assert.Contains("x-api-key=", formatted);
    }

    [Theory]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("api-key")]
    public void FormatHeadersForLog_OtherCredentialHeaders_AreMasked(string headerName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/");
        request.Headers.TryAddWithoutValidation(headerName, GitHubToken);

        var formatted = ProviderClientHelpers.FormatHeadersForLog(request.Headers);

        Assert.DoesNotContain(GitHubToken, formatted);
    }

    [Fact]
    public void FormatHeadersForLog_NonSensitiveHeaders_ArePreserved()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.githubcopilot.com/v1/messages");
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-06-01");

        var formatted = ProviderClientHelpers.FormatHeadersForLog(request.Headers);

        Assert.Contains("anthropic-version=2023-06-01", formatted);
        Assert.Contains("X-GitHub-Api-Version=2026-06-01", formatted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("abcd")]
    public void MaskToken_ShortToken_DoesNotThrowOrLeak(string token)
    {
        var masked = ProviderClientHelpers.MaskToken(token);

        if (token.Length > 0)
        {
            Assert.DoesNotContain(token, masked);
        }
    }
}
