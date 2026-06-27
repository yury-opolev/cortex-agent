using System.Net;
using System.Text;
using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Stt;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Speech.Tests.Stt;

public sealed class RemoteSpeechToTextTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public byte[]? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.LastRequest = request;
            if (request.Content is not null)
            {
                this.LastBody = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }

            return responder(request);
        }
    }

    private static RemoteSpeechToText Make(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5300") };
        return new RemoteSpeechToText(http, NullLoggerFactory.Instance);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task TranscribeAsync_PostsPcmToTranscribe_ReturnsText()
    {
        var handler = new StubHandler(_ => Json("""{"text":"hello world"}"""));
        var sut = Make(handler);

        var result = await sut.TranscribeAsync([1, 0, 2, 0]);

        Assert.Equal("hello world", result);
        Assert.Equal("/v1/transcribe", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(new byte[] { 1, 0, 2, 0 }, handler.LastBody);
    }

    [Fact]
    public async Task TranscribeAsync_NoContent_ReturnsNull()
    {
        var sut = Make(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        Assert.Null(await sut.TranscribeAsync([1, 0]));
    }

    [Fact]
    public async Task TranscribeAsync_NonSuccess_ReturnsNull()
    {
        var sut = Make(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        Assert.Null(await sut.TranscribeAsync([1, 0]));
    }

    [Fact]
    public async Task TranscribeDetailedAsync_ParsesTokens()
    {
        var handler = new StubHandler(_ =>
            Json("""{"text":"hi","tokens":[{"text":"hi","startMs":10,"endMs":40}]}"""));
        var sut = Make(handler);

        var detailed = await sut.TranscribeDetailedAsync([1, 0], prompt: null);

        Assert.NotNull(detailed);
        Assert.Equal("hi", detailed!.Text);
        Assert.Equal(new TranscribedToken("hi", 10, 40), detailed.Tokens[0]);
        Assert.Equal("/v1/transcribe/detailed", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task TranscribeAsync_SendsLanguageQuery()
    {
        var handler = new StubHandler(_ => Json("""{"text":"x"}"""));
        var sut = Make(handler);
        sut.SetLanguage("ru");

        await sut.TranscribeAsync([1, 0]);

        Assert.Contains("language=ru", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscribeDetailedAsync_SendsPromptQuery()
    {
        var handler = new StubHandler(_ => Json("""{"text":"x","tokens":[]}"""));
        var sut = Make(handler);

        await sut.TranscribeDetailedAsync([1, 0], prompt: "prior text");

        Assert.Contains("prompt=prior%20text", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckReady_Loaded_True()
    {
        var sut = Make(new StubHandler(_ => Json("""{"loaded":true}""")));
        Assert.True(await sut.CheckReadyAsync());
        Assert.True(sut.IsReady);
    }

    [Fact]
    public async Task CheckReady_NotLoaded_False()
    {
        var sut = Make(new StubHandler(_ => Json("""{"loaded":false}""")));
        Assert.False(await sut.CheckReadyAsync());
        Assert.False(sut.IsReady);
    }

    [Fact]
    public async Task CheckReady_Unreachable_False()
    {
        var sut = Make(new StubHandler(_ => throw new HttpRequestException("connection refused")));
        Assert.False(await sut.CheckReadyAsync());
    }

    [Fact]
    public void SupportedLanguages_IncludesCommonCodes()
    {
        var sut = Make(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Contains("en", sut.SupportedLanguages);
        Assert.Contains("ru", sut.SupportedLanguages);
        Assert.Contains("auto", sut.SupportedLanguages);
    }
}
