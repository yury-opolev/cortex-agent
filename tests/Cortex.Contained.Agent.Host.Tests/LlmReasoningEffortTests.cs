using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Reasoning effort controls how long a model deliberates before answering. The two provider
/// families disagree on both the wire shape and which models accept it, so the value is resolved
/// per family and per model rather than passed through blindly — sending it to a model that does
/// not accept it is an HTTP 400, and sending a level a model does not offer is another.
/// </summary>
public class LlmReasoningEffortTests
{
    // ── Parsing ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("max")]
    [InlineData("HIGH")]
    [InlineData("  high  ")]
    public void IsLevel_KnownLevels_AreAccepted(string value)
    {
        Assert.True(LlmReasoningEffort.IsLevel(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("extreme")]
    [InlineData("0.7")]
    public void IsLevel_UnknownValues_AreRejected(string? value)
    {
        Assert.False(LlmReasoningEffort.IsLevel(value));
    }

    // ── OpenAI / Responses family ─────────────────────────────────────

    [Theory]
    [InlineData("minimal", "minimal")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    public void ResolveForResponses_SupportedLevels_PassThrough(string requested, string expected)
    {
        Assert.Equal(expected, LlmReasoningEffort.ResolveForResponses("gpt-5.6-sol", requested));
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4.1")]
    [InlineData("llama-3.1-70b")]
    public void ResolveForResponses_NonReasoningModels_SendNothing(string model)
    {
        // reasoning.effort on a model that does not reason is an HTTP 400, which the failover
        // policy treats as terminal — so every turn would fail until config is edited.
        Assert.Null(LlmReasoningEffort.ResolveForResponses(model, "high"));
    }

    [Theory]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-5-mini")]
    [InlineData("o3")]
    [InlineData("o4-mini")]
    [InlineData("gpt-5.3-codex")]
    public void ResolveForResponses_ReasoningModels_SendEffort(string model)
    {
        Assert.Equal("high", LlmReasoningEffort.ResolveForResponses(model, "high"));
    }

    [Fact]
    public void ResolveForResponses_MinimalOnNonGpt5ReasoningModel_IsClampedToLow()
    {
        // "minimal" is a gpt-5-family level; the o-series rejects it.
        Assert.Equal("low", LlmReasoningEffort.ResolveForResponses("o3", "minimal"));
    }

    [Fact]
    public void ResolveForResponses_MaxIsClampedToHigh()
    {
        // The Responses API tops out at "high"; "max" is an Anthropic level.
        Assert.Equal("high", LlmReasoningEffort.ResolveForResponses("gpt-5.6-sol", "max"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void ResolveForResponses_NoOrInvalidRequest_SendsNothing(string? requested)
    {
        Assert.Null(LlmReasoningEffort.ResolveForResponses("gpt-5.6-sol", requested));
    }

    // ── Anthropic family ──────────────────────────────────────────────

    [Theory]
    [InlineData("claude-opus-4-8")]
    [InlineData("claude-opus-4-6")]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("claude-opus-4.8")]
    [InlineData("claude-sonnet-4.6")]
    public void ResolveForAnthropic_SupportedModels_SendEffort(string model)
    {
        // Both the hyphenated (direct Anthropic) and dotted (catalog) ID forms must match.
        Assert.Equal("high", LlmReasoningEffort.ResolveForAnthropic("anthropic-messages", model, "high"));
    }

    [Fact]
    public void ResolveForAnthropic_CopilotServedClaude_SendsNothing()
    {
        // Copilot's Messages surface never receives an anthropic-beta header, and
        // output_config.effort without its gating beta is an HTTP 400 — which the failover policy
        // treats as terminal, so the turn would fail outright with no retry.
        Assert.Null(LlmReasoningEffort.ResolveForAnthropic("github-copilot-api", "claude-opus-4.8", "high"));
    }

    [Theory]
    [InlineData("claude-haiku-4-5")]
    [InlineData("claude-3-5-sonnet")]
    [InlineData("claude-sonnet-4-0")]
    public void ResolveForAnthropic_UnsupportedModels_SendNothing(string model)
    {
        // Sending output_config.effort to a model that does not accept it is an HTTP 400.
        Assert.Null(LlmReasoningEffort.ResolveForAnthropic("anthropic-messages", model, "high"));
    }

    [Fact]
    public void ResolveForAnthropic_MaxOnOpus_IsHonoured()
    {
        Assert.Equal("max", LlmReasoningEffort.ResolveForAnthropic("anthropic-messages", "claude-opus-4-8", "max"));
    }

    [Fact]
    public void ResolveForAnthropic_MaxOnNonOpus_IsClampedToHigh()
    {
        // Only Opus offers "max"; asking for it on Sonnet must degrade rather than fail.
        Assert.Equal("high", LlmReasoningEffort.ResolveForAnthropic("anthropic-messages", "claude-sonnet-4-6", "max"));
    }

    [Fact]
    public void ResolveForAnthropic_MinimalIsClampedToLow()
    {
        // Anthropic's lowest level is "low"; "minimal" is an OpenAI level.
        Assert.Equal("low", LlmReasoningEffort.ResolveForAnthropic("anthropic-messages", "claude-opus-4-8", "minimal"));
    }

    [Fact]
    public void ResolveForAnthropic_NoRequest_SendsNothing()
    {
        Assert.Null(LlmReasoningEffort.ResolveForAnthropic("anthropic-messages", "claude-opus-4-8", null));
    }
}

/// <summary>Verifies the resolved effort reaches each provider in its own wire shape.</summary>
public class LlmReasoningEffortWireTests
{
    private const string SeededBearer = "seeded-bearer";

    private static LlmCompletionRequest BuildRequest(string model, string? effort) => new()
    {
        Model = model,
        Messages = [new LlmMessage { Role = "user", Content = "hello" }],
        RequestId = "req-1",
        ConversationId = "conv-1",
        ReasoningEffort = effort,
    };

    // ── OpenAI Responses: reasoning.effort ────────────────────────────

    [Fact]
    public void ResponsesRequest_WithEffort_SerializesReasoningEffort()
    {
        var body = OpenAiResponsesRequestMapper.Build(BuildRequest("gpt-5.6-sol", "high"));
        var json = System.Text.Json.JsonSerializer.Serialize(
            body, Cortex.Contained.Agent.Host.Llm.Providers.ProviderClientHelpers.JsonOptions);

        Assert.Contains("\"reasoning\":{\"effort\":\"high\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsesRequest_WithoutEffort_OmitsReasoning()
    {
        var body = OpenAiResponsesRequestMapper.Build(BuildRequest("gpt-5.6-sol", null));
        var json = System.Text.Json.JsonSerializer.Serialize(
            body, Cortex.Contained.Agent.Host.Llm.Providers.ProviderClientHelpers.JsonOptions);

        Assert.DoesNotContain("reasoning", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsesRequest_MaxEffort_IsClampedToHighOnTheWire()
    {
        var body = OpenAiResponsesRequestMapper.Build(BuildRequest("gpt-5.6-sol", "max"));
        var json = System.Text.Json.JsonSerializer.Serialize(
            body, Cortex.Contained.Agent.Host.Llm.Providers.ProviderClientHelpers.JsonOptions);

        Assert.Contains("\"effort\":\"high\"", json, StringComparison.Ordinal);
    }

    // ── Anthropic: output_config.effort + beta header ─────────────────

    private const string AnthropicResponseJson =
        """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""";

    private static LlmCredentials BuildAnthropicCredentials(string model) => new()
    {
        Providers =
        [
            new LlmProviderCredential
            {
                Name = "anthropic",
                Api = "anthropic-messages",
                BaseUrl = "https://api.anthropic.com",
                Kind = CredentialKind.ApiKey,
                ApiKey = "k",
                Models = [model],
            },
        ],
    };

    [Fact]
    public async Task AnthropicRequest_SupportedModelWithEffort_SendsOutputConfigAndBetaHeader()
    {
        const string model = "claude-opus-4-8";
        var handler = new RecordingHandler((System.Net.HttpStatusCode.OK, AnthropicResponseJson));
        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildAnthropicCredentials(model));

        await client.CompleteAsync(BuildRequest(model, "max"), CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Contains("\"output_config\":{\"effort\":\"max\"}", sent.Body, StringComparison.Ordinal);
        Assert.Contains(
            LlmReasoningEffort.AnthropicBetaHeader,
            sent.Headers.GetValueOrDefault("anthropic-beta") ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicRequest_UnsupportedModel_OmitsOutputConfigAndBetaHeader()
    {
        const string model = "claude-haiku-4-5";
        var handler = new RecordingHandler((System.Net.HttpStatusCode.OK, AnthropicResponseJson));
        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildAnthropicCredentials(model));

        await client.CompleteAsync(BuildRequest(model, "high"), CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.DoesNotContain("output_config", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            LlmReasoningEffort.AnthropicBetaHeader,
            sent.Headers.GetValueOrDefault("anthropic-beta") ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicRequest_NoEffortRequested_OmitsOutputConfig()
    {
        const string model = "claude-opus-4-8";
        var handler = new RecordingHandler((System.Net.HttpStatusCode.OK, AnthropicResponseJson));
        var client = new DirectLlmClient(
            new RecordingHttpClientFactory(handler),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DirectLlmClient>.Instance);
        client.ConfigureCredentials(BuildAnthropicCredentials(model));

        await client.CompleteAsync(BuildRequest(model, null), CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.DoesNotContain("output_config", sent.Body, StringComparison.Ordinal);
    }
}
