namespace Cortex.Contained.Bridge.Setup;

/// <summary>
/// Builds GitHub OAuth endpoint URLs from an optional, user-supplied base host
/// so the wizard can target either public github.com or an enterprise GHE host.
/// </summary>
public static class GitHubOAuthUrls
{
    /// <summary>The default GitHub base URL used when no custom host is supplied.</summary>
    public const string DefaultGitHubBaseUrl = "https://github.com";

    /// <summary>
    /// Normalizes a GitHub base URL. Returns <see cref="DefaultGitHubBaseUrl"/> when
    /// <paramref name="baseUrl"/> is null, empty, or whitespace; otherwise trims
    /// trailing slashes.
    /// </summary>
    /// <param name="baseUrl">The caller-supplied base URL, or null.</param>
    /// <returns>A normalized base URL with no trailing slash.</returns>
    public static string NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return DefaultGitHubBaseUrl;
        }

        return baseUrl.TrimEnd('/');
    }

    /// <summary>Returns the device-code endpoint URL for the given host.</summary>
    /// <param name="baseUrl">The caller-supplied base URL, or null.</param>
    public static string DeviceCodeUrl(string? baseUrl)
    {
        return $"{NormalizeBaseUrl(baseUrl)}/login/device/code";
    }

    /// <summary>Returns the access-token endpoint URL for the given host.</summary>
    /// <param name="baseUrl">The caller-supplied base URL, or null.</param>
    public static string AccessTokenUrl(string? baseUrl)
    {
        return $"{NormalizeBaseUrl(baseUrl)}/login/oauth/access_token";
    }

    /// <summary>Returns the authorize endpoint URL for the given host.</summary>
    /// <param name="baseUrl">The caller-supplied base URL, or null.</param>
    public static string AuthorizeUrl(string? baseUrl)
    {
        return $"{NormalizeBaseUrl(baseUrl)}/login/oauth/authorize";
    }

    /// <summary>
    /// Derives the GitHub Copilot inference + models API host from the configured GitHub auth
    /// host. For public github.com (or null/blank) this is the public
    /// <c>https://api.githubcopilot.com</c>; for a GitHub Enterprise data-residency host it is
    /// <c>https://copilot-api.&lt;host&gt;</c> (e.g. <c>copilot-api.microsoft.ghe.com</c>). A
    /// GHE-issued device token is only valid at the GHE Copilot host — hitting the public host
    /// with it returns 401 (the 0.2.293 D2/D5 gap). Mirrors OpenCode's <c>base(enterpriseUrl)</c>.
    /// </summary>
    /// <param name="githubBaseUrl">The configured GitHub auth host, or null for public github.com.</param>
    public static string CopilotApiBaseUrl(string? githubBaseUrl)
    {
        var host = NormalizeBaseUrl(githubBaseUrl)
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        return string.IsNullOrEmpty(host) || string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
            ? "https://api.githubcopilot.com"
            : $"https://copilot-api.{host}";
    }
}
