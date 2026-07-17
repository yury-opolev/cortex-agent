using System.Net;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests that <see cref="DirectLlmClient"/> routes a GitHub Copilot provider's direct calls by
/// live model endpoint metadata: <c>/v1/messages</c> models dispatch through the Anthropic wire
/// shape (Copilot base URL + bearer headers), <c>/responses</c> models through the OpenAI
/// Responses protocol, and a subsequent credential push with changed <c>SupportedEndpoints</c>
/// takes effect without a reconnect. Provider retry/failover ownership stays with the facade.
/// </summary>
public class DirectLlmClientCopilotRoutingTests
{
    private const string SeededBearer = "seeded-bearer";

    /// <summary>Non-streaming Anthropic Messages response body: one assistant text "hi".</summary>
    private const string AnthropicResponseJson =
        """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""";

    /// <summary>Anthropic Messages SSE: one text delta "hi" then a terminal message_delta.</summary>
    private const string AnthropicStreamSse =
        "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n";

    /// <summary>Non-streaming Responses body: one assistant output_text "hi".</summary>
    private const string ResponsesResponseJson =
        """{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi"}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}""";

    private const string Model = "gpt-5.6-sol";

    private static LlmCompletionRequest BuildRequest() => new()
    {
        Model = Model,
        Messages = [new LlmMessage { Role = "user", Content = "hello" }],
        RequestId = "req-1",
        ConversationId = "conv-1",
    };

    private static LlmCredentials BuildCopilotCredentials(IReadOnlyList<string> supportedEndpoints) => new()
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
                ApiKey = null,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
                Models = [Model],
                ModelMetadata =
                [
                    new LlmModelMetadata { Id = Model, SupportedEndpoints = supportedEndpoints },
                ],
            },
        ],
    };

    [Fact]
    public async Task CompleteAsync_CopilotMessagesMetadata_DispatchesAnthropicWireToMessagesEndpoint()
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, AnthropicResponseJson));
        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCopilotCredentials(["/v1/messages"]));

        var result = await client.CompleteAsync(BuildRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hi", result.Content);

        var sent = Assert.Single(handler.Requests);

        // Anthropic Messages endpoint on the Copilot base URL, no OAuth beta query.
        Assert.StartsWith("https://api.githubcopilot.com/", sent.Url);
        Assert.EndsWith("/v1/messages", sent.AbsolutePath);
        Assert.DoesNotContain("beta=true", sent.Url);

        // Anthropic wire shape (system/messages), not OpenAI Chat/Responses.
        Assert.Contains("\"messages\"", sent.Body);
        Assert.DoesNotContain("\"input\"", sent.Body);

        // Copilot bearer headers + anthropic-version; no x-api-key / anthropic-beta.
        Assert.Equal("Bearer", sent.Authorization?.Scheme);
        Assert.Equal(SeededBearer, sent.Authorization?.Parameter);
        Assert.Equal("conversation-edits", sent.Headers.GetValueOrDefault("Openai-Intent"));
        Assert.Equal("user", sent.Headers.GetValueOrDefault("x-initiator"));
        Assert.Equal("2026-06-01", sent.Headers.GetValueOrDefault("X-GitHub-Api-Version"));
        Assert.Equal("2023-06-01", sent.Headers.GetValueOrDefault("anthropic-version"));
        Assert.False(sent.Headers.ContainsKey("x-api-key"));
        Assert.False(sent.Headers.ContainsKey("anthropic-beta"));
    }

    [Fact]
    public async Task StreamCompleteAsync_CopilotMessagesMetadata_DispatchesAnthropicWireToMessagesEndpoint()
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, AnthropicStreamSse));
        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCopilotCredentials(["/v1/messages"]));

        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in client.StreamCompleteAsync(BuildRequest(), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.DoesNotContain(chunks, c => !string.IsNullOrEmpty(c.ErrorMessage));
        Assert.Contains(chunks, c => c.ContentDelta == "hi");

        var sent = Assert.Single(handler.Requests);
        Assert.EndsWith("/v1/messages", sent.AbsolutePath);
        Assert.Equal("Bearer", sent.Authorization?.Scheme);
        Assert.Equal(SeededBearer, sent.Authorization?.Parameter);
        Assert.Equal("2026-06-01", sent.Headers.GetValueOrDefault("X-GitHub-Api-Version"));
    }

    [Fact]
    public async Task CompleteAsync_CopilotResponsesMetadata_DispatchesResponsesEndpoint()
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, ResponsesResponseJson));
        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildCopilotCredentials(["/responses"]));

        var result = await client.CompleteAsync(BuildRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hi", result.Content);

        var sent = Assert.Single(handler.Requests);
        Assert.EndsWith("/responses", sent.AbsolutePath);
        Assert.Contains("\"input\"", sent.Body);
        Assert.DoesNotContain("\"messages\"", sent.Body);
    }

    [Fact]
    public async Task ConfigureCredentials_SecondPushChangesSupportedEndpoints_TakesEffectWithoutReconnect()
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, ResponsesResponseJson));
        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);

        // First push: the model only supports Chat Completions.
        client.ConfigureCredentials(BuildCopilotCredentials(["/chat/completions"]));

        // Second push (same provider — the Bridge re-mints the bearer in place, no reconnect):
        // the model now advertises the Responses endpoint. The frozen-metadata bug would keep
        // routing to /chat/completions; the fix must pick up the new endpoint.
        client.ConfigureCredentials(BuildCopilotCredentials(["/responses"]));

        var result = await client.CompleteAsync(BuildRequest(), CancellationToken.None);

        Assert.True(result.Success);
        var sent = Assert.Single(handler.Requests);
        Assert.EndsWith("/responses", sent.AbsolutePath);
    }
}
