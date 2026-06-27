using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests;

/// <summary>
/// Tests for the provider model refresh logic used by POST /api/settings
/// when providerModels is provided — validates model list updates,
/// default/memory model reset, and model definition pruning/enrichment.
/// </summary>
public class ProviderModelRefreshTests
{
    // ── Model list update ───────────────────────────────────────

    [Fact]
    public void UpdateModels_ReplacesExistingModelList()
    {
        var provider = CreateProvider("anthropic", ["claude-3-opus", "claude-3-sonnet"]);

        var newModels = new List<string> { "claude-sonnet-4-20250514", "claude-haiku-4-20250414" };
        ApplyModelListUpdate(provider, newModels);

        Assert.Equal(newModels, provider.Models);
    }

    [Fact]
    public void UpdateModels_ResetsDefaultModel_WhenRemovedFromList()
    {
        var provider = CreateProvider("anthropic", ["model-a", "model-b"]);
        provider.DefaultModel = "model-a";

        ApplyModelListUpdate(provider, ["model-b", "model-c"]);

        // model-a was removed; default should reset to first in new list
        Assert.Equal("model-b", provider.DefaultModel);
    }

    [Fact]
    public void UpdateModels_KeepsDefaultModel_WhenStillInList()
    {
        var provider = CreateProvider("anthropic", ["model-a", "model-b"]);
        provider.DefaultModel = "model-b";

        ApplyModelListUpdate(provider, ["model-a", "model-b", "model-c"]);

        Assert.Equal("model-b", provider.DefaultModel);
    }

    [Fact]
    public void UpdateModels_ClearsMemoryModel_WhenRemovedFromList()
    {
        var provider = CreateProvider("anthropic", ["model-a", "model-b"]);
        provider.MemoryModel = "model-a";

        ApplyModelListUpdate(provider, ["model-b", "model-c"]);

        // model-a was removed; memory model should be cleared (falls back to default)
        Assert.Null(provider.MemoryModel);
    }

    [Fact]
    public void UpdateModels_KeepsMemoryModel_WhenStillInList()
    {
        var provider = CreateProvider("anthropic", ["model-a", "model-b"]);
        provider.MemoryModel = "model-b";

        ApplyModelListUpdate(provider, ["model-a", "model-b", "model-c"]);

        Assert.Equal("model-b", provider.MemoryModel);
    }

    [Fact]
    public void UpdateModels_NullDefaultModel_StaysNull()
    {
        var provider = CreateProvider("anthropic", ["model-a"]);
        provider.DefaultModel = null;

        ApplyModelListUpdate(provider, ["model-b"]);

        // DefaultModel was null (means "use first"), should stay null
        Assert.Null(provider.DefaultModel);
    }

    [Fact]
    public void UpdateModels_NullMemoryModel_StaysNull()
    {
        var provider = CreateProvider("anthropic", ["model-a"]);
        provider.MemoryModel = null;

        ApplyModelListUpdate(provider, ["model-b"]);

        Assert.Null(provider.MemoryModel);
    }

    // ── Model definition pruning ────────────────────────────────

    [Fact]
    public void UpdateModels_RemovesDefinitions_ForRemovedModels()
    {
        var provider = CreateProvider("anthropic", ["model-a", "model-b"]);
        provider.ModelDefinitions =
        [
            new LlmModelDefinition { Id = "model-a", ContextWindow = 100_000, MaxOutputTokens = 4096 },
            new LlmModelDefinition { Id = "model-b", ContextWindow = 200_000, MaxOutputTokens = 8192 },
        ];

        ApplyModelListUpdate(provider, ["model-b", "model-c"]);

        // model-a definition should be removed; model-b should remain
        Assert.Single(provider.ModelDefinitions);
        Assert.Equal("model-b", provider.ModelDefinitions[0].Id);
    }

    [Fact]
    public void UpdateModels_KeepsAllDefinitions_WhenNoModelsRemoved()
    {
        var provider = CreateProvider("anthropic", ["model-a", "model-b"]);
        provider.ModelDefinitions =
        [
            new LlmModelDefinition { Id = "model-a", ContextWindow = 100_000, MaxOutputTokens = 4096 },
            new LlmModelDefinition { Id = "model-b", ContextWindow = 200_000, MaxOutputTokens = 8192 },
        ];

        ApplyModelListUpdate(provider, ["model-a", "model-b", "model-c"]);

        // Both existing definitions should remain
        Assert.Equal(2, provider.ModelDefinitions.Count);
    }

    // ── Model catalog enrichment ────────────────────────────────

    [Fact]
    public void UpdateModels_EnrichesNewModels_FromCatalog()
    {
        var provider = CreateProvider("anthropic", ["claude-3-opus"]);
        var catalog = CreateCatalogWithAnthropicData();

        // Add a new model that exists in the catalog
        ApplyModelListUpdate(provider, ["claude-sonnet-4-20250514"], catalog);

        // New model should be enriched with catalog data
        Assert.Single(provider.ModelDefinitions);
        var def = provider.ModelDefinitions[0];
        Assert.Equal("claude-sonnet-4-20250514", def.Id);
        Assert.Equal(200_000, def.ContextWindow);
        Assert.Equal(64_000, def.MaxOutputTokens);
    }

    [Fact]
    public void UpdateModels_DoesNotDuplicateExistingDefinitions_OnEnrich()
    {
        var provider = CreateProvider("anthropic", ["claude-sonnet-4-20250514"]);
        provider.ModelDefinitions =
        [
            new LlmModelDefinition { Id = "claude-sonnet-4-20250514", ContextWindow = 999_999, MaxOutputTokens = 1234 },
        ];
        var catalog = CreateCatalogWithAnthropicData();

        // Model still in list, definition already exists — should not duplicate
        ApplyModelListUpdate(provider, ["claude-sonnet-4-20250514"], catalog);

        Assert.Single(provider.ModelDefinitions);
        // Original custom values preserved (EnrichModelDefinitions skips existing)
        Assert.Equal(999_999, provider.ModelDefinitions[0].ContextWindow);
    }

    // ── SettingsUpdateRequest DTO ────────────────────────────────

    [Fact]
    public void SettingsUpdateRequest_ProviderModels_IsNullByDefault()
    {
        var request = new SettingsUpdateRequest();
        Assert.Null(request.ProviderModels);
    }

    [Fact]
    public void SettingsUpdateRequest_ProviderModels_RoundTrips()
    {
        var request = new SettingsUpdateRequest
        {
            ProviderModels = new Dictionary<string, List<string>>
            {
                ["anthropic"] = ["claude-sonnet-4-20250514", "claude-haiku-4-20250414"],
            },
        };

        Assert.NotNull(request.ProviderModels);
        Assert.Equal(2, request.ProviderModels["anthropic"].Count);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static LlmProviderConfig CreateProvider(string name, List<string> models)
    {
        return new LlmProviderConfig
        {
            Name = name,
            Api = name == "anthropic" ? "anthropic-messages" : "openai-completions",
            Models = models,
        };
    }

    /// <summary>
    /// Simulates the model list update logic from POST /api/settings providerModels block.
    /// This is the same logic as in Program.cs.
    /// </summary>
    private static void ApplyModelListUpdate(
        LlmProviderConfig provider,
        List<string> newModels,
        ModelCatalog? catalog = null)
    {
        provider.Models = newModels;

        // Reset default/memory model if the currently selected model was removed
        if (provider.DefaultModel is not null && !newModels.Contains(provider.DefaultModel))
        {
            provider.DefaultModel = newModels.Count > 0 ? newModels[0] : null;
        }

        if (provider.MemoryModel is not null && !newModels.Contains(provider.MemoryModel))
        {
            provider.MemoryModel = null;
        }

        // Remove model definitions for models no longer in the list
        provider.ModelDefinitions.RemoveAll(d => !newModels.Contains(d.Id));

        // Enrich new models with metadata from the model catalog
        catalog?.EnrichModelDefinitions(provider);
    }

    private static ModelCatalog CreateCatalogWithAnthropicData()
    {
        const string json = """
            {
                "anthropic": {
                    "id": "anthropic",
                    "models": {
                        "claude-sonnet-4-20250514": {
                            "id": "claude-sonnet-4-20250514",
                            "name": "Claude Sonnet 4",
                            "limit": { "context": 200000, "output": 64000 }
                        },
                        "claude-haiku-4-20250414": {
                            "id": "claude-haiku-4-20250414",
                            "name": "Claude Haiku 4",
                            "limit": { "context": 200000, "output": 8192 }
                        }
                    }
                }
            }
            """;

        var httpFactory = Substitute.For<IHttpClientFactory>();
        var catalog = new ModelCatalog(httpFactory, NullLogger<ModelCatalog>.Instance);

        var parsed = ModelCatalog.ParseModelsDevJson(json);
        var field = typeof(ModelCatalog).GetField("catalog",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(catalog, parsed);

        return catalog;
    }
}
