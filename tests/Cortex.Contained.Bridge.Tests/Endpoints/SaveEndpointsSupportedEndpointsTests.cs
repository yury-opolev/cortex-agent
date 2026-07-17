using System.Net;
using System.Text;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Endpoints;

/// <summary>
/// Proves the real production save seams — <see cref="SetupEndpoints.EnrichProviderConfigsAsync"/> (called
/// from <c>POST /api/setup/save</c>) and <see cref="SettingsEndpoints.EnrichProviderModelDefinitionsAsync"/>
/// (called from <c>POST /api/settings</c>) — populate <see cref="LlmModelDefinition.SupportedEndpoints"/> for
/// selected Copilot models via <see cref="CopilotEndpointOverlay"/>, not just context/output limits from
/// <see cref="ModelCatalog.EnrichModelDefinitions"/>. Before this fix, both call sites only ever persisted
/// <c>SupportedEndpoints = []</c> because UI save payloads carry model IDs only.
/// </summary>
public sealed class SaveEndpointsEndpointSupportedEndpointsTests
{
    private const string CopilotModelsBody = """
        {
          "data": [
            {
              "id": "gpt-5.6-sol",
              "name": "GPT-5.6 Sol",
              "vendor": "openai",
              "supported_endpoints": ["/responses", "ws:/responses"],
              "capabilities": {
                "type": "chat",
                "limits": { "max_context_window_tokens": 1050000, "max_output_tokens": 128000 }
              }
            }
          ]
        }
        """;

    // ── SetupEndpoints.EnrichProviderConfigsAsync (POST /api/setup/save seam) ──

    [Fact]
    public async Task EnrichProviderConfigsAsync_CopilotProvider_PopulatesSupportedEndpoints()
    {
        // models.dev supplies context/output limits (so EnrichModelDefinitions creates the definition);
        // the live Copilot /models fetch below supplies SupportedEndpoints on top of it.
        var modelCatalog = CreateCatalogWithCopilotLimits();
        var providerConfigs = new List<LlmProviderConfig>
        {
            new()
            {
                Name = "github-copilot-api",
                Api = "github-copilot-api",
                ApiKey = "gho_faketoken",
                TokenType = "oauth",
                Models = ["gpt-5.6-sol"],
                DefaultModel = "gpt-5.6-sol",
            },
        };
        var httpFactory = new StubHttpClientFactory(Json(CopilotModelsBody));

        await SetupEndpoints.EnrichProviderConfigsAsync(
            providerConfigs, modelCatalog, httpFactory, NullLogger.Instance, CancellationToken.None);

        var def = Assert.Single(providerConfigs[0].ModelDefinitions);
        Assert.Equal("gpt-5.6-sol", def.Id);
        Assert.Equal(1_050_000, def.ContextWindow);
        Assert.Equal(["/responses", "ws:/responses"], def.SupportedEndpoints);
    }

    [Fact]
    public async Task EnrichProviderConfigsAsync_NonCopilotProvider_LeavesSupportedEndpointsEmpty()
    {
        var modelCatalog = new ModelCatalog(new ThrowingHttpClientFactory(), NullLogger<ModelCatalog>.Instance);
        var providerConfigs = new List<LlmProviderConfig>
        {
            new()
            {
                Name = "openai",
                Api = "openai-completions",
                ApiKey = "sk-fake",
                ModelDefinitions = [new LlmModelDefinition { Id = "gpt-5.5", ContextWindow = 200_000, MaxOutputTokens = 32_000 }],
            },
        };
        var httpFactory = new ThrowingHttpClientFactory();

        await SetupEndpoints.EnrichProviderConfigsAsync(
            providerConfigs, modelCatalog, httpFactory, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(providerConfigs[0].ModelDefinitions[0].SupportedEndpoints);
    }

    // ── SettingsEndpoints.EnrichProviderModelDefinitionsAsync (POST /api/settings seam) ──

    [Fact]
    public async Task EnrichProviderModelDefinitionsAsync_CopilotProvider_PopulatesSupportedEndpoints()
    {
        var modelCatalog = new ModelCatalog(new ThrowingHttpClientFactory(), NullLogger<ModelCatalog>.Instance);
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            ApiKey = "gho_faketoken",
            TokenType = "oauth",
            Models = ["gpt-5.6-sol"],
            ModelDefinitions = [new LlmModelDefinition { Id = "gpt-5.6-sol", ContextWindow = 1_050_000, MaxOutputTokens = 128_000 }],
        };
        var httpFactory = new StubHttpClientFactory(Json(CopilotModelsBody));

        await SettingsEndpoints.EnrichProviderModelDefinitionsAsync(
            provider, modelCatalog, httpFactory, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(["/responses", "ws:/responses"], provider.ModelDefinitions[0].SupportedEndpoints);
    }

    [Fact]
    public async Task EnrichProviderModelDefinitionsAsync_NonCopilotProvider_LeavesSupportedEndpointsEmpty()
    {
        var modelCatalog = new ModelCatalog(new ThrowingHttpClientFactory(), NullLogger<ModelCatalog>.Instance);
        var provider = new LlmProviderConfig
        {
            Name = "anthropic",
            Api = "anthropic-messages",
            ApiKey = "sk-ant-fake",
            Models = ["claude-sonnet-4-6"],
            ModelDefinitions = [new LlmModelDefinition { Id = "claude-sonnet-4-6", ContextWindow = 200_000, MaxOutputTokens = 64_000 }],
        };
        var httpFactory = new ThrowingHttpClientFactory();

        await SettingsEndpoints.EnrichProviderModelDefinitionsAsync(
            provider, modelCatalog, httpFactory, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(provider.ModelDefinitions[0].SupportedEndpoints);
    }

    // ── Test doubles ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="ModelCatalog"/> pre-populated (via reflection, same pattern as
    /// <c>ProviderModelRefreshTests.CreateCatalogWithAnthropicData</c>) with models.dev-shaped
    /// github-copilot limits, so <see cref="ModelCatalog.EnrichModelDefinitions"/> creates the
    /// <see cref="LlmModelDefinition"/> that <see cref="CopilotEndpointOverlay"/> then overlays
    /// endpoint metadata onto.
    /// </summary>
    private static ModelCatalog CreateCatalogWithCopilotLimits()
    {
        const string json = """
            {
                "github-copilot": {
                    "id": "github-copilot",
                    "models": {
                        "gpt-5.6-sol": {
                            "id": "gpt-5.6-sol",
                            "name": "GPT-5.6 Sol",
                            "limit": { "context": 1050000, "output": 128000 }
                        }
                    }
                }
            }
            """;

        var catalog = new ModelCatalog(new ThrowingHttpClientFactory(), NullLogger<ModelCatalog>.Instance);
        var parsed = ModelCatalog.ParseModelsDevJson(json);
        var field = typeof(ModelCatalog).GetField("catalog",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(catalog, parsed);

        return catalog;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("HTTP must not be called for this provider.");
    }

    private sealed class StubHttpClientFactory(HttpResponseMessage response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(response), disposeHandler: false);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
