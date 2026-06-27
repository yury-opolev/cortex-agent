namespace Cortex.Contained.Speech.Tests.SpeakerId;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Speech.SpeakerId;

public sealed class HttpSpeakerEmbedderTests
{
    [Fact]
    public async Task EmbedAsync_PostsPcmBase64_AndReturnsEmbedding()
    {
        string? capturedBody = null;
        var handler = new TestHandler((req, ct) =>
        {
            capturedBody = req.Content!.ReadAsStringAsync(ct).Result;
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"embedding\":[0.1,-0.2,0.3],\"embeddingDim\":3,\"modelId\":\"x\",\"elapsedMs\":12}", Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        });
        var factory = new TestHttpClientFactory(handler, new Uri("http://test/"));
        var sut = new HttpSpeakerEmbedder(factory, "voice-id", modelId: "x", embeddingDim: 3, sampleRate: 16000);

        var pcm = new short[] { 1, 2, 3, 4 };
        var result = await sut.EmbedAsync(pcm, CancellationToken.None);

        Assert.Equal([0.1f, -0.2f, 0.3f], result);
        Assert.NotNull(capturedBody);
        var json = JsonDocument.Parse(capturedBody!);
        Assert.Equal(16000, json.RootElement.GetProperty("sampleRate").GetInt32());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("pcm16Base64").GetString()));
    }

    [Fact]
    public async Task EmbedAsync_NonSuccess_Throws()
    {
        var handler = new TestHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var factory = new TestHttpClientFactory(handler, new Uri("http://test/"));
        var sut = new HttpSpeakerEmbedder(factory, "voice-id", "x", 3, 16000);
        await Assert.ThrowsAsync<HttpRequestException>(() => sut.EmbedAsync(new short[10].AsMemory(), CancellationToken.None).AsTask());
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl;
        public TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl) { this.impl = impl; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => this.impl(request, cancellationToken);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler handler;
        private readonly Uri baseAddress;
        public TestHttpClientFactory(HttpMessageHandler handler, Uri baseAddress)
        {
            this.handler = handler;
            this.baseAddress = baseAddress;
        }
        public HttpClient CreateClient(string name)
            => new HttpClient(this.handler, disposeHandler: false) { BaseAddress = this.baseAddress };
    }
}
