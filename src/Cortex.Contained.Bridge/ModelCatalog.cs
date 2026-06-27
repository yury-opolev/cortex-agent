using System.Text.Json;
using System.Text.Json.Nodes;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge;

/// <summary>
/// Per-model token limits (context window + max output tokens).
/// </summary>
public sealed record ModelLimits(int ContextWindow, int MaxOutputTokens);

/// <summary>
/// Fetches and caches model metadata from <c>models.dev/api.json</c>.
/// Provides per-provider, per-model context window and max output token limits.
/// <para>
/// Data is fetched once at startup, cached to disk under <c>%LOCALAPPDATA%\Cortex</c>,
/// and refreshed every 24 hours. If the network is unavailable, the disk cache is used.
/// If both fail, all lookups return <c>null</c> (callers should fall back to defaults).
/// </para>
/// </summary>
public sealed partial class ModelCatalog : IDisposable
{
    private const string ModelsDevUrl = "https://models.dev/api.json";
    private const string CacheFileName = "models-dev-cache.json";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly string cacheFilePath;
    private readonly ILogger<ModelCatalog> logger;
    private readonly CancellationTokenSource cts = new();
    private Timer? refreshTimer;

    /// <summary>
    /// Maps provider key (models.dev) → model ID → limits.
    /// Populated from the parsed models.dev JSON.
    /// </summary>
    private volatile Dictionary<string, Dictionary<string, ModelLimits>> catalog = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps our provider names (e.g. "github-copilot-api") to models.dev provider keys (e.g. "github-copilot").
    /// </summary>
    private static readonly Dictionary<string, string> ProviderKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github-copilot-api"] = "github-copilot",
        ["openai"] = "openai",
        ["anthropic"] = "anthropic",
    };

    public ModelCatalog(IHttpClientFactory httpClientFactory, ILogger<ModelCatalog> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cortex");
        Directory.CreateDirectory(dataDir);
        this.cacheFilePath = Path.Combine(dataDir, CacheFileName);
    }

    /// <summary>
    /// Initialise the catalog: load from disk cache, then fetch from network.
    /// Call once at startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 1. Try disk cache first for fast startup
        if (TryLoadFromDisk())
        {
            var count = CountModels(this.catalog);
            this.LogLoadedFromDisk(count);
        }

        // 2. Fetch fresh data from network (replaces disk cache data if successful)
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

        // 3. Schedule periodic refresh
        this.refreshTimer = new Timer(
            _ => _ = RefreshInBackgroundAsync(),
            null,
            RefreshInterval,
            RefreshInterval);
    }

    /// <summary>
    /// Look up token limits for a model under a specific provider.
    /// </summary>
    /// <param name="providerName">Our provider name (e.g. "anthropic", "github-copilot-api").</param>
    /// <param name="modelId">Model ID (e.g. "claude-sonnet-4-6", "gpt-4o").</param>
    /// <returns>Limits if found; <c>null</c> otherwise.</returns>
    public ModelLimits? GetLimits(string providerName, string modelId)
    {
        var key = ProviderKeyMap.GetValueOrDefault(providerName, providerName);

        if (this.catalog.TryGetValue(key, out var models) &&
            models.TryGetValue(modelId, out var limits))
        {
            return limits;
        }

        return null;
    }

    /// <summary>
    /// Enrich a provider's model definitions with limits from the catalog.
    /// For each model in the provider, if it has no explicit <see cref="LlmModelDefinition"/>
    /// and the catalog has limits for it, a definition is added.
    /// </summary>
    /// <param name="providerConfig">The provider config to enrich (modified in-place).</param>
    public void EnrichModelDefinitions(LlmProviderConfig providerConfig)
    {
        // Build a set of model IDs that already have explicit definitions
        var existing = new HashSet<string>(
            providerConfig.ModelDefinitions.Select(d => d.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach (var modelId in providerConfig.Models)
        {
            if (existing.Contains(modelId))
            {
                continue;
            }

            var limits = GetLimits(providerConfig.Name, modelId);
            if (limits is null)
            {
                continue;
            }

            providerConfig.ModelDefinitions.Add(new LlmModelDefinition
            {
                Id = modelId,
                ContextWindow = limits.ContextWindow,
                MaxOutputTokens = limits.MaxOutputTokens,
            });
        }
    }

    /// <summary>Whether the catalog has any data loaded.</summary>
    public bool IsLoaded => this.catalog.Count > 0;

    public void Dispose()
    {
        this.cts.Cancel();
        this.refreshTimer?.Dispose();
        this.cts.Dispose();
    }

    private static int CountModels(Dictionary<string, Dictionary<string, ModelLimits>> catalog)
        => catalog.Sum(p => p.Value.Count);

    // ── Internal ────────────────────────────────────────────────────

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = this.httpClientFactory.CreateClient("models-dev");
            var json = await httpClient.GetStringAsync(ModelsDevUrl, cancellationToken).ConfigureAwait(false);

            var parsed = ParseModelsDevJson(json);
            if (parsed.Count > 0)
            {
                this.catalog = parsed;
                var modelCount = CountModels(parsed);
                this.LogRefreshed(parsed.Count, modelCount);

                // Persist to disk for next startup
                await File.WriteAllTextAsync(this.cacheFilePath, json, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogRefreshFailed(ex.Message);
        }
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await RefreshAsync(this.cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
    }

    private bool TryLoadFromDisk()
    {
        try
        {
            if (!File.Exists(this.cacheFilePath))
            {
                return false;
            }

            // Skip stale cache (older than 7 days)
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(this.cacheFilePath);
            if (age > TimeSpan.FromDays(7))
            {
                this.LogCacheStale(age);
                return false;
            }

            var json = File.ReadAllText(this.cacheFilePath);
            var parsed = ParseModelsDevJson(json);
            if (parsed.Count > 0)
            {
                this.catalog = parsed;
                return true;
            }
        }
        catch (Exception ex)
        {
            this.LogCacheLoadFailed(ex.Message);
        }

        return false;
    }

    /// <summary>
    /// Parse the models.dev JSON into a provider → model → limits dictionary.
    /// Structure: <c>{ "provider-key": { "models": { "model-id": { "limit": { "context": N, "output": N } } } } }</c>
    /// </summary>
    internal static Dictionary<string, Dictionary<string, ModelLimits>> ParseModelsDevJson(string json)
    {
        var result = new Dictionary<string, Dictionary<string, ModelLimits>>(StringComparer.OrdinalIgnoreCase);

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject();
        }
        catch (JsonException)
        {
            return result;
        }

        if (root is null)
        {
            return result;
        }

        foreach (var (providerKey, providerNode) in root)
        {
            var modelsNode = providerNode?["models"]?.AsObject();
            if (modelsNode is null)
            {
                continue;
            }

            var models = new Dictionary<string, ModelLimits>(StringComparer.OrdinalIgnoreCase);

            foreach (var (modelId, modelNode) in modelsNode)
            {
                var limitNode = modelNode?["limit"];
                if (limitNode is null)
                {
                    continue;
                }

                var context = limitNode["context"]?.GetValue<int>() ?? 0;
                var output = limitNode["output"]?.GetValue<int>() ?? 0;

                if (context > 0 && output > 0)
                {
                    models[modelId] = new ModelLimits(context, output);
                }
            }

            if (models.Count > 0)
            {
                result[providerKey] = models;
            }
        }

        return result;
    }

    // ── Logging ─────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Model catalog loaded from disk cache ({ModelCount} models)")]
    private partial void LogLoadedFromDisk(int modelCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Model catalog refreshed from models.dev ({ProviderCount} providers, {ModelCount} models)")]
    private partial void LogRefreshed(int providerCount, int modelCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to refresh model catalog from models.dev: {Error}")]
    private partial void LogRefreshFailed(string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Model catalog disk cache is stale ({Age}), skipping")]
    private partial void LogCacheStale(TimeSpan age);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load model catalog from disk cache: {Error}")]
    private partial void LogCacheLoadFailed(string error);
}
