using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Common.Auth;

/// <summary>
/// Shared OAuth token operations for Anthropic. Supports the device code flow
/// which grants access to Claude 4 models.
/// Used by the Bridge (at runtime when the Agent Host detects expiry) and by
/// the Evals fixture (to obtain a fresh token before running tests).
/// </summary>
public static class OAuthTokenService
{
    /// <summary>Anthropic OAuth client ID (registered UUID) — used for both
    /// device-code and PKCE authorization-code flows.</summary>
    public const string AnthropicOAuthClientId = ""; // not bundled — set to enable Anthropic subscription OAuth

    /// <summary>Device code flow endpoints — platform.claude.com is the current host
    /// (console.anthropic.com redirects there).</summary>
    private const string AnthropicDeviceCodeUrl = "https://platform.claude.com/auth/device/code";
    private const string AnthropicDeviceTokenUrl = "https://platform.claude.com/auth/device/token";

    /// <summary>Legacy PKCE token endpoint (kept for backward compatibility).</summary>
    private const string AnthropicLegacyTokenUrl = "https://console.anthropic.com/v1/oauth/token";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Initiates the device code flow. Returns a device code and a URL the user
    /// must visit to authorize the app.
    /// </summary>
    public static async Task<AnthropicDeviceCodeResponse> InitiateDeviceCodeAsync(
        HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        // Use a handler that doesn't follow redirects — console.anthropic.com redirects
        // to platform.claude.com and the POST body is lost on redirect.
        using var noRedirectClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var bodyJson = JsonSerializer.Serialize(new { client_id = AnthropicOAuthClientId });
        var targetUrl = AnthropicDeviceCodeUrl;

        // Try up to 2 times (original URL + redirect)
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            using var response = await noRedirectClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Follow redirect manually — re-POST to the new URL
            if (response.StatusCode is System.Net.HttpStatusCode.Redirect or
                System.Net.HttpStatusCode.MovedPermanently or
                System.Net.HttpStatusCode.TemporaryRedirect)
            {
                var location = response.Headers.Location;
                if (location is not null)
                {
                    targetUrl = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(targetUrl), location).ToString();
                    continue;
                }
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"Anthropic device code request failed ({(int)response.StatusCode}): {json}"));
            }

            return JsonSerializer.Deserialize<AnthropicDeviceCodeResponse>(json, JsonOptions)
                ?? throw new InvalidOperationException("Failed to deserialize Anthropic device code response.");
        }

        throw new InvalidOperationException("Anthropic device code request failed after redirect.");
    }

    /// <summary>
    /// Polls the device token endpoint to check if the user has approved.
    /// Returns tokens on success, or null if still pending (caller should retry).
    /// Throws on permanent errors (expired, denied).
    /// </summary>
    public static async Task<AnthropicOAuthTokenResponse?> PollDeviceTokenAsync(
        string deviceCode, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AnthropicDeviceTokenUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                grant_type = "urn:ietf:params:oauth:grant-type:device_code",
                device_code = deviceCode,
                client_id = AnthropicOAuthClientId,
            }),
            Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return JsonSerializer.Deserialize<AnthropicOAuthTokenResponse>(json, JsonOptions)
                ?? throw new InvalidOperationException("Failed to deserialize Anthropic device token response.");
        }

        // Check for "authorization_pending" — means user hasn't approved yet
        if (json.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
        {
            return null; // Caller should retry after interval
        }

        // Any other error is permanent (expired_token, access_denied, etc.)
        throw new InvalidOperationException(
            string.Create(CultureInfo.InvariantCulture,
                $"Anthropic device token poll failed ({(int)response.StatusCode}): {json}"));
    }

    /// <summary>
    /// Uses a refresh token to obtain a fresh access + refresh token pair.
    /// Uses the device token endpoint (not the legacy /v1/oauth/token endpoint).
    /// </summary>
    public static async Task<AnthropicOAuthTokenResponse> RefreshAnthropicTokenAsync(
        string refreshToken, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AnthropicDeviceTokenUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                grant_type = "refresh_token",
                client_id = AnthropicOAuthClientId,
                refresh_token = refreshToken,
            }),
            Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture,
                    $"Anthropic token refresh failed ({(int)response.StatusCode}): {json}"));
        }

        return JsonSerializer.Deserialize<AnthropicOAuthTokenResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize Anthropic token refresh response.");
    }
}

/// <summary>Response from Anthropic's device code initiation endpoint.</summary>
public sealed class AnthropicDeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    [JsonPropertyName("verification_uri")]
    public string? VerificationUri { get; set; }

    [JsonPropertyName("verification_uri_complete")]
    public string VerificationUriComplete { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; } = 5;
}

/// <summary>Response from Anthropic's OAuth token endpoint.</summary>
public sealed class AnthropicOAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    /// <summary>Lifetime in seconds from the time of issue.</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }
}
