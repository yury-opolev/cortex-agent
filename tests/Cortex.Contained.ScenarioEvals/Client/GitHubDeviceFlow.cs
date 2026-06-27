using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.ScenarioEvals.Client;

/// <summary>
/// Runs the GitHub OAuth device code flow interactively in the console.
/// Used to obtain a Copilot API access token when Llm.Api is "github-copilot-oauth".
/// </summary>
public static class GitHubDeviceFlow
{
    /// <summary>
    /// Default GitHub OAuth App client ID (opencode).
    /// Users can register their own OAuth App at https://github.com/settings/developers.
    /// </summary>
    private const string DefaultClientId = "Ov23li8tweQw6odWQebz";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Run the full device flow: initiate, prompt user, poll until approved.
    /// Writes instructions to Console.Error so they appear in test output.
    /// </summary>
    /// <param name="clientId">Custom OAuth client ID, or null to use the default.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The OAuth access token.</returns>
    public static async Task<string> AuthenticateAsync(string? clientId = null, CancellationToken ct = default)
    {
        var effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId;

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("james-scenario-evals/1.0.0");

        // Step 1: Initiate device flow
        var deviceResponse = await InitiateDeviceFlowAsync(httpClient, effectiveClientId, ct);

        if (string.IsNullOrEmpty(deviceResponse.DeviceCode))
            throw new InvalidOperationException("GitHub device flow returned empty device code.");

        // Step 2: Prompt user
        Console.Error.WriteLine();
        Console.Error.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.Error.WriteLine("║  GitHub Copilot OAuth — authorization required           ║");
        Console.Error.WriteLine("╠══════════════════════════════════════════════════════════╣");
        Console.Error.WriteLine($"║  1. Open:  {deviceResponse.VerificationUri,-45}║");
        Console.Error.WriteLine($"║  2. Enter: {deviceResponse.UserCode,-45}║");
        Console.Error.WriteLine($"║  3. Approve access                                       ║");
        Console.Error.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Waiting for authorization...");

        // Step 3: Poll until success, expired, or denied
        var intervalMs = Math.Max(deviceResponse.Interval, 5) * 1000;
        var deadline = DateTime.UtcNow.AddSeconds(deviceResponse.ExpiresIn > 0 ? deviceResponse.ExpiresIn : 900);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(intervalMs, ct);

            var pollResult = await PollTokenAsync(httpClient, deviceResponse.DeviceCode, effectiveClientId, ct);

            switch (pollResult.Status)
            {
                case "success":
                    Console.Error.WriteLine("GitHub OAuth authorized successfully.");
                    return pollResult.AccessToken
                        ?? throw new InvalidOperationException("OAuth succeeded but access token was null.");

                case "pending":
                    // Keep polling
                    if (pollResult.RetryAfterSeconds > 0)
                        intervalMs = pollResult.RetryAfterSeconds * 1000;
                    break;

                case "expired":
                    throw new InvalidOperationException("GitHub device code expired. Please re-run the eval.");

                case "denied":
                    throw new InvalidOperationException("GitHub authorization was denied by the user.");

                default:
                    throw new InvalidOperationException($"GitHub OAuth polling failed with status: {pollResult.Status}");
            }
        }

        throw new TimeoutException("GitHub OAuth device flow timed out waiting for user authorization.");
    }

    private static async Task<DeviceCodeResponse> InitiateDeviceFlowAsync(
        HttpClient httpClient, string clientId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { client_id = clientId, scope = "read:user" }),
            Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<DeviceCodeResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse GitHub device code response.");
    }

    private static async Task<PollResult> PollTokenAsync(
        HttpClient httpClient, string deviceCode, string clientId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                client_id = clientId,
                device_code = deviceCode,
                grant_type = "urn:ietf:params:oauth:grant-type:device_code",
            }),
            Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return new PollResult { Status = "failed" };

        var json = await response.Content.ReadAsStringAsync(ct);
        var tokenData = JsonSerializer.Deserialize<AccessTokenResponse>(json, JsonOptions);

        if (!string.IsNullOrEmpty(tokenData?.AccessToken))
        {
            return new PollResult { Status = "success", AccessToken = tokenData.AccessToken };
        }

        return tokenData?.Error switch
        {
            "authorization_pending" => new PollResult { Status = "pending" },
            "slow_down" => new PollResult { Status = "pending", RetryAfterSeconds = (tokenData?.Interval ?? 10) + 3 },
            "expired_token" => new PollResult { Status = "expired" },
            "access_denied" => new PollResult { Status = "denied" },
            _ => new PollResult { Status = "failed" }
        };
    }

    private sealed class DeviceCodeResponse
    {
        public string? DeviceCode { get; set; }
        public string? UserCode { get; set; }
        public string? VerificationUri { get; set; }
        public int ExpiresIn { get; set; }
        public int Interval { get; set; }
    }

    private sealed class AccessTokenResponse
    {
        public string? AccessToken { get; set; }
        public string? TokenType { get; set; }
        public string? Scope { get; set; }
        public string? Error { get; set; }
        public string? ErrorDescription { get; set; }
        public int? Interval { get; set; }
    }

    private sealed class PollResult
    {
        public required string Status { get; init; }
        public string? AccessToken { get; init; }
        public int RetryAfterSeconds { get; init; }
    }
}
