using Cortex.Contained.Bridge.Setup;

namespace Cortex.Contained.Bridge.Tests.Setup;

public sealed class GitHubOAuthUrlsTests
{
    // --- NormalizeBaseUrl ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeBaseUrl_NullOrWhitespace_ReturnsDefault(string? input)
    {
        var result = GitHubOAuthUrls.NormalizeBaseUrl(input);

        Assert.Equal(GitHubOAuthUrls.DefaultGitHubBaseUrl, result);
    }

    [Fact]
    public void NormalizeBaseUrl_TrailingSlash_IsRemoved()
    {
        var result = GitHubOAuthUrls.NormalizeBaseUrl("https://github.com/");

        Assert.Equal("https://github.com", result);
    }

    [Fact]
    public void NormalizeBaseUrl_CustomHost_IsReturnedUnchanged()
    {
        var result = GitHubOAuthUrls.NormalizeBaseUrl("https://microsoft.ghe.com");

        Assert.Equal("https://microsoft.ghe.com", result);
    }

    // --- DeviceCodeUrl ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeviceCodeUrl_NullOrWhitespace_UsesPublicGitHub(string? input)
    {
        var result = GitHubOAuthUrls.DeviceCodeUrl(input);

        Assert.Equal("https://github.com/login/device/code", result);
    }

    [Fact]
    public void DeviceCodeUrl_TrailingSlashOnHost_NoDoubleSlash()
    {
        var result = GitHubOAuthUrls.DeviceCodeUrl("https://github.com/");

        Assert.Equal("https://github.com/login/device/code", result);
        Assert.DoesNotContain("//login", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceCodeUrl_CustomHost_UsesCustomHost()
    {
        var result = GitHubOAuthUrls.DeviceCodeUrl("https://microsoft.ghe.com");

        Assert.Equal("https://microsoft.ghe.com/login/device/code", result);
    }

    // --- AccessTokenUrl ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AccessTokenUrl_NullOrWhitespace_UsesPublicGitHub(string? input)
    {
        var result = GitHubOAuthUrls.AccessTokenUrl(input);

        Assert.Equal("https://github.com/login/oauth/access_token", result);
    }

    [Fact]
    public void AccessTokenUrl_CustomHost_UsesCustomHost()
    {
        var result = GitHubOAuthUrls.AccessTokenUrl("https://microsoft.ghe.com");

        Assert.Equal("https://microsoft.ghe.com/login/oauth/access_token", result);
    }

    // --- AuthorizeUrl ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AuthorizeUrl_NullOrWhitespace_UsesPublicGitHub(string? input)
    {
        var result = GitHubOAuthUrls.AuthorizeUrl(input);

        Assert.Equal("https://github.com/login/oauth/authorize", result);
    }

    [Fact]
    public void AuthorizeUrl_CustomHost_UsesCustomHost()
    {
        var result = GitHubOAuthUrls.AuthorizeUrl("https://microsoft.ghe.com");

        Assert.Equal("https://microsoft.ghe.com/login/oauth/authorize", result);
    }

    // --- CopilotApiBaseUrl: derives the inference/models host from the auth host ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/")]
    [InlineData("github.com")]
    public void CopilotApiBaseUrl_PublicOrBlank_UsesPublicCopilotHost(string? input)
    {
        Assert.Equal("https://api.githubcopilot.com", GitHubOAuthUrls.CopilotApiBaseUrl(input));
    }

    [Theory]
    [InlineData("https://microsoft.ghe.com")]
    [InlineData("https://microsoft.ghe.com/")]
    [InlineData("microsoft.ghe.com")]
    [InlineData("http://microsoft.ghe.com")]
    public void CopilotApiBaseUrl_EnterpriseHost_DerivesCopilotApiSubdomain(string input)
    {
        Assert.Equal("https://copilot-api.microsoft.ghe.com", GitHubOAuthUrls.CopilotApiBaseUrl(input));
    }

    [Theory]
    [InlineData("copilot-api.microsoft.ghe.com")]
    [InlineData("https://copilot-api.microsoft.ghe.com")]
    [InlineData("https://copilot-api.microsoft.ghe.com/")]
    public void CopilotApiBaseUrl_AlreadyCopilotHost_DoesNotDoublePrefix(string input)
    {
        Assert.Equal("https://copilot-api.microsoft.ghe.com", GitHubOAuthUrls.CopilotApiBaseUrl(input));
    }

    [Fact]
    public void CopilotApiBaseUrl_HostWithPort_PreservesPort()
    {
        Assert.Equal(
            "https://copilot-api.ghe.acme.com:8443",
            GitHubOAuthUrls.CopilotApiBaseUrl("https://ghe.acme.com:8443"));
    }
}
