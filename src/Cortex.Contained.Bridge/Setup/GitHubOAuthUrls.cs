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
}
