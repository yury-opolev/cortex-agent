using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Shared HTTP test doubles for the Copilot endpoint-routing tests. A
/// <see cref="RecordingHandler"/> returns a scripted sequence of responses (one per call)
/// and snapshots every request's absolute path, <c>Authorization</c> header, the full request
/// header set, and the serialized body — captured at send time, before the client disposes the
/// request. It also tracks each response body so tests can assert responses were disposed
/// (no leak across a 401 retry branch).
/// </summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly (HttpStatusCode Status, string Body)[] steps;
    private int index;

    public RecordingHandler(params (HttpStatusCode Status, string Body)[] steps) => this.steps = steps;

    public List<RecordedRequest> Requests { get; } = [];

    public List<TrackedStringContent> CreatedResponses { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Read the (buffered) body with a non-cancellable token so it is captured even when the
        // caller cancels mid-flight, then record the request before honoring cancellation — the
        // client may dispose the request before the test inspects it.
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false)
            : string.Empty;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in request.Headers)
        {
            headers[key] = string.Join(",", values);
        }

        this.Requests.Add(new RecordedRequest(
            request.RequestUri!.AbsoluteUri,
            request.RequestUri!.AbsolutePath,
            request.Headers.Authorization,
            headers,
            body));

        cancellationToken.ThrowIfCancellationRequested();

        var step = this.steps[Math.Min(this.index, this.steps.Length - 1)];
        this.index++;

        var content = new TrackedStringContent(step.Body);
        this.CreatedResponses.Add(content);
        return new HttpResponseMessage(step.Status) { Content = content };
    }
}

/// <summary>Immutable snapshot of a sent request captured by <see cref="RecordingHandler"/>.</summary>
internal sealed record RecordedRequest(
    string Url,
    string AbsolutePath,
    AuthenticationHeaderValue? Authorization,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

/// <summary>String content that records when it is disposed, to detect leaked responses.</summary>
internal sealed class TrackedStringContent : StringContent
{
    public TrackedStringContent(string body)
        : base(body, Encoding.UTF8, "application/json")
    {
    }

    public bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        this.IsDisposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Minimal <see cref="IHttpClientFactory"/> that hands every named client the same shared
/// <see cref="HttpMessageHandler"/> without disposing it when the <see cref="HttpClient"/> is disposed.
/// </summary>
internal sealed class RecordingHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler handler;

    public RecordingHttpClientFactory(HttpMessageHandler handler) => this.handler = handler;

    public HttpClient CreateClient(string name) => new(this.handler, disposeHandler: false);
}
