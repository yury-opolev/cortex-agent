using System.Net;
using Cortex.Contained.Launcher.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Launcher.Tests.Services;

public sealed class HealthMonitorServiceTests
{
    [Fact]
    public async Task CheckHealthAsync_BothHealthy_ReturnsAllHealthy()
    {
        var handler = new FakeHttpHandler(new Dictionary<string, HttpStatusCode>
        {
            ["http://127.0.0.1:5080/health"] = HttpStatusCode.OK,
            ["http://127.0.0.1:5100/health"] = HttpStatusCode.OK,
        });
        var client = new HttpClient(handler);
        var sut = new HealthMonitorService(client, NullLogger<HealthMonitorService>.Instance);

        var result = await sut.CheckHealthAsync();

        Assert.True(result.IsBridgeHealthy);
        Assert.True(result.IsAgentHealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_AgentDown_ReportsAgentUnhealthy()
    {
        var handler = new FakeHttpHandler(new Dictionary<string, HttpStatusCode>
        {
            ["http://127.0.0.1:5080/health"] = HttpStatusCode.OK,
            ["http://127.0.0.1:5100/health"] = HttpStatusCode.ServiceUnavailable,
        });
        var client = new HttpClient(handler);
        var sut = new HealthMonitorService(client, NullLogger<HealthMonitorService>.Instance);

        var result = await sut.CheckHealthAsync();

        Assert.True(result.IsBridgeHealthy);
        Assert.False(result.IsAgentHealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_BridgeDown_ReportsBridgeUnhealthy()
    {
        var handler = new FakeHttpHandler(new Dictionary<string, HttpStatusCode>
        {
            ["http://127.0.0.1:5100/health"] = HttpStatusCode.OK,
        });
        // Bridge URL not in dictionary — handler throws HttpRequestException
        var client = new HttpClient(handler);
        var sut = new HealthMonitorService(client, NullLogger<HealthMonitorService>.Instance);

        var result = await sut.CheckHealthAsync();

        Assert.False(result.IsBridgeHealthy);
        Assert.True(result.IsAgentHealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_BothDown_ReportsBothUnhealthy()
    {
        var handler = new FakeHttpHandler(new Dictionary<string, HttpStatusCode>());
        var client = new HttpClient(handler);
        var sut = new HealthMonitorService(client, NullLogger<HealthMonitorService>.Instance);

        var result = await sut.CheckHealthAsync();

        Assert.False(result.IsBridgeHealthy);
        Assert.False(result.IsAgentHealthy);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpStatusCode> responses;

        public FakeHttpHandler(Dictionary<string, HttpStatusCode> responses)
        {
            this.responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (this.responses.TryGetValue(url, out var statusCode))
            {
                return Task.FromResult(new HttpResponseMessage(statusCode));
            }

            throw new HttpRequestException($"Connection refused: {url}");
        }
    }
}
