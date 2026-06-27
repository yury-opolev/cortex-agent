using System.Net;
using System.Net.Http.Json;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Integration.Tests;

/// <summary>
/// Integration tests for the Agent Host. These tests spin up the full ASP.NET Core
/// pipeline via <see cref="AgentHostFactory"/> and exercise HTTP + SignalR endpoints.
/// </summary>
public sealed class AgentHostIntegrationTests : IClassFixture<AgentHostFactory>, IAsyncLifetime
{
    private readonly AgentHostFactory _factory;
    private HubConnection? _hub;

    public AgentHostIntegrationTests(AgentHostFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }
    }

    private async Task<HubConnection> GetConnectedHubAsync()
    {
        _hub ??= _factory.CreateHubConnection();

        if (_hub.State == HubConnectionState.Disconnected)
        {
            await _hub.StartAsync();
        }

        return _hub;
    }

    // -------------------------------------------------------
    // HTTP Endpoint Tests
    // -------------------------------------------------------

    [Fact]
    public async Task Health_Endpoint_Returns_Ok_With_Subsystems()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<HealthEndpointResponse>();
        Assert.NotNull(body);
        Assert.True(body.Healthy);
        Assert.NotNull(body.Version);
        Assert.NotNull(body.Subsystems);
        Assert.NotNull(body.Subsystems.ToolRegistry);
        Assert.True(body.Subsystems.ToolRegistry.ToolCount > 0);
        Assert.Equal("ok", body.Subsystems.ToolRegistry.Status);
        Assert.NotNull(body.Subsystems.SessionStore);
        Assert.Equal("ok", body.Subsystems.SessionStore.Status);
    }

    [Fact]
    public async Task Root_Endpoint_Returns_Running_Message()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("running", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Health_Endpoint_Does_Not_Require_Authentication()
    {
        // Create a raw HttpClient without any auth token
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // -------------------------------------------------------
    // SignalR Connection Tests
    // -------------------------------------------------------

    [Fact]
    public async Task Hub_Connection_Succeeds_With_Valid_Token()
    {
        var hub = await GetConnectedHubAsync();

        Assert.Equal(HubConnectionState.Connected, hub.State);
    }

    [Fact]
    public async Task Hub_Connection_Fails_Without_Token()
    {
        var server = _factory.Server;
        var hubUrl = new Uri(server.BaseAddress, "/hub/agent");

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                // No token provided
            })
            .Build();

        await using var _ = connection;

        await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
    }

    [Fact]
    public async Task Hub_Connection_Fails_With_Invalid_Token()
    {
        var server = _factory.Server;
        var hubUrl = new Uri(server.BaseAddress, "/hub/agent");

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>("wrong-token");
            })
            .Build();

        await using var _ = connection;

        await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
    }

    // -------------------------------------------------------
    // Ping Tests
    // -------------------------------------------------------

    [Fact]
    public async Task Ping_Returns_Healthy_Status()
    {
        var hub = await GetConnectedHubAsync();

        var result = await hub.InvokeAsync<HealthInfo>("Ping");

        Assert.NotNull(result);
        Assert.True(result.Healthy);
        Assert.NotNull(result.Version);
        Assert.True(result.Timestamp <= DateTimeOffset.UtcNow);
    }

    // -------------------------------------------------------
    // Status Tests
    // -------------------------------------------------------

    [Fact]
    public async Task GetStatus_Returns_Agent_Status()
    {
        var hub = await GetConnectedHubAsync();

        var status = await hub.InvokeAsync<AgentStatusInfo>("GetStatus");

        Assert.NotNull(status);
        Assert.Equal(AgentStatus.Idle, status.Status);
    }

    // -------------------------------------------------------
    // DTOs for deserializing the /health anonymous object
    // -------------------------------------------------------

#pragma warning disable CA1812 // Avoid uninstantiated internal classes — used by JSON deserialization
    private sealed class HealthEndpointResponse
    {
        public bool Healthy { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public string? Version { get; set; }
        public SubsystemsInfo? Subsystems { get; set; }
    }

    private sealed class SubsystemsInfo
    {
        public ToolRegistryInfo? ToolRegistry { get; set; }
        public SessionStoreInfo? SessionStore { get; set; }
    }

    private sealed class ToolRegistryInfo
    {
        public string? Status { get; set; }
        public int ToolCount { get; set; }
    }

    private sealed class SessionStoreInfo
    {
        public string? Status { get; set; }
        public int SessionCount { get; set; }
    }
#pragma warning restore CA1812
}
