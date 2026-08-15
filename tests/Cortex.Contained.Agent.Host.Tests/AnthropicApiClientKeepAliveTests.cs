using System.Net;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// <see cref="AnthropicApiClient"/> must translate the provider's liveness events into explicit
/// keep-alive chunks.
/// <para>
/// Root cause (2026-08-15): <c>message_start</c>, <c>ping</c> and <c>thinking_delta</c> all
/// yielded nothing, so extended thinking on a large context was byte-for-byte indistinguishable
/// from a hung socket and the inactivity watchdog killed two healthy 25-minute runs.
/// </para>
/// </summary>
public class AnthropicApiClientKeepAliveTests
{
    private static LlmCompletionRequest Request() => new()
    {
        Model = "claude-opus-5",
        Messages = [new LlmMessage { Role = "user", Content = "think hard" }],
        RequestId = "req-1",
        ConversationId = "conv-1",
    };

    private static ProviderState Provider() => new(new LlmProviderCredential
    {
        Name = "anthropic",
        Api = "anthropic-messages",
        BaseUrl = "https://api.anthropic.com",
        Kind = CredentialKind.ApiKey,
        ApiKey = "k",
        Models = ["claude-opus-5"],
    });

    private static async Task<List<LlmStreamChunk>> StreamAsync(string sse)
    {
        var handler = new RecordingHandler((HttpStatusCode.OK, sse));
        using var tokenManager = new OAuthTokenManager(NullLogger.Instance, metrics: null);
        var client = new AnthropicApiClient(
            new RecordingHttpClientFactory(handler), tokenManager, NullLogger.Instance);

        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in client.StreamAsync(Provider(), Request(), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    [Fact]
    public async Task MessageStart_EmitsAKeepAlive()
    {
        var chunks = await StreamAsync(
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":9}}}\n\n"
            + "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n");

        Assert.Contains(chunks, c => c.IsKeepAlive);
        Assert.Contains(chunks, c => c.ContentDelta == "hi");
    }

    [Fact]
    public async Task Ping_EmitsAKeepAlive()
    {
        var chunks = await StreamAsync(
            "data: {\"type\":\"ping\"}\n\n"
            + "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n");

        Assert.Contains(chunks, c => c.IsKeepAlive);
    }

    [Fact]
    public async Task ThinkingDelta_EmitsAKeepAlive()
    {
        // The critical one: extended thinking can run for minutes and produced literally no
        // signal before this fix.
        var chunks = await StreamAsync(
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hmm...\"}}\n\n"
            + "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"still hmm\"}}\n\n"
            + "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"done\"}}\n\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n");

        Assert.Equal(2, chunks.Count(c => c.IsKeepAlive));
        Assert.Contains(chunks, c => c.ContentDelta == "done");
    }

    [Fact]
    public async Task SignatureDelta_EmitsAKeepAlive()
    {
        // The signature that closes a thinking block arrives after the same long silence.
        var chunks = await StreamAsync(
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"signature_delta\",\"signature\":\"abc\"}}\n\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n");

        Assert.Contains(chunks, c => c.IsKeepAlive);
    }

    [Fact]
    public async Task KeepAliveChunks_CarryNoContentAndAreNotTerminal()
    {
        var chunks = await StreamAsync(
            "data: {\"type\":\"ping\"}\n\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n");

        var keepAlive = Assert.Single(chunks, c => c.IsKeepAlive);
        Assert.Null(keepAlive.ContentDelta);
        Assert.Null(keepAlive.ToolCallDeltas);
        Assert.Null(keepAlive.ErrorMessage);
        Assert.False(keepAlive.IsComplete);
    }

    [Fact]
    public async Task TextOnlyStream_EmitsNoKeepAlives()
    {
        var chunks = await StreamAsync(
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n");

        Assert.DoesNotContain(chunks, c => c.IsKeepAlive);
    }
}
