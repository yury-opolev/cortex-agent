using System.Net;
using System.Text;
using Cortex.Contained.Bridge;
using Cortex.Contained.Bridge.Setup;

namespace Cortex.Contained.Bridge.Tests.Setup;

/// <summary>
/// Pins the wiring of <see cref="SetupHelpers.InitiateCopilotDeviceFlowAsync"/>: the
/// configurable host targets the right GitHub instance, transient 5xx failures retry, and —
/// the user's explicit rule — a 400 is TERMINAL (no retry) and surfaces GitHub's real error.
/// </summary>
public class InitiateCopilotDeviceFlowTests
{
    private const string ValidDeviceBody =
        """{"device_code":"dc-123","user_code":"WXYZ-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""";

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public async Task BadRequest_IsTerminal_ThrowsWithRealError_AndDoesNotRetry()
    {
        var handler = new StubHandler(
            Json(HttpStatusCode.BadRequest,
                """{"error":"device_flow_disabled","error_description":"Device flow is disabled for this app."}"""));
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<CopilotSetupException>(() =>
            SetupHelpers.InitiateCopilotDeviceFlowAsync(client));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("Device flow is disabled for this app.", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests); // terminal — exactly one attempt, no retry
    }

    [Fact]
    public async Task ServerError_ThenSuccess_RetriesAndSucceeds()
    {
        var handler = new StubHandler(
            Json(HttpStatusCode.ServiceUnavailable, "{}"),
            Json(HttpStatusCode.OK, ValidDeviceBody));
        using var client = new HttpClient(handler);

        var result = await SetupHelpers.InitiateCopilotDeviceFlowAsync(client);

        Assert.Equal("dc-123", result.DeviceCode);
        Assert.Equal("WXYZ-1234", result.UserCode);
        Assert.Equal(2, handler.Requests.Count); // retried the 5xx once
    }

    [Fact]
    public async Task CustomGithubHost_TargetsEnterpriseDeviceEndpoint()
    {
        var handler = new StubHandler(Json(HttpStatusCode.OK, ValidDeviceBody));
        using var client = new HttpClient(handler);

        await SetupHelpers.InitiateCopilotDeviceFlowAsync(client, clientId: null, githubBaseUrl: "https://microsoft.ghe.com");

        Assert.Equal(
            "https://microsoft.ghe.com/login/device/code",
            handler.Requests[0].RequestUri!.ToString());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses;

        public StubHandler(params HttpResponseMessage[] responses)
        {
            this.responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            return Task.FromResult(this.responses.Dequeue());
        }
    }
}
