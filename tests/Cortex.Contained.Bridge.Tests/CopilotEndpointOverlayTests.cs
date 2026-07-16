using System.Net;
using System.Text;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests;

/// <summary>
/// Pins <see cref="CopilotEndpointOverlay"/> — the shared production helper that overlays live Copilot
/// <c>/models</c> endpoint metadata onto persisted <see cref="LlmModelDefinition.SupportedEndpoints"/>.
/// Without this overlay, <c>POST /api/setup/save</c> and <c>POST /api/settings</c> only ever persist
/// <c>SupportedEndpoints = []</c> because UI save payloads carry model IDs only, and
/// <see cref="ModelCatalog.EnrichModelDefinitions"/> supplies context/output limits from models.dev, which
/// never reports endpoint support.
/// </summary>
public class CopilotEndpointOverlayTests
{
    // ── ApplySupportedEndpoints (pure overlay) ──────────────────────────────

    [Fact]
    public void ApplySupportedEndpoints_MatchingLiveModel_OverlaysEndpoints_CaseInsensitiveId()
    {
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            ModelDefinitions =
            [
                new LlmModelDefinition { Id = "gpt-5.6-sol", ContextWindow = 1_050_000, MaxOutputTokens = 128_000 },
            ],
        };
        var liveModels = new List<AvailableModel>
        {
            new() { Id = "GPT-5.6-SOL", SupportedEndpoints = ["/responses", "ws:/responses"] },
        };

        CopilotEndpointOverlay.ApplySupportedEndpoints(provider, liveModels);

        Assert.Equal(["/responses", "ws:/responses"], provider.ModelDefinitions[0].SupportedEndpoints);
    }

    [Fact]
    public void ApplySupportedEndpoints_NoMatchingLiveModel_LeavesDefinitionEmpty()
    {
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            ModelDefinitions =
            [
                new LlmModelDefinition { Id = "gpt-5.5", ContextWindow = 200_000, MaxOutputTokens = 32_000 },
            ],
        };
        var liveModels = new List<AvailableModel>
        {
            new() { Id = "gpt-5.6-sol", SupportedEndpoints = ["/responses"] },
        };

        CopilotEndpointOverlay.ApplySupportedEndpoints(provider, liveModels);

        Assert.Empty(provider.ModelDefinitions[0].SupportedEndpoints);
    }

    [Fact]
    public void ApplySupportedEndpoints_LiveModelReportsNoEndpoints_LeavesExistingValueUntouched()
    {
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            ModelDefinitions =
            [
                new LlmModelDefinition
                {
                    Id = "gpt-5.6-sol",
                    ContextWindow = 1_050_000,
                    MaxOutputTokens = 128_000,
                    SupportedEndpoints = ["/responses"],
                },
            ],
        };
        // Live fetch succeeded but reported no endpoints for this model (e.g. transient upstream gap).
        var liveModels = new List<AvailableModel> { new() { Id = "gpt-5.6-sol", SupportedEndpoints = [] } };

        CopilotEndpointOverlay.ApplySupportedEndpoints(provider, liveModels);

        Assert.Equal(["/responses"], provider.ModelDefinitions[0].SupportedEndpoints);
    }

    [Fact]
    public void ApplySupportedEndpoints_EmptyLiveModelList_NoOp()
    {
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            ModelDefinitions =
            [
                new LlmModelDefinition { Id = "gpt-5.5", ContextWindow = 200_000, MaxOutputTokens = 32_000 },
            ],
        };

        CopilotEndpointOverlay.ApplySupportedEndpoints(provider, []);

        Assert.Empty(provider.ModelDefinitions[0].SupportedEndpoints);
    }

    [Fact]
    public void ApplySupportedEndpoints_SelectedModelMissingFromModelsDev_BackfillsDefinitionFromLiveData()
    {
        // gpt-5.6-sol is selected but models.dev hasn't cataloged it yet, so
        // ModelCatalog.EnrichModelDefinitions never created a definition for it. The overlay must still
        // persist a definition — carrying the live context/output limits and SupportedEndpoints — otherwise
        // the live endpoint metadata (the entire point of this feature) is silently discarded.
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            Models = ["gpt-5.6-sol"],
        };
        var liveModels = new List<AvailableModel>
        {
            new()
            {
                Id = "gpt-5.6-sol",
                ContextWindow = 1_050_000,
                MaxOutputTokens = 128_000,
                SupportedEndpoints = ["/responses", "ws:/responses"],
            },
        };

        CopilotEndpointOverlay.ApplySupportedEndpoints(provider, liveModels);

        var def = Assert.Single(provider.ModelDefinitions);
        Assert.Equal("gpt-5.6-sol", def.Id);
        Assert.Equal(1_050_000, def.ContextWindow);
        Assert.Equal(128_000, def.MaxOutputTokens);
        Assert.Equal(["/responses", "ws:/responses"], def.SupportedEndpoints);
    }

    [Fact]
    public void ApplySupportedEndpoints_SelectedModelNotInLiveModels_NoDefinitionCreated()
    {
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            Models = ["some-unavailable-model"],
        };

        CopilotEndpointOverlay.ApplySupportedEndpoints(provider, [new AvailableModel { Id = "other-model" }]);

        Assert.Empty(provider.ModelDefinitions);
    }

    [Fact]
    public void ApplySupportedEndpoints_BackfilledDefinition_UnknownLiveLimits_UsesClassDefaults()
    {
        // Live limits of 0 mean "unknown" (AvailableModel.ContextWindow/MaxOutputTokens docs) — persisting 0
        // would violate LlmModelDefinition's [Range(1, ...)] validation, so the ctor defaults must be kept.
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            Models = ["gpt-5.6-sol"],
        };
        var liveModels = new List<AvailableModel>
        {
            new() { Id = "gpt-5.6-sol", ContextWindow = 0, MaxOutputTokens = 0, SupportedEndpoints = ["/responses"] },
        };

        CopilotEndpointOverlay.ApplySupportedEndpoints(provider, liveModels);

        var def = Assert.Single(provider.ModelDefinitions);
        Assert.Equal(128_000, def.ContextWindow);
        Assert.Equal(8_192, def.MaxOutputTokens);
        Assert.Equal(["/responses"], def.SupportedEndpoints);
    }

    [Fact]
    public void ApplySupportedEndpoints_ModelAlreadyDefined_NeverBackfillsDuplicate()
    {
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            Models = ["gpt-5.6-sol"],
            ModelDefinitions = [new LlmModelDefinition { Id = "gpt-5.6-sol", ContextWindow = 999_999, MaxOutputTokens = 1234 }],
        };
        var liveModels = new List<AvailableModel>
        {
            new() { Id = "gpt-5.6-sol", ContextWindow = 1_050_000, MaxOutputTokens = 128_000, SupportedEndpoints = ["/responses"] },
        };

        CopilotEndpointOverlay.ApplySupportedEndpoints(provider, liveModels);

        var def = Assert.Single(provider.ModelDefinitions);
        // Existing models.dev-derived limits are untouched; only SupportedEndpoints is overlaid.
        Assert.Equal(999_999, def.ContextWindow);
        Assert.Equal(1234, def.MaxOutputTokens);
        Assert.Equal(["/responses"], def.SupportedEndpoints);
    }

    // ── RefreshSupportedEndpointsAsync (re-fetch + overlay) ─────────────────

    [Fact]
    public async Task RefreshSupportedEndpointsAsync_NonCopilotProvider_NeverCallsHttp()
    {
        var provider = new LlmProviderConfig
        {
            Name = "openai",
            Api = "openai-completions",
            ApiKey = "sk-fake",
            ModelDefinitions = [new LlmModelDefinition { Id = "gpt-5.5" }],
        };
        var httpFactory = new ThrowingHttpClientFactory();

        await CopilotEndpointOverlay.RefreshSupportedEndpointsAsync(
            provider, httpFactory, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(provider.ModelDefinitions[0].SupportedEndpoints);
    }

    [Fact]
    public async Task RefreshSupportedEndpointsAsync_NoApiKey_NeverCallsHttp()
    {
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            ApiKey = null,
            ModelDefinitions = [new LlmModelDefinition { Id = "gpt-5.6-sol" }],
        };
        var httpFactory = new ThrowingHttpClientFactory();

        await CopilotEndpointOverlay.RefreshSupportedEndpointsAsync(
            provider, httpFactory, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(provider.ModelDefinitions[0].SupportedEndpoints);
    }

    [Fact]
    public async Task RefreshSupportedEndpointsAsync_CopilotProviderWithApiKey_FetchesLiveModelsAndOverlays()
    {
        const string body = """
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
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            ApiKey = "gho_faketoken",
            TokenType = "oauth",
            ModelDefinitions =
            [
                new LlmModelDefinition { Id = "gpt-5.6-sol", ContextWindow = 1_050_000, MaxOutputTokens = 128_000 },
            ],
        };
        var httpFactory = new StubHttpClientFactory(Json(body));

        await CopilotEndpointOverlay.RefreshSupportedEndpointsAsync(
            provider, httpFactory, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(["/responses", "ws:/responses"], provider.ModelDefinitions[0].SupportedEndpoints);
    }

    [Fact]
    public async Task RefreshSupportedEndpointsAsync_HttpFailure_SwallowsAndLeavesDefinitionsUnchanged()
    {
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            ApiKey = "gho_faketoken",
            TokenType = "oauth",
            ModelDefinitions = [new LlmModelDefinition { Id = "gpt-5.6-sol" }],
        };
        var httpFactory = new StubHttpClientFactory(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        // Must not throw — a live-refresh failure must never fail the whole save.
        await CopilotEndpointOverlay.RefreshSupportedEndpointsAsync(
            provider, httpFactory, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(provider.ModelDefinitions[0].SupportedEndpoints);
    }

    [Fact]
    public async Task RefreshSupportedEndpointsAsync_MalformedGithubBaseUrl_SwallowsUriFormatException()
    {
        // A malformed GithubBaseUrl (e.g. a stray space in a hand-edited GHE host) makes
        // SetupHelpers.FetchCopilotApiModelsAsync's HttpRequestMessage constructor throw
        // UriFormatException — an exception type outside HttpRequestException/JsonException/
        // TaskCanceledException. This must still never fail the whole save. Verified real:
        // `new HttpRequestMessage(HttpMethod.Get, "https://copilot-api.bad host/with spaces/models")`
        // throws UriFormatException before any request is ever sent.
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            ApiKey = "gho_faketoken",
            TokenType = "oauth",
            GithubBaseUrl = "bad host/with spaces",
            ModelDefinitions = [new LlmModelDefinition { Id = "gpt-5.6-sol" }],
        };
        // Request construction fails before send, so the stubbed response is never used.
        var httpFactory = new StubHttpClientFactory(Json("{}"));

        await CopilotEndpointOverlay.RefreshSupportedEndpointsAsync(
            provider, httpFactory, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(provider.ModelDefinitions[0].SupportedEndpoints);
    }

    [Fact]
    public async Task RefreshSupportedEndpointsAsync_UnexpectedExceptionFromHttpClientFactory_SwallowsAndDoesNotThrow()
    {
        var provider = new LlmProviderConfig
        {
            Name = "github-copilot-api",
            Api = "github-copilot-api",
            ApiKey = "gho_faketoken",
            TokenType = "oauth",
            ModelDefinitions = [new LlmModelDefinition { Id = "gpt-5.6-sol" }],
        };
        var httpFactory = new ThrowingHttpClientFactory();

        // Must not throw — any unexpected exception type from the live re-fetch must never fail the save.
        await CopilotEndpointOverlay.RefreshSupportedEndpointsAsync(
            provider, httpFactory, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(provider.ModelDefinitions[0].SupportedEndpoints);
    }

    // ── Test doubles ─────────────────────────────────────────────────────

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
