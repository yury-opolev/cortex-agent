using Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Cached prompt tokens are reported by OpenAI with the opposite convention to Anthropic:
/// <c>input_tokens</c>/<c>prompt_tokens</c> is the TOTAL and the cached count is a SUBSET of it.
/// Reporting the total as fresh input overstates real prompt cost and hides whether the cache is
/// working at all, so the cached part is split out into <c>CacheReadTokens</c>.
/// </summary>
public class OpenAiCachedTokenUsageTests
{
    // ── Responses API (input_tokens_details.cached_tokens) ────────────

    [Fact]
    public void ResponsesUsage_WithCachedTokens_SplitsCachedOutOfPromptTokens()
    {
        var usage = new OpenAiResponsesUsage
        {
            InputTokens = 1000,
            OutputTokens = 50,
            TotalTokens = 1050,
            InputTokensDetails = new OpenAiResponsesTokenDetails { CachedTokens = 800 },
        };

        var mapped = usage.ToTokenUsage();

        Assert.Equal(200, mapped.PromptTokens);
        Assert.Equal(800, mapped.CacheReadTokens);
        Assert.Equal(50, mapped.CompletionTokens);
        Assert.Equal(1050, mapped.TotalTokens);
    }

    [Fact]
    public void ResponsesUsage_WithoutCachedTokens_ReportsPromptTokensUnchanged()
    {
        var usage = new OpenAiResponsesUsage { InputTokens = 300, OutputTokens = 20, TotalTokens = 320 };

        var mapped = usage.ToTokenUsage();

        Assert.Equal(300, mapped.PromptTokens);
        Assert.Equal(0, mapped.CacheReadTokens);
    }

    [Fact]
    public void ResponsesUsage_CachedExceedsInput_ClampsInsteadOfGoingNegative()
    {
        // Defensive: a provider bug must never produce negative prompt tokens downstream.
        var usage = new OpenAiResponsesUsage
        {
            InputTokens = 100,
            OutputTokens = 5,
            TotalTokens = 105,
            InputTokensDetails = new OpenAiResponsesTokenDetails { CachedTokens = 500 },
        };

        var mapped = usage.ToTokenUsage();

        Assert.Equal(0, mapped.PromptTokens);
        Assert.Equal(100, mapped.CacheReadTokens);
    }

    [Fact]
    public void ResponsesUsage_ParsedFromJson_ReadsCachedTokens()
    {
        const string json =
            """{"input_tokens":1000,"output_tokens":50,"total_tokens":1050,"input_tokens_details":{"cached_tokens":800}}""";

        var usage = System.Text.Json.JsonSerializer.Deserialize<OpenAiResponsesUsage>(
            json, Cortex.Contained.Agent.Host.Llm.Providers.ProviderClientHelpers.JsonOptions);

        var mapped = usage!.ToTokenUsage();

        Assert.Equal(200, mapped.PromptTokens);
        Assert.Equal(800, mapped.CacheReadTokens);
    }

    // ── Chat Completions (prompt_tokens_details.cached_tokens) ────────

    [Fact]
    public void ChatUsage_WithCachedTokens_SplitsCachedOutOfPromptTokens()
    {
        var usage = new OpenAiUsage
        {
            PromptTokens = 900,
            CompletionTokens = 40,
            TotalTokens = 940,
            PromptTokensDetails = new OpenAiResponsesTokenDetails { CachedTokens = 600 },
        };

        var mapped = usage.ToTokenUsage();

        Assert.Equal(300, mapped.PromptTokens);
        Assert.Equal(600, mapped.CacheReadTokens);
        Assert.Equal(40, mapped.CompletionTokens);
    }

    [Fact]
    public void ChatUsage_ParsedFromJson_ReadsCachedTokens()
    {
        const string json =
            """{"prompt_tokens":900,"completion_tokens":40,"total_tokens":940,"prompt_tokens_details":{"cached_tokens":600}}""";

        var usage = System.Text.Json.JsonSerializer.Deserialize<OpenAiUsage>(
            json, Cortex.Contained.Agent.Host.Llm.Providers.ProviderClientHelpers.JsonOptions);

        var mapped = usage!.ToTokenUsage();

        Assert.Equal(300, mapped.PromptTokens);
        Assert.Equal(600, mapped.CacheReadTokens);
    }

    [Fact]
    public void ChatUsage_WithoutDetails_ReportsPromptTokensUnchanged()
    {
        var usage = new OpenAiUsage { PromptTokens = 120, CompletionTokens = 8, TotalTokens = 128 };

        var mapped = usage.ToTokenUsage();

        Assert.Equal(120, mapped.PromptTokens);
        Assert.Equal(0, mapped.CacheReadTokens);
    }

    // ── Context occupancy ─────────────────────────────────────────────

    [Fact]
    public void TotalInputTokens_CountsCachedTokensToo()
    {
        // Splitting cached out of PromptTokens must not shrink apparent context occupancy:
        // OpenAI caches automatically above ~1024 tokens and typically covers most of a long
        // prefix, so comparing PromptTokens alone against a window would stop compaction firing.
        var usage = new OpenAiResponsesUsage
        {
            InputTokens = 100_000,
            OutputTokens = 100,
            TotalTokens = 100_100,
            InputTokensDetails = new OpenAiResponsesTokenDetails { CachedTokens = 95_000 },
        }.ToTokenUsage();

        Assert.Equal(5_000, usage.PromptTokens);
        Assert.Equal(100_000, usage.TotalInputTokens);
    }

    [Fact]
    public void TotalInputTokens_IncludesAnthropicCacheWrites()
    {
        var usage = new Cortex.Contained.Contracts.Llm.LlmTokenUsage
        {
            PromptTokens = 10,
            CacheWriteTokens = 200,
            CacheReadTokens = 3_000,
        };

        Assert.Equal(3_210, usage.TotalInputTokens);
    }
}
