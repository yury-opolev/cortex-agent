using System.Net.Http.Json;
using Cortex.Contained.Channels.CloudMessaging.Auth;

namespace Cortex.Contained.Channels.CloudMessaging.Negotiate;

/// <summary>
/// HTTP client for the AI Messenger service's bridge-negotiate endpoint.
/// Calls <c>POST {serviceBaseUrl}/negotiate-bridge</c> with the bridge bearer token
/// and returns the Web PubSub client URL + tenant list.
/// </summary>
public sealed class CloudNegotiateClient : ICloudNegotiateClient
{
    private readonly HttpClient httpClient;
    private readonly IBridgeCredentialProvider credentialProvider;
    private readonly string negotiateEndpoint;

    /// <param name="httpClient">Named client "cloud-messaging-negotiate" (timeout pre-set by caller).</param>
    /// <param name="credentialProvider">Bridge credential (token resolver).</param>
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

        this.negotiateEndpoint = serviceBaseUrl.TrimEnd('/') + "/negotiate-bridge";
    }

    /// <inheritdoc />
    public async Task<NegotiateResult> NegotiateAsync(CancellationToken ct = default)
    {
        var token = await this.credentialProvider.GetTokenAsync(ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, this.negotiateEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await this.httpClient
            .SendAsync(request, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var parsed = await response.Content
            .ReadFromJsonAsync<NegotiateResponse>(cancellationToken: ct)
            .ConfigureAwait(false);

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Url))
        {
            throw new InvalidOperationException(
                $"[cloud-msg] negotiate-bridge returned an invalid response from {this.negotiateEndpoint}");
        }

        if (parsed.Tenants is null || parsed.Tenants.Count == 0)
        {
            throw new InvalidOperationException(
                $"[cloud-msg] negotiate-bridge returned no tenants for this bridge from {this.negotiateEndpoint}");
        }

        return new NegotiateResult
        {
            Url = parsed.Url,
            Tenants = parsed.Tenants,
        };
    }
}
