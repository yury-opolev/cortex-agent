using System.Net;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// End-to-end proof that a connection dropped WHILE the SSE body is being read is retried on
/// the same provider and failed over to the next one, exactly like a failure at the request
/// stage. Before <c>LlmStreamFault.Guard</c> the exception escaped the whole facade, so a
/// mid-stream reset ("Unable to read data from the transport connection") killed the turn.
/// </summary>
public class DirectLlmClientStreamFaultTests
{
    private const string Model = "gpt-5-chat";

    /// <summary>OpenAI chat SSE: one "hi" delta, a stop, then the terminal sentinel.</summary>
    private const string GoodSse =
        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    private static LlmCompletionRequest BuildRequest() => new()
    {
        Model = Model,
        Messages = [new LlmMessage { Role = "user", Content = "hello" }],
        RequestId = "req-1",
        ConversationId = "conv-1",
    };

    private static LlmCredentials BuildCredentials(params string[] providerNames)
    {
        var providers = providerNames.Select(name => new LlmProviderCredential
        {
            Name = name,
            Api = "openai-completions",
            BaseUrl = "https://example.invalid",
            Kind = CredentialKind.ApiKey,
            ApiKey = "k",
            Models = [Model],
        }).ToList();

        return new LlmCredentials { Providers = providers };
    }

    private static async Task<List<LlmStreamChunk>> StreamAsync(DirectLlmClient client)
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in client.StreamCompleteAsync(BuildRequest(), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    [Fact]
    public async Task StreamCompleteAsync_ConnectionDropsBeforeAnyContent_RetriesSameProviderAndSucceeds()
    {
        var handler = new ScriptedStreamHandler(
            StreamStep.Faults(new IOException("Unable to read data from the transport connection")),
            StreamStep.Ok(GoodSse));

        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCredentials("primary"));

        var chunks = await StreamAsync(client);

        Assert.Equal(2, handler.CallCount);
        Assert.Contains(chunks, c => c.ContentDelta == "hi");
        Assert.DoesNotContain(chunks, c => !string.IsNullOrEmpty(c.ErrorMessage));
    }

    [Fact]
    public async Task StreamCompleteAsync_ConnectionDropsBeforeAnyContent_FailsOverToNextProvider()
    {
        // Every attempt on the first provider drops; the second provider answers.
        var handler = new ScriptedStreamHandler(
            StreamStep.Faults(new IOException("reset")),
            StreamStep.Faults(new IOException("reset")),
            StreamStep.Faults(new IOException("reset")),
            StreamStep.Ok(GoodSse));

        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCredentials("primary", "secondary"));

        var chunks = await StreamAsync(client);

        Assert.Contains(chunks, c => c.ContentDelta == "hi");
        Assert.DoesNotContain(chunks, c => !string.IsNullOrEmpty(c.ErrorMessage));
    }

    [Fact]
    public async Task StreamCompleteAsync_ConnectionDropsAfterContent_SurfacesTerminalErrorWithoutThrowing()
    {
        // Content already reached the caller, so the turn cannot be re-run. The fault must still
        // arrive as a terminal chunk rather than an exception tearing down the agent loop.
        var handler = new ScriptedStreamHandler(
            StreamStep.FaultsAfter(
                "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n",
                new IOException("reset mid-stream")));

        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCredentials("primary"));

        var chunks = await StreamAsync(client);

        Assert.Contains(chunks, c => c.ContentDelta == "partial");
        var terminal = Assert.Single(chunks, c => !string.IsNullOrEmpty(c.ErrorMessage));
        Assert.True(terminal.IsComplete);
    }

    [Fact]
    public async Task StreamCompleteAsync_AllAttemptsDrop_ReportsErrorInsteadOfThrowing()
    {
        var handler = new ScriptedStreamHandler(StreamStep.Faults(new IOException("reset")));

        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCredentials("primary"));

        var chunks = await StreamAsync(client);

        var terminal = Assert.Single(chunks, c => !string.IsNullOrEmpty(c.ErrorMessage));
        Assert.True(terminal.IsComplete);
        Assert.Contains("reset", terminal.ErrorMessage, StringComparison.Ordinal);
    }
}

/// <summary>One scripted streaming response: a body, a fault, or a body then a fault.</summary>
internal sealed record StreamStep(string Body, Exception? Fault)
{
    internal static StreamStep Ok(string body) => new(body, null);

    internal static StreamStep Faults(Exception fault) => new(string.Empty, fault);

    internal static StreamStep FaultsAfter(string body, Exception fault) => new(body, fault);
}

/// <summary>
/// Returns a scripted sequence of SSE responses whose bodies can fault part-way through the
/// read — the one thing <see cref="RecordingHandler"/> cannot express, because it buffers a
/// complete string body. The last step repeats once the script is exhausted.
/// </summary>
internal sealed class ScriptedStreamHandler : HttpMessageHandler
{
    private readonly StreamStep[] steps;
    private int index;

    public ScriptedStreamHandler(params StreamStep[] steps) => this.steps = steps;

    public int CallCount => this.index;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var step = this.steps[Math.Min(this.index, this.steps.Length - 1)];
        this.index++;

        var content = new StreamContent(new FaultingStream(step.Body, step.Fault));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}

/// <summary>A read-only stream that serves <paramref name="body"/> then optionally throws.</summary>
internal sealed class FaultingStream : Stream
{
    private readonly byte[] payload;
    private readonly Exception? fault;
    private int position;

    public FaultingStream(string body, Exception? fault)
    {
        this.payload = System.Text.Encoding.UTF8.GetBytes(body);
        this.fault = fault;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => this.position;
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.position < this.payload.Length)
        {
            var count = Math.Min(buffer.Length, this.payload.Length - this.position);
            this.payload.AsSpan(this.position, count).CopyTo(buffer.Span);
            this.position += count;
            return ValueTask.FromResult(count);
        }

        return this.fault is not null
            ? ValueTask.FromException<int>(this.fault)
            : ValueTask.FromResult(0);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => this.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
