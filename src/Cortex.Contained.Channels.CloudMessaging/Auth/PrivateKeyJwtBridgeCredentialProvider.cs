using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Channels.CloudMessaging.Auth;

/// <summary>
/// <see cref="IBridgeCredentialProvider"/> that authenticates via the OAuth 2.0
/// <c>client_credentials</c> grant with a <c>private_key_jwt</c> assertion.
/// Flow:
/// <list type="number">
///   <item>Signs a short-lived JWT assertion using the bridge's private RSA key
///   (loaded from the DPAPI secret store as a PEM string).</item>
///   <item><c>POST /oauth2/token</c> with <c>grant_type=client_credentials</c>,
///   <c>client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer</c>,
///   <c>client_id</c>, and <c>client_assertion</c>.</item>
///   <item>Returns and caches the issued short-lived S2S access token; refreshes
///   before expiry so callers never see an expired token.</item>
/// </list>
/// No private key material or tokens appear in logs.
/// </summary>
public sealed class PrivateKeyJwtBridgeCredentialProvider : IBridgeCredentialProvider, IDisposable
{
    private const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    private const int RefreshBufferSeconds = 60; // refresh this many seconds before expiry

    private readonly HttpClient httpClient;
    private readonly string tokenEndpointUrl;
    private readonly string clientId;
    private readonly string rsaPrivateKeyPem;

    private string? cachedToken;
    private DateTimeOffset tokenExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    /// <param name="httpClient">HTTP client for the token endpoint.</param>
    /// <param name="tokenEndpointUrl">Full URL of <c>POST /oauth2/token</c>.</param>
    /// <param name="clientId">The bridge's registered client ID.</param>
    /// <param name="rsaPrivateKeyPem">
    /// PEM-encoded RSA private key (PKCS#8 or PKCS#1) sourced from the DPAPI secret store.
    /// Never logged.
    /// </param>
    public PrivateKeyJwtBridgeCredentialProvider(
        HttpClient httpClient,
        string tokenEndpointUrl,
        string clientId,
        string rsaPrivateKeyPem)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        if (string.IsNullOrWhiteSpace(tokenEndpointUrl))
        {
            throw new ArgumentException("Token endpoint URL must not be empty.", nameof(tokenEndpointUrl));
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client ID must not be empty.", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(rsaPrivateKeyPem))
        {
            throw new ArgumentException("RSA private key PEM must not be empty.", nameof(rsaPrivateKeyPem));
        }

        this.tokenEndpointUrl = tokenEndpointUrl;
        this.clientId = clientId;
        this.rsaPrivateKeyPem = rsaPrivateKeyPem;
    }

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        // Fast path: return cached token if still valid with refresh buffer.
        var now = DateTimeOffset.UtcNow;
        if (this.cachedToken is not null && now < this.tokenExpiry - TimeSpan.FromSeconds(RefreshBufferSeconds))
        {
            return this.cachedToken;
        }

        await this.refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the lock (another thread may have refreshed already).
            now = DateTimeOffset.UtcNow;
            if (this.cachedToken is not null && now < this.tokenExpiry - TimeSpan.FromSeconds(RefreshBufferSeconds))
            {
                return this.cachedToken;
            }

            var (token, expiresIn) = await this.FetchTokenAsync(ct).ConfigureAwait(false);
            this.cachedToken = token;
            this.tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return token;
        }
        finally
        {
            this.refreshLock.Release();
        }
    }

    private async Task<(string token, int expiresInSeconds)> FetchTokenAsync(CancellationToken ct)
    {
        var assertion = BuildJwtAssertion(this.clientId, this.tokenEndpointUrl, this.rsaPrivateKeyPem);

        var formContent = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", this.clientId),
            new KeyValuePair<string, string>("client_assertion_type", AssertionType),
            new KeyValuePair<string, string>("client_assertion", assertion),
        ]);

        using var request = new HttpRequestMessage(HttpMethod.Post, this.tokenEndpointUrl)
        {
            Content = formContent,
        };

        using var response = await this.httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
            .ConfigureAwait(false);

        if (body is null || string.IsNullOrWhiteSpace(body.AccessToken))
        {
            throw new InvalidOperationException(
                $"[cloud-msg] /oauth2/token returned an invalid response from {this.tokenEndpointUrl}");
        }

        return (body.AccessToken, body.ExpiresIn > 0 ? body.ExpiresIn : 3600);
    }

    /// <summary>
    /// Builds a minimal <c>private_key_jwt</c> assertion:
    /// RS256-signed JWT with <c>iss=client_id, sub=client_id, aud=tokenEndpointUrl, jti, iat, exp</c>.
    /// </summary>
    private static string BuildJwtAssertion(string clientId, string audience, string rsaPrivateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(rsaPrivateKeyPem);

        var now = DateTimeOffset.UtcNow;
        var exp = now.AddMinutes(5);

        // Header
        var headerBytes = Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}""");
        var header = Base64UrlEncode(headerBytes);

        // Payload (use raw JSON to stay allocation-light and avoid any serializer config)
        var payload = $$$"""
            {
              "iss": "{{{EscapeJson(clientId)}}}",
              "sub": "{{{EscapeJson(clientId)}}}",
              "aud": "{{{EscapeJson(audience)}}}",
              "jti": "{{{Guid.NewGuid():N}}}",
              "iat": {{{now.ToUnixTimeSeconds()}}},
              "exp": {{{exp.ToUnixTimeSeconds()}}}
            }
            """;
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var payloadEncoded = Base64UrlEncode(payloadBytes);

        // Signing input
        var signingInput = $"{header}.{payloadEncoded}";
        var signingInputBytes = Encoding.ASCII.GetBytes(signingInput);
        var signature = rsa.SignData(signingInputBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <inheritdoc />
    public void Dispose()
    {
        this.refreshLock.Dispose();
    }

    // ── Wire types ────────────────────────────────────────────────────

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
