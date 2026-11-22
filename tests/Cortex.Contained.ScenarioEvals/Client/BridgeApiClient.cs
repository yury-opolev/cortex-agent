using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.ScenarioEvals.Abstractions;
using Cortex.Contained.ScenarioEvals.Model;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.ScenarioEvals.Client;

/// <summary>
/// HTTP client for Bridge API with dual auth:
/// - X-Api-Key header for tenant message endpoints
/// - cortex_session cookie (obtained via POST /api/auth/login) for admin endpoints
/// </summary>
public sealed class BridgeApiClient : IBridgeApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _tenantId;
    private readonly string _apiKey;
    private readonly ILogger<BridgeApiClient> _logger;

    public string ChannelId => $"api-{_tenantId}";

    private BridgeApiClient(HttpClient httpClient, string tenantId, string apiKey, ILogger<BridgeApiClient> logger)
    {
        _httpClient = httpClient;
        _tenantId = tenantId;
        _apiKey = apiKey;
        _logger = logger;
    }

    /// <summary>
    /// Create a client and log in to obtain the session cookie.
    /// </summary>
    public static async Task<BridgeApiClient> CreateAsync(
        string bridgeUrl,
        string password,
        string apiKey,
        string tenantId,
        ILogger<BridgeApiClient> logger,
        CancellationToken ct)
    {
        var cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = cookieContainer };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(bridgeUrl) };

        var client = new BridgeApiClient(httpClient, tenantId, apiKey, logger);
        await client.LoginAsync(password, ct);
        return client;
    }

    private async Task LoginAsync(string password, CancellationToken ct)
    {
        _logger.LogInformation("Logging in to Bridge at {BaseAddress}", _httpClient.BaseAddress);

        var response = await _httpClient.PostAsJsonAsync("/api/auth/login", new { password }, ct);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Bridge login successful, session cookie obtained");
    }

    public async Task<(string Response, TokenUsageInfo? Tokens)> SendMessageAsync(string text, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tenants/{_tenantId}/message");
        request.Headers.Add("X-Api-Key", _apiKey);
        request.Content = JsonContent.Create(new { text });

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<MessageResponse>(JsonOptions, ct);
        return (body?.Response ?? "", null);
    }

    public async Task<MemoryListResult> ListMemoriesAsync(int limit = 200, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"/api/tenants/{_tenantId}/memories?limit={limit}", ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<MemoryListResult>(JsonOptions, ct)
            ?? new MemoryListResult { Items = [] };
    }

    public async Task CompactAsync(string channelId, CancellationToken ct)
    {
        _logger.LogInformation("Triggering compact for channel {ChannelId}", channelId);
        var response = await _httpClient.PostAsync($"/api/tenants/{_tenantId}/compact?channelId={Uri.EscapeDataString(channelId)}", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task CompactMemoriesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Triggering memory compaction");
        var response = await _httpClient.PostAsync($"/api/tenants/{_tenantId}/compact-memories", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ResetSessionAsync(string channelId, CancellationToken ct)
    {
        _logger.LogInformation("Resetting session for channel {ChannelId}", channelId);
        var response = await _httpClient.PostAsync($"/api/tenants/{_tenantId}/reset-session?channelId={Uri.EscapeDataString(channelId)}", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ResetAllAsync(CancellationToken ct)
    {
        _logger.LogInformation("Resetting all data for tenant {TenantId}", _tenantId);
        var response = await _httpClient.PostAsync($"/api/tenants/{_tenantId}/reset", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ClearMessagesAsync(CancellationToken ct)
    {
        var response = await _httpClient.DeleteAsync($"/api/tenants/{_tenantId}/messages", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ClearMemoriesAsync(CancellationToken ct)
    {
        var response = await _httpClient.DeleteAsync($"/api/tenants/{_tenantId}/memories", ct);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _httpClient.Dispose();

    public async Task ImportMessagesAsync(IReadOnlyList<ExportMessageEntry> items, CancellationToken ct)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/tenants/{_tenantId}/import/messages",
            new ImportMessagesRequest { Items = items, ClearExisting = true },
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ExportMessageEntry>> ExportMessagesAsync(CancellationToken ct)
    {
        var payload = await _httpClient.GetFromJsonAsync<ExportMessagesPayload>($"/api/tenants/{_tenantId}/export/messages", JsonOptions, ct);
        return payload?.Items ?? [];
    }

    public async Task SetRecallToolsAsync(bool enabled, CancellationToken ct)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/tenants/{_tenantId}/history/recall-tools", new { enabled }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task PurgeChannelAsync(string channelId, DateTimeOffset? olderThan, CancellationToken ct)
    {
        var url = $"/api/tenants/{_tenantId}/channels/{Uri.EscapeDataString(channelId)}/history";
        if (olderThan is { } cutoff)
        {
            url += $"?olderThan={Uri.EscapeDataString(cutoff.ToString("O"))}";
        }

        var response = await _httpClient.DeleteAsync(url, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> GetSelfNotesAsync(CancellationToken ct)
    {
        var body = await _httpClient.GetFromJsonAsync<SelfNotesResponse>($"/api/tenants/{_tenantId}/self-notes", JsonOptions, ct);
        return body?.SelfNotes ?? "";
    }

    private sealed class SelfNotesResponse
    {
        public string? SelfNotes { get; init; }
    }

    private sealed class MessageResponse
    {
        public string? ConversationId { get; init; }
        public string? Response { get; init; }
        public string? MessageId { get; init; }
    }
}
