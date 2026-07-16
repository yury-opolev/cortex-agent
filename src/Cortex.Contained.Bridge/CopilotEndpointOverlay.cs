using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge;

/// <summary>
/// Overlays live GitHub Copilot <c>/models</c> endpoint metadata (<see cref="AvailableModel.SupportedEndpoints"/>)
/// onto a provider's persisted <see cref="LlmModelDefinition.SupportedEndpoints"/>, keyed by model ID.
/// <para>
/// UI save payloads (both the first-run setup wizard and the settings page) carry only selected model IDs.
/// <see cref="ModelCatalog.EnrichModelDefinitions"/> fills in context-window/max-output-token limits from
/// models.dev, but models.dev never reports per-model endpoint support. Without this overlay, every save
/// would persist <c>SupportedEndpoints = []</c> forever, forcing Copilot models such as <c>gpt-5.6-sol</c>
/// onto Chat Completions even though the live Copilot API reports <c>/responses</c> support.
/// </para>
/// Called from both <c>POST /api/setup/save</c> (<see cref="Endpoints.SetupEndpoints"/>) and
/// <c>POST /api/settings</c> (<see cref="Endpoints.SettingsEndpoints"/>) so every save path stays current.
/// </summary>
public static class CopilotEndpointOverlay
{
    private const string CopilotApi = "github-copilot-api";

    /// <summary>
    /// Overlays <paramref name="liveModels"/>' <see cref="AvailableModel.SupportedEndpoints"/> onto matching
    /// <paramref name="providerConfig"/> model definitions, matched by model ID (case-insensitive). A
    /// definition with no live match, or whose live entry reports no endpoints, is left untouched — this
    /// preserves the existing Chat Completions fallback for models with unavailable live metadata.
    /// <para>
    /// Also backfills a definition for any <paramref name="providerConfig"/>-selected model that has a live
    /// match but no definition at all — i.e. a model too new for <see cref="ModelCatalog.EnrichModelDefinitions"/>'s
    /// models.dev catalog to have limits for yet. Without this, live endpoint metadata (and the live
    /// context/output limits) for such a model would be silently discarded, defeating the point of this
    /// overlay for exactly the newest-model scenario it exists to fix. Models already enriched from
    /// models.dev keep their models.dev-derived <see cref="LlmModelDefinition.ContextWindow"/>/
    /// <see cref="LlmModelDefinition.MaxOutputTokens"/> untouched — only backfilled definitions use live
    /// limits, and only when the live value is known (non-zero); otherwise the class defaults apply.
    /// </para>
    /// </summary>
    public static void ApplySupportedEndpoints(LlmProviderConfig providerConfig, IReadOnlyList<AvailableModel> liveModels)
    {
        ArgumentNullException.ThrowIfNull(providerConfig);
        ArgumentNullException.ThrowIfNull(liveModels);

        if (liveModels.Count == 0)
        {
            return;
        }

        var liveById = new Dictionary<string, AvailableModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in liveModels)
        {
            if (!string.IsNullOrEmpty(model.Id))
            {
                liveById[model.Id] = model;
            }
        }

        foreach (var def in providerConfig.ModelDefinitions)
        {
            if (liveById.TryGetValue(def.Id, out var live) && live.SupportedEndpoints.Count > 0)
            {
                def.SupportedEndpoints = live.SupportedEndpoints;
            }
        }

        var definedIds = new HashSet<string>(
            providerConfig.ModelDefinitions.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var modelId in providerConfig.Models)
        {
            if (definedIds.Contains(modelId) || !liveById.TryGetValue(modelId, out var live))
            {
                continue;
            }

            var backfilled = new LlmModelDefinition { Id = modelId, SupportedEndpoints = live.SupportedEndpoints };
            if (live.ContextWindow > 0)
            {
                backfilled.ContextWindow = live.ContextWindow;
            }

            if (live.MaxOutputTokens > 0)
            {
                backfilled.MaxOutputTokens = live.MaxOutputTokens;
            }

            providerConfig.ModelDefinitions.Add(backfilled);
            definedIds.Add(modelId);
        }
    }

    /// <summary>
    /// Best-effort re-fetches live Copilot model metadata using <paramref name="providerConfig"/>'s stored
    /// credentials and overlays <see cref="AvailableModel.SupportedEndpoints"/> onto its model definitions.
    /// No-op for non-Copilot providers or providers without a stored API key/token — this preserves current
    /// behavior for those cases. Network/parse failures are logged and swallowed; the model definitions are
    /// left exactly as <see cref="ModelCatalog.EnrichModelDefinitions"/> produced them (empty
    /// <c>SupportedEndpoints</c>, i.e. the existing Chat Completions fallback), matching the "missing
    /// metadata" error-handling rule — a live-refresh failure must never fail the whole save.
    /// </summary>
    public static async Task RefreshSupportedEndpointsAsync(
        LlmProviderConfig providerConfig,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providerConfig);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        if (!string.Equals(providerConfig.Api, CopilotApi, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(providerConfig.ApiKey))
        {
            return;
        }

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            var isOAuth = string.Equals(providerConfig.TokenType, "oauth", StringComparison.OrdinalIgnoreCase);
            var liveModels = await SetupHelpers.FetchAvailableModelsAsync(
                providerConfig.Api,
                providerConfig.ApiKey,
                httpClient,
                isOAuth ? "oauth" : null,
                providerConfig.GithubBaseUrl,
                cancellationToken).ConfigureAwait(false);

            ApplySupportedEndpoints(providerConfig, liveModels);
        }
#pragma warning disable CA1031 // A save must never fail because this best-effort live refresh hit an
                               // unexpected exception (e.g. UriFormatException from a malformed stored
                               // GithubBaseUrl) — swallow anything and fall back to whatever
                               // ModelCatalog.EnrichModelDefinitions already produced.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            BridgeLogMessages.LogEndpointOverlayRefreshFailed(logger, providerConfig.Name, ex.Message);
        }
    }
}
