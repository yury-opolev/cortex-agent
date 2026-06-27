using System.Net;
using System.Text;
using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Tts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Speech.Tests.Tts;

public sealed class RemoteTtsProviderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.LastRequest = request;
            if (request.Content is not null)
            {
                this.LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return responder(request);
        }
    }

    private static RemoteTtsProvider Make(StubHandler handler, string engine = "kokoro")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") };
        return new RemoteTtsProvider(engine, http, NullLoggerFactory.Instance,
            [new TtsVoiceInfo("af_heart", "en", VoiceGender.Female)]);
    }

    [Fact]
    public async Task SynthesizeStreaming_SendsEngineAndReturnsPcm()
    {
        var pcm = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(pcm),
        });
        var provider = Make(handler);

        var outBytes = new List<byte>();
        await foreach (var chunk in provider.SynthesizeStreamingAsync("hi", "af_heart"))
        {
            outBytes.AddRange(chunk);
        }

        Assert.Equal(pcm, outBytes.ToArray());
        Assert.Contains("\"engine\":\"kokoro\"", handler.LastBody);
        Assert.Contains("\"voice\":\"af_heart\"", handler.LastBody);
        Assert.EndsWith("/v1/synthesize/stream", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SynthesizeStreaming_RealignsOddChunkBoundaries()
    {
        // Two reads that split a 16-bit sample across the boundary: 3 bytes then 1.
        var pcm = new byte[] { 10, 20, 30, 40 };
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(pcm),
        });
        var provider = Make(handler);

        var outBytes = new List<byte>();
        await foreach (var chunk in provider.SynthesizeStreamingAsync("hi", "af_heart"))
        {
            Assert.Equal(0, chunk.Length % 2); // every yielded chunk is sample-aligned
            outBytes.AddRange(chunk);
        }

        Assert.Equal(pcm, outBytes.ToArray());
    }

    [Fact]
    public async Task SynthesizeStreaming_NonSuccess_YieldsNothing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var provider = Make(handler);

        var any = false;
        await foreach (var _ in provider.SynthesizeStreamingAsync("hi", "af_heart"))
        {
            any = true;
        }

        Assert.False(any);
    }

    [Fact]
    public async Task CheckReady_ParsesPerEngineLoaded_True()
    {
        const string json = """
        {"ready":true,"engines":{"kokoro":{"loaded":true,"nativeSampleRate":24000}}}
        """;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var provider = Make(handler);

        Assert.True(await provider.CheckReadyAsync());
        Assert.True(provider.IsReady);
    }

    [Fact]
    public async Task CheckReady_EngineNotLoaded_False()
    {
        const string json = """
        {"ready":false,"engines":{"kokoro":{"loaded":false}}}
        """;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var provider = Make(handler);

        Assert.False(await provider.CheckReadyAsync());
        Assert.False(provider.IsReady);
    }

    [Fact]
    public async Task CheckReady_MissingEngine_False()
    {
        const string json = """
        {"ready":true,"engines":{"roest-da":{"loaded":true}}}
        """;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var provider = Make(handler, engine: "kokoro");

        Assert.False(await provider.CheckReadyAsync());
    }

    [Fact]
    public async Task CheckReady_Unreachable_False()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var provider = Make(handler);

        Assert.False(await provider.CheckReadyAsync());
    }

    [Fact]
    public void OutputFormat_Is48kHz()
    {
        var provider = Make(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal(48000, provider.OutputFormat.SampleRate);
    }

    [Fact]
    public void Name_IsEngineName()
    {
        var provider = Make(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), engine: "silero-v5-russian");
        Assert.Equal("silero-v5-russian", provider.Name);
    }
}
