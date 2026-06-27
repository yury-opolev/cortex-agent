using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests;

public class ModelCatalogTests
{
    // ── ParseModelsDevJson ──────────────────────────────────────

    private const string SampleJson = """
        {
            "anthropic": {
                "id": "anthropic",
                "models": {
                    "claude-sonnet-4-20250514": {
                        "id": "claude-sonnet-4-20250514",
                        "name": "Claude Sonnet 4",
                        "limit": { "context": 200000, "output": 64000 }
                    },
                    "claude-3-5-haiku-20241022": {
                        "id": "claude-3-5-haiku-20241022",
                        "name": "Claude 3.5 Haiku",
                        "limit": { "context": 200000, "output": 8192 }
                    }
                }
            },
            "openai": {
                "id": "openai",
                "models": {
                    "gpt-4o": {
                        "id": "gpt-4o",
                        "name": "GPT-4o",
                        "limit": { "context": 128000, "output": 16384 }
                    }
                }
            },
            "github-copilot": {
                "id": "github-copilot",
                "models": {
                    "claude-sonnet-4": {
                        "id": "claude-sonnet-4",
                        "name": "Claude Sonnet 4",
                        "limit": { "context": 128000, "output": 16000 }
                    },
                    "gpt-4o": {
                        "id": "gpt-4o",
                        "name": "GPT-4o",
                        "limit": { "context": 64000, "output": 16384 }
                    }
                }
            }
        }
        """;

    [Fact]
    public void ParseModelsDevJson_ExtractsAllProviders()
    {
        var result = ModelCatalog.ParseModelsDevJson(SampleJson);

        Assert.Equal(3, result.Count);
        Assert.True(result.ContainsKey("anthropic"));
        Assert.True(result.ContainsKey("openai"));
        Assert.True(result.ContainsKey("github-copilot"));
    }

    [Fact]
    public void ParseModelsDevJson_ExtractsCorrectModelCount()
    {
        var result = ModelCatalog.ParseModelsDevJson(SampleJson);

        Assert.Equal(2, result["anthropic"].Count);
        Assert.Single(result["openai"]);
        Assert.Equal(2, result["github-copilot"].Count);
    }

    [Fact]
    public void ParseModelsDevJson_ExtractsCorrectLimits_Anthropic()
    {
        var result = ModelCatalog.ParseModelsDevJson(SampleJson);

        var limits = result["anthropic"]["claude-sonnet-4-20250514"];
        Assert.Equal(200_000, limits.ContextWindow);
        Assert.Equal(64_000, limits.MaxOutputTokens);
    }

    [Fact]
    public void ParseModelsDevJson_ExtractsCorrectLimits_OpenAi()
    {
        var result = ModelCatalog.ParseModelsDevJson(SampleJson);

        var limits = result["openai"]["gpt-4o"];
        Assert.Equal(128_000, limits.ContextWindow);
        Assert.Equal(16_384, limits.MaxOutputTokens);
    }

    [Fact]
    public void ParseModelsDevJson_CopilotLimits_DifferFromNativeProvider()
    {
        var result = ModelCatalog.ParseModelsDevJson(SampleJson);

        // Copilot has lower context than native Anthropic for the same model family
        var copilot = result["github-copilot"]["gpt-4o"];
        var native = result["openai"]["gpt-4o"];

        Assert.Equal(64_000, copilot.ContextWindow);
        Assert.Equal(128_000, native.ContextWindow);
    }

    [Fact]
    public void ParseModelsDevJson_SkipsModelsWithoutLimits()
    {
        const string json = """
            {
                "test-provider": {
                    "id": "test",
                    "models": {
                        "has-limits": { "limit": { "context": 100000, "output": 8192 } },
                        "no-limits": { "name": "No Limits Model" },
                        "zero-context": { "limit": { "context": 0, "output": 8192 } }
                    }
                }
            }
            """;

        var result = ModelCatalog.ParseModelsDevJson(json);

        Assert.Single(result["test-provider"]);
        Assert.True(result["test-provider"].ContainsKey("has-limits"));
    }

    [Fact]
    public void ParseModelsDevJson_EmptyJson_ReturnsEmpty()
    {
        var result = ModelCatalog.ParseModelsDevJson("{}");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseModelsDevJson_InvalidJson_ReturnsEmpty()
    {
        var result = ModelCatalog.ParseModelsDevJson("not json");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseModelsDevJson_CaseInsensitiveLookup()
    {
        var result = ModelCatalog.ParseModelsDevJson(SampleJson);

        // Provider keys should be case-insensitive
        Assert.True(result.ContainsKey("ANTHROPIC"));
        Assert.True(result.ContainsKey("OpenAI"));

        // Model keys should be case-insensitive
        Assert.True(result["openai"].ContainsKey("GPT-4O"));
        Assert.True(result["anthropic"].ContainsKey("CLAUDE-SONNET-4-20250514"));
    }

    // ── GetLimits ───────────────────────────────────────────────

    [Fact]
    public void GetLimits_ReturnsLimits_ForKnownProviderAndModel()
    {
        var catalog = CreateCatalogWithSampleData();

        var limits = catalog.GetLimits("anthropic", "claude-sonnet-4-20250514");

        Assert.NotNull(limits);
        Assert.Equal(200_000, limits.ContextWindow);
        Assert.Equal(64_000, limits.MaxOutputTokens);
    }

    [Fact]
    public void GetLimits_MapsProviderName_GitHubCopilotApi()
    {
        var catalog = CreateCatalogWithSampleData();

        // Our provider name "github-copilot-api" maps to models.dev key "github-copilot"
        var limits = catalog.GetLimits("github-copilot-api", "claude-sonnet-4");

        Assert.NotNull(limits);
        Assert.Equal(128_000, limits.ContextWindow);
        Assert.Equal(16_000, limits.MaxOutputTokens);
    }

    [Fact]
    public void GetLimits_ReturnsNull_ForUnknownModel()
    {
        var catalog = CreateCatalogWithSampleData();

        var limits = catalog.GetLimits("anthropic", "nonexistent-model");

        Assert.Null(limits);
    }

    [Fact]
    public void GetLimits_ReturnsNull_ForUnknownProvider()
    {
        var catalog = CreateCatalogWithSampleData();

        var limits = catalog.GetLimits("unknown-provider", "gpt-4o");

        Assert.Null(limits);
    }

    // ── EnrichModelDefinitions ──────────────────────────────────

    [Fact]
    public void EnrichModelDefinitions_AddsLimitsFromCatalog()
    {
        var catalog = CreateCatalogWithSampleData();
        var config = new LlmProviderConfig
        {
            Name = "anthropic",
            Api = "anthropic-messages",
            Models = ["claude-sonnet-4-20250514", "claude-3-5-haiku-20241022"],
        };

        catalog.EnrichModelDefinitions(config);

        Assert.Equal(2, config.ModelDefinitions.Count);

        var sonnet = config.ModelDefinitions.First(d => d.Id == "claude-sonnet-4-20250514");
        Assert.Equal(200_000, sonnet.ContextWindow);
        Assert.Equal(64_000, sonnet.MaxOutputTokens);

        var haiku = config.ModelDefinitions.First(d => d.Id == "claude-3-5-haiku-20241022");
        Assert.Equal(200_000, haiku.ContextWindow);
        Assert.Equal(8_192, haiku.MaxOutputTokens);
    }

    [Fact]
    public void EnrichModelDefinitions_DoesNotOverrideExistingDefinitions()
    {
        var catalog = CreateCatalogWithSampleData();
        var config = new LlmProviderConfig
        {
            Name = "openai",
            Api = "openai-completions",
            Models = ["gpt-4o"],
            ModelDefinitions =
            [
                new LlmModelDefinition { Id = "gpt-4o", ContextWindow = 999_999, MaxOutputTokens = 1234 },
            ],
        };

        catalog.EnrichModelDefinitions(config);

        // Should not have added a duplicate — still just 1 definition
        Assert.Single(config.ModelDefinitions);
        // Original values preserved
        Assert.Equal(999_999, config.ModelDefinitions[0].ContextWindow);
        Assert.Equal(1234, config.ModelDefinitions[0].MaxOutputTokens);
    }

    [Fact]
    public void EnrichModelDefinitions_SkipsModelsNotInCatalog()
    {
        var catalog = CreateCatalogWithSampleData();
        var config = new LlmProviderConfig
        {
            Name = "anthropic",
            Api = "anthropic-messages",
            Models = ["claude-sonnet-4-20250514", "unknown-model-xyz"],
        };

        catalog.EnrichModelDefinitions(config);

        // Only the known model should be enriched
        Assert.Single(config.ModelDefinitions);
        Assert.Equal("claude-sonnet-4-20250514", config.ModelDefinitions[0].Id);
    }

    [Fact]
    public void EnrichModelDefinitions_MapsCopilotProviderName()
    {
        var catalog = CreateCatalogWithSampleData();
        var config = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            Models = ["claude-sonnet-4", "gpt-4o"],
        };

        catalog.EnrichModelDefinitions(config);

        Assert.Equal(2, config.ModelDefinitions.Count);

        var sonnet = config.ModelDefinitions.First(d => d.Id == "claude-sonnet-4");
        Assert.Equal(128_000, sonnet.ContextWindow); // Copilot limits, not native Anthropic
        Assert.Equal(16_000, sonnet.MaxOutputTokens);
    }

    [Fact]
    public void EnrichModelDefinitions_EmptyModels_NoOp()
    {
        var catalog = CreateCatalogWithSampleData();
        var config = new LlmProviderConfig
        {
            Name = "anthropic",
            Api = "anthropic-messages",
            Models = [],
        };

        catalog.EnrichModelDefinitions(config);

        Assert.Empty(config.ModelDefinitions);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static ModelCatalog CreateCatalogWithSampleData()
    {
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var catalog = new ModelCatalog(httpFactory, NullLogger<ModelCatalog>.Instance);

        // Use reflection to set the internal catalog field with parsed sample data
        var parsed = ModelCatalog.ParseModelsDevJson(SampleJson);
        var field = typeof(ModelCatalog).GetField("catalog",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(catalog, parsed);

        return catalog;
    }
}
