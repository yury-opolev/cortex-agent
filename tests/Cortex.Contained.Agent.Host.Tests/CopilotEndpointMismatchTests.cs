using System.Net;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Llm.Providers.Copilot;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Copilot routes a model to Responses or Chat Completions from endpoint metadata pushed by the
/// Bridge. That metadata is a snapshot, so when Copilot moves a model (new models such as
/// gpt-5.6-sol are Responses-only and answer 400 on /chat/completions) a stale snapshot pins every
/// call to an endpoint the model no longer serves, and the turn fails until someone re-saves
/// settings. coda avoids this by refetching /models live; the Agent Host has no credentials to do
/// that, so it recovers from the rejection itself and remembers the correction.
/// </summary>
public class CopilotEndpointMismatchTests
{
    private const string Model = "gpt-5.6-sol";
    private const string SeededBearer = "seeded-bearer";

    private const string MismatchBody =
        """{"error":{"message":"The model `gpt-5.6-sol` is not supported on this endpoint.","code":"model_not_supported"}}""";

    private const string ResponsesResponseJson =
        """{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi"}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}""";

    private const string ChatResponseJson =
        """{"choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    private static LlmCompletionRequest BuildRequest() => new()
    {
        Model = Model,
        Messages = [new LlmMessage { Role = "user", Content = "hello" }],
        RequestId = "req-1",
        ConversationId = "conv-1",
    };

    private static LlmCredentials BuildCopilotCredentials(params string[] supportedEndpoints) => new()
    {
        Providers =
        [
            new LlmProviderCredential
            {
                Name = "github-copilot",
                Api = "github-copilot-api",
                BaseUrl = "https://api.githubcopilot.com",
                Kind = CredentialKind.GitHubCopilotBearer,
                AccessToken = SeededBearer,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
                Models = [Model],
                ModelMetadata = [new LlmModelMetadata { Id = Model, SupportedEndpoints = supportedEndpoints }],
            },
        ],
    };

    // ── Pure detection ────────────────────────────────────────────────

    [Theory]
    [InlineData("The model `gpt-5.6-sol` is not supported on this endpoint.")]
    [InlineData("""{"error":{"code":"model_not_supported"}}""")]
    [InlineData("This model does not support chat completions.")]
    [InlineData("unsupported_endpoint for the requested model")]
    public void IsMismatch_EndpointRejectionBodies_AreDetected(string body)
    {
        Assert.True(CopilotEndpointMismatch.IsMismatch(400, body));
    }

    [Theory]
    [InlineData(400, "context_length_exceeded: too many tokens")]
    [InlineData(400, "invalid_request_error: messages must not be empty")]
    [InlineData(429, "The model is not supported on this endpoint.")]
    [InlineData(500, "The model is not supported on this endpoint.")]
    [InlineData(400, "")]
    public void IsMismatch_UnrelatedFailures_AreNotDetected(int status, string body)
    {
        Assert.False(CopilotEndpointMismatch.IsMismatch(status, body));
    }

    [Fact]
    public void Alternate_SwapsOnlyBetweenResponsesAndChat()
    {
        Assert.Equal(CopilotEndpoint.Responses, CopilotEndpointMismatch.Alternate(CopilotEndpoint.ChatCompletions));
        Assert.Equal(CopilotEndpoint.ChatCompletions, CopilotEndpointMismatch.Alternate(CopilotEndpoint.Responses));

        // Messages models are dispatched to the Anthropic client by the facade, so there is no
        // in-client alternate for them.
        Assert.Null(CopilotEndpointMismatch.Alternate(CopilotEndpoint.Messages));
    }

    // ── Recovery, non-streaming ───────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_StaleChatMetadataRejected_RetriesOnResponsesAndSucceeds()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.BadRequest, MismatchBody),
            (HttpStatusCode.OK, ResponsesResponseJson));

        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCopilotCredentials("/chat/completions"));

        var result = await client.CompleteAsync(BuildRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hi", result.Content);
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/chat/completions", handler.Requests[0].AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/responses", handler.Requests[1].AbsolutePath, StringComparison.Ordinal);

        // The retry must use the OTHER wire shape, not resend the chat body.
        Assert.Contains("\"input\"", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_StaleResponsesMetadataRejected_RetriesOnChatAndSucceeds()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.BadRequest, MismatchBody),
            (HttpStatusCode.OK, ChatResponseJson));

        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCopilotCredentials("/responses"));

        var result = await client.CompleteAsync(BuildRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hi", result.Content);
        Assert.EndsWith("/responses", handler.Requests[0].AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/chat/completions", handler.Requests[1].AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_AfterRecovery_SubsequentCallsGoStraightToTheWorkingEndpoint()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.BadRequest, MismatchBody),
            (HttpStatusCode.OK, ResponsesResponseJson));

        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCopilotCredentials("/chat/completions"));

        await client.CompleteAsync(BuildRequest(), CancellationToken.None);
        var second = await client.CompleteAsync(BuildRequest(), CancellationToken.None);

        Assert.True(second.Success);
        Assert.Equal(3, handler.Requests.Count);

        // Third request is the second turn: it must skip the known-bad endpoint entirely.
        Assert.EndsWith("/responses", handler.Requests[2].AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_UnrelatedBadRequest_IsNotRetriedOnAnotherEndpoint()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.BadRequest, "context_length_exceeded: prompt is too long"));

        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCopilotCredentials("/chat/completions"));

        var result = await client.CompleteAsync(BuildRequest(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CompleteAsync_FreshMetadataPushed_ClearsALearnedOverride()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.BadRequest, MismatchBody),
            (HttpStatusCode.OK, ResponsesResponseJson));

        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCopilotCredentials("/chat/completions"));
        await client.CompleteAsync(BuildRequest(), CancellationToken.None);

        // The Bridge re-pushes authoritative metadata — it must win over what we learned.
        client.ConfigureCredentials(BuildCopilotCredentials("/chat/completions"));
        await client.CompleteAsync(BuildRequest(), CancellationToken.None);

        // Three calls in total: chat (rejected), responses (recovered), then chat again because
        // the re-push dropped the learned override.
        Assert.Equal(3, handler.Requests.Count);
        Assert.EndsWith("/chat/completions", handler.Requests[^1].AbsolutePath, StringComparison.Ordinal);
    }

    // ── Recovery, streaming ───────────────────────────────────────────

    [Fact]
    public async Task StreamCompleteAsync_StaleChatMetadataRejected_RetriesOnResponsesAndStreams()
    {
        const string responsesSse =
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hi\"}\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}\n\n";

        var handler = new RecordingHandler(
            (HttpStatusCode.BadRequest, MismatchBody),
            (HttpStatusCode.OK, responsesSse));

        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCopilotCredentials("/chat/completions"));

        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in client.StreamCompleteAsync(BuildRequest(), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Contains(chunks, c => c.ContentDelta == "hi");
        Assert.DoesNotContain(chunks, c => !string.IsNullOrEmpty(c.ErrorMessage));
        Assert.EndsWith("/chat/completions", handler.Requests[0].AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/responses", handler.Requests[1].AbsolutePath, StringComparison.Ordinal);
    }
}
