using System.Net;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Hubs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Contained.Integration.Tests;

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> for Agent Host integration tests.
/// Overrides authentication token, disables Serilog file sinks, and uses an in-memory session store.
/// </summary>
public sealed class AgentHostFactory : WebApplicationFactory<AgentHub>
{
    public const string TestHubToken = "integration-test-token-1234";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("CORTEX_HUB_TOKEN", TestHubToken);

        // Use a unique temp directory for each test run to avoid state leakage.
        // Both CORTEX_DATA_PATH (tool sandbox) and CORTEX_STATE_PATH (databases —
        // scheduler, memory) must be set; otherwise CORTEX_STATE_PATH falls back
        // to "/app/state" which on Windows resolves to C:\app\state\ and gets
        // shared across all test runs, causing schema drift between versions.
        var tempRoot = Path.Combine(Path.GetTempPath(), "james-integration-tests", Guid.NewGuid().ToString("N"));
        var tempDataPath = Path.Combine(tempRoot, "data");
        var tempStatePath = Path.Combine(tempRoot, "state");
        Directory.CreateDirectory(tempDataPath);
        Directory.CreateDirectory(tempStatePath);
        builder.UseSetting("CORTEX_DATA_PATH", tempDataPath);
        builder.UseSetting("CORTEX_STATE_PATH", tempStatePath);
    }

    /// <summary>
    /// Create a SignalR <see cref="HubConnection"/> authenticated with the test token.
    /// </summary>
    public HubConnection CreateHubConnection()
    {
        var server = Server; // ensure test server is started
        var hubUrl = new Uri(server.BaseAddress, "/hub/agent");

        return new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(TestHubToken);
            })
            .Build();
    }
}
