using System.Net;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>A request with <c>PinnedProvider</c> is answered by the provider that lists its model or not at all (labelling must not change model family).</summary>
public class DirectLlmClientPinnedProviderTests
{
    private const string Model = "gpt-5-chat";

    private static LlmCompletionRequest Request(string model = Model, bool pinned = false) => new()
    {
        Model = model,
        Messages = [new LlmMessage { Role = "user", Content = "hello" }],
        RequestId = "req-1",
        ConversationId = "conv-1",
        PinnedProvider = pinned,
    };

    private static DirectLlmClient Client(RecordingHandler handler, params string[] providers)
    {
        var client = new DirectLlmClient(new RecordingHttpClientFactory(handler), NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(new LlmCredentials
        {
            Providers = providers.Select((name, i) => new LlmProviderCredential
            {
                Name = name,
                Api = "openai-completions",
                BaseUrl = "https://example.invalid",
                Kind = CredentialKind.ApiKey,
                ApiKey = "k",
                Models = i == 0 ? [Model] : ["other-model"],
            }).ToList(),
        });
        return client;
    }

    [Fact]
    public async Task CompleteAsync_ProviderRejectsAndRequestIsNotPinned_FailsOverToTheNextProvider()
    {
        var handler = new RecordingHandler((HttpStatusCode.Forbidden, "no"), (HttpStatusCode.OK, "{}"));

        await Client(handler, "primary", "secondary").CompleteAsync(Request(), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CompleteAsync_ProviderRejectsAndRequestIsPinned_DoesNotFailOver()
    {
        var handler = new RecordingHandler((HttpStatusCode.Forbidden, "no"), (HttpStatusCode.OK, "{}"));

        var result = await Client(handler, "primary", "secondary").CompleteAsync(Request(pinned: true), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(handler.Requests);
        Assert.Equal("primary", result.ProviderId);
    }

    [Fact]
    public async Task CompleteAsync_PinnedTransientError_IsNotRetriedInsideTheClient()
    {
        var handler = new RecordingHandler((HttpStatusCode.TooManyRequests, "slow down"));

        var result = await Client(handler, "primary").CompleteAsync(Request(pinned: true), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CompleteAsync_PinnedModelNoProviderLists_FailsWithoutCallingTheFallbackDefault()
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, "{}"));

        var result = await Client(handler, "primary").CompleteAsync(Request("unlisted-model", pinned: true), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
        Assert.Equal("LlmProviderUnavailable", result.ErrorCode);
    }
}
