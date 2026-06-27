using System.Net;
using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Speech.Tts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class DanishTtsReadinessProbeTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ =>
            new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(this.Responder(request));
    }

    private static RoestDanishTtsProvider NewProvider(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") },
            NullLoggerFactory.Instance);

    [Fact]
    public async Task ProbeOnceAsync_HealthySidecar_MarksProviderReady()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"model_loaded":true,"sample_rate":24000}"""),
            },
        };
        var provider = NewProvider(handler);
        Assert.False(provider.IsReady);

        var probe = new DanishTtsReadinessProbe(provider, NullLogger<DanishTtsReadinessProbe>.Instance);
        await probe.ProbeOnceAsync(CancellationToken.None);

        Assert.True(provider.IsReady);
    }

    [Fact]
    public async Task ProbeOnceAsync_UnavailableSidecar_LeavesProviderNotReady()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        };
        var provider = NewProvider(handler);

        var probe = new DanishTtsReadinessProbe(provider, NullLogger<DanishTtsReadinessProbe>.Instance);
        await probe.ProbeOnceAsync(CancellationToken.None);

        Assert.False(provider.IsReady);
    }
}
