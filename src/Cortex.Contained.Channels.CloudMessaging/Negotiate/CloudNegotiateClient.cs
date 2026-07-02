using System.Net.Http.Json;
using Cortex.Contained.Channels.CloudMessaging.Auth;

namespace Cortex.Contained.Channels.CloudMessaging.Negotiate;

/// <summary>
/// HTTP client for the AI Messenger service's two-step bridge-negotiate flow.
/// <list type="number">
///   <item>Calls <see cref="IBridgeCredentialProvider.GetTokenAsync"/> to obtain a short-lived S2S
///   access token (the provider internally does <c>POST /oauth2/token</c> with a
///   <c>private_key_jwt</c> assertion).</item>
///   <item>Calls <c>POST {serviceBaseUrl}/negotiate-bridge</c> with the access token and parses
///   the returned <c>{ hubUrl, tenants[] }</c> shape.</item>
/// </list>
/// No credentials or PII in logs.
/// </summary>
public sealed class CloudNegotiateClient : ICloudNegotiateClient
{
    private readonly HttpClient httpClient;
    private readonly IBridgeCredentialProvider credentialProvider;
    private readonly string serviceBaseUrl;
    private readonly string negotiateEndpoint;

    /// <param name="httpClient">Named client "cloud-messaging-negotiate" (timeout pre-set by caller).</param>
    /// <param name="credentialProvider">Bridge credential — obtains a short-lived S2S access token.</param>
    /// <param name="serviceBaseUrl">Base URL of the AI Messenger service (e.g. https://api.example.com).</param>
    public CloudNegotiateClient(
        HttpClient httpClient,
        IBridgeCredentialProvider credentialProvider,
        string serviceBaseUrl)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));

        if (string.IsNullOrWhiteSpace(serviceBaseUrl))
        {
            throw new ArgumentException("Service base URL must not be null or empty.", nameof(serviceBaseUrl));
        }

        this.serviceBaseUrl = serviceBaseUrl.TrimEnd('/');
        this.negotiateEndpoint = this.serviceBaseUrl + "/negotiate-bridge";
    }

    /// <inheritdoc />
    public async Task<NegotiateResult> NegotiateAsync(CancellationToken ct = default)
    {
        // Step 1: obtain a short-lived S2S access token.
        // The provider handles the private_key_jwt assertion and token caching internally.
        var token = await this.credentialProvider.GetTokenAsync(ct).ConfigureAwait(false);

        // Step 2: call POST /negotiate-bridge with the bearer token.
        using var request = new HttpRequestMessage(HttpMethod.Post, this.negotiateEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await this.httpClient
            .SendAsync(request, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var parsed = await response.Content
            .ReadFromJsonAsync<NegotiateResponse>(cancellationToken: ct)
            .ConfigureAwait(false);

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.HubUrl))
        {
            throw new InvalidOperationException(
                $"[cloud-msg] negotiate-bridge returned an invalid response from {this.negotiateEndpoint}");
        }

        if (parsed.Tenants is null || parsed.Tenants.Count == 0)
        {
            throw new InvalidOperationException(
                $"[cloud-msg] negotiate-bridge returned no tenants for this bridge from {this.negotiateEndpoint}");
        }

        // Build the fully-qualified hub URL: serviceBaseUrl + hubUrl path.
        // The service returns a path like "/hub"; prepend the base to get "https://api.example.com/hub".
        var hubUrl = parsed.HubUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? parsed.HubUrl                              // already absolute
            : this.serviceBaseUrl + "/" + parsed.HubUrl.TrimStart('/');

        return new NegotiateResult
        {
            HubUrl = hubUrl,
            Tenants = parsed.Tenants,
        };
    }
}
