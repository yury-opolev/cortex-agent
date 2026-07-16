using Cortex.Contained.Agent.Host.Llm.Providers.Copilot;

namespace Cortex.Contained.Agent.Host.Tests;

public class CopilotEndpointResolverTests
{
    // ── Fixed priority: Responses > Messages > ChatCompletions ────────────

    [Theory]
    [InlineData("/chat/completions,/responses", CopilotEndpoint.Responses)]
    [InlineData("/responses,/chat/completions", CopilotEndpoint.Responses)]
    [InlineData("/v1/messages,/chat/completions", CopilotEndpoint.Messages)]
    [InlineData("/chat/completions,/v1/messages", CopilotEndpoint.Messages)]
    [InlineData("/chat/completions", CopilotEndpoint.ChatCompletions)]
    [InlineData("/v1/chat/completions", CopilotEndpoint.ChatCompletions)]
    [InlineData("", CopilotEndpoint.ChatCompletions)]
    [InlineData("ws:/responses", CopilotEndpoint.ChatCompletions)]
    // `expected` is boxed as `object` because CopilotEndpoint is internal: an internal
    // parameter type on a public xUnit theory method would be less accessible than the
    // method (CS0051). The enum is unboxed inside the body, which InternalsVisibleTo permits.
    public void Resolve_SelectsEndpointByFixedPriority(string csv, object expected)
    {
        Assert.Equal((CopilotEndpoint)expected, CopilotEndpointResolver.Resolve(Parse(csv)));
    }

    [Theory]
    [InlineData("/responses")]
    [InlineData("/v1/responses")]
    public void Resolve_RecognizesResponsesVariants(string endpoint)
    {
        Assert.Equal(CopilotEndpoint.Responses, CopilotEndpointResolver.Resolve([endpoint]));
    }

    [Fact]
    public void Resolve_RecognizesMessagesEndpoint()
    {
        Assert.Equal(CopilotEndpoint.Messages, CopilotEndpointResolver.Resolve(["/v1/messages"]));
    }

    [Theory]
    [InlineData("/chat/completions")]
    [InlineData("/v1/chat/completions")]
    public void Resolve_RecognizesChatCompletionsVariants(string endpoint)
    {
        Assert.Equal(CopilotEndpoint.ChatCompletions, CopilotEndpointResolver.Resolve([endpoint]));
    }

    // ── Priority holds regardless of array order ─────────────────────────

    [Fact]
    public void Resolve_ResponsesWinsOverMessagesAndChat_RegardlessOfOrder()
    {
        var forward = CopilotEndpointResolver.Resolve(["/chat/completions", "/v1/messages", "/responses"]);
        var reverse = CopilotEndpointResolver.Resolve(["/responses", "/v1/messages", "/chat/completions"]);

        Assert.Equal(CopilotEndpoint.Responses, forward);
        Assert.Equal(CopilotEndpoint.Responses, reverse);
    }

    [Fact]
    public void Resolve_MessagesWinsOverChat_RegardlessOfOrder()
    {
        var forward = CopilotEndpointResolver.Resolve(["/chat/completions", "/v1/messages"]);
        var reverse = CopilotEndpointResolver.Resolve(["/v1/messages", "/chat/completions"]);

        Assert.Equal(CopilotEndpoint.Messages, forward);
        Assert.Equal(CopilotEndpoint.Messages, reverse);
    }

    // ── Websocket-only variant is ignored ────────────────────────────────

    [Fact]
    public void Resolve_WebsocketOnlyResponses_FallsBackToChat()
    {
        Assert.Equal(CopilotEndpoint.ChatCompletions, CopilotEndpointResolver.Resolve(["ws:/responses"]));
    }

    // ── Case-insensitive matching ────────────────────────────────────────

    [Theory]
    [InlineData("/RESPONSES", CopilotEndpoint.Responses)]
    [InlineData("/V1/Responses", CopilotEndpoint.Responses)]
    [InlineData("/V1/Messages", CopilotEndpoint.Messages)]
    [InlineData("/Chat/Completions", CopilotEndpoint.ChatCompletions)]
    public void Resolve_IsCaseInsensitive(string endpoint, object expected)
    {
        Assert.Equal((CopilotEndpoint)expected, CopilotEndpointResolver.Resolve([endpoint]));
    }

    // ── Missing / null / empty / unknown metadata → ChatCompletions ──────

    [Fact]
    public void Resolve_NullList_FallsBackToChat()
    {
        Assert.Equal(CopilotEndpoint.ChatCompletions, CopilotEndpointResolver.Resolve(null));
    }

    [Fact]
    public void Resolve_EmptyList_FallsBackToChat()
    {
        Assert.Equal(CopilotEndpoint.ChatCompletions, CopilotEndpointResolver.Resolve([]));
    }

    [Fact]
    public void Resolve_UnknownEndpoints_FallBackToChat()
    {
        Assert.Equal(
            CopilotEndpoint.ChatCompletions,
            CopilotEndpointResolver.Resolve(["/embeddings", "/audio/speech"]));
    }

    // ── Null entries in the list are tolerated ───────────────────────────

    [Fact]
    public void Resolve_NullEntries_AreIgnored()
    {
        Assert.Equal(
            CopilotEndpoint.Responses,
            CopilotEndpointResolver.Resolve([null!, "/responses", null!]));
    }

    [Fact]
    public void Resolve_OnlyNullEntries_FallBackToChat()
    {
        Assert.Equal(CopilotEndpoint.ChatCompletions, CopilotEndpointResolver.Resolve([null!]));
    }

    private static string[] Parse(string csv) =>
        csv.Length == 0 ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
