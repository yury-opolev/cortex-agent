using System.Net;
using System.Text;
using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Tts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Speech.Tests.Tts;

public sealed class RoestDanishTtsProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ =>
            new HttpResponseMessage(HttpStatusCode.OK);
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.LastRequest = request;
            return Task.FromResult(this.Responder(request));
        }
    }

    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] data;
        private readonly int maxPerRead;
        private int pos;

        public ChunkedStream(byte[] data, int maxPerRead)
        {
            this.data = data;
            this.maxPerRead = maxPerRead;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (this.pos >= this.data.Length)
            {
                return 0;
            }
            var n = Math.Min(this.maxPerRead, Math.Min(count, this.data.Length - this.pos));
            Array.Copy(this.data, this.pos, buffer, offset, n);
            this.pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => this.data.Length;
        public override long Position { get => this.pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static RoestDanishTtsProvider NewProvider(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") },
            NullLoggerFactory.Instance);

    [Fact]
    public void Voices_AreMicMaleAndNicFemale_InDanish()
    {
        // CoRal speaker genders verified by pitch analysis: mic=MALE, nic=FEMALE
        // (the upstream spike README had them reversed).
        var provider = NewProvider(new StubHandler());
        var byName = provider.Voices.ToDictionary(v => v.Name, StringComparer.Ordinal);
        Assert.Equal("roest-da", provider.Name);
        Assert.True(byName.ContainsKey("mic"));
        Assert.True(byName.ContainsKey("nic"));
        Assert.Equal("da", byName["mic"].Language);
        Assert.Equal(VoiceGender.Male, byName["mic"].Gender);
        Assert.Equal(VoiceGender.Female, byName["nic"].Gender);
    }

    [Fact]
    public void OutputFormat_Is24kHzMono16Bit()
    {
        var provider = NewProvider(new StubHandler());
        Assert.Equal(AudioFormat.Kokoro, provider.OutputFormat);
        Assert.True(provider.SupportsStreaming);
    }

    [Fact]
    public async Task StreamingAsync_YieldsPcmChunksInOrder()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            },
        };
        var provider = NewProvider(handler);
        var received = new List<byte>();
        await foreach (var chunk in provider.SynthesizeStreamingAsync("Hej", "mic"))
        {
            received.AddRange(chunk);
        }
        Assert.Equal(payload, received.ToArray());
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("/synthesize/stream", handler.LastRequest!.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingAsync_MultipleReads_PreservesOrder()
    {
        var payload = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                // ChunkedStream returns at most 3 bytes per read, so the
                // 10-byte payload spans more than one ReadAsync call.
                Content = new StreamContent(new ChunkedStream(payload, maxPerRead: 3)),
            },
        };
        var provider = NewProvider(handler);
        var received = new List<byte>();
        var chunkCount = 0;
        await foreach (var chunk in provider.SynthesizeStreamingAsync("Hej", "mic"))
        {
            chunkCount++;
            received.AddRange(chunk);
        }
        Assert.Equal(payload, received.ToArray());
        Assert.True(chunkCount > 1, "payload should be delivered across multiple reads");
    }

    [Fact]
    public async Task StreamingAsync_OddSizedReads_YieldsOnlyEvenLengthChunks()
    {
        // 12-byte (even total) payload delivered 3 bytes per read, so raw reads
        // are 3,3,3,3 — all odd. Each yielded chunk must be sample-aligned
        // (even length) so per-chunk 24kHz→48kHz resampling never splits a
        // 16-bit sample.
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkedStream(payload, maxPerRead: 3)),
            },
        };
        var provider = NewProvider(handler);
        var received = new List<byte>();
        await foreach (var chunk in provider.SynthesizeStreamingAsync("Hej", "mic"))
        {
            Assert.Equal(0, chunk.Length % 2);
            received.AddRange(chunk);
        }
        Assert.Equal(payload, received.ToArray());
    }

    [Fact]
    public async Task SynthesizeAsync_BuffersWholeStream()
    {
        var payload = Encoding.ASCII.GetBytes("PCMDATA!");
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            },
        };
        var provider = NewProvider(handler);
        var all = await provider.SynthesizeAsync("Hej", "nic");
        Assert.Equal(payload, all);
    }

    [Fact]
    public async Task IsReady_TrueWhenHealthReportsModelLoaded()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"model_loaded":true,"device":"cuda","sample_rate":24000}"""),
            },
        };
        var provider = NewProvider(handler);
        Assert.True(await provider.CheckReadyAsync());
    }

    [Fact]
    public async Task IsReady_FalseWhenHealthUnavailable()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        };
        var provider = NewProvider(handler);
        Assert.False(await provider.CheckReadyAsync());
    }
}
