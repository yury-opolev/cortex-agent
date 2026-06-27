using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Cortex.Contained.ScenarioEvals.Abstractions;
using Cortex.Contained.ScenarioEvals.Actor;
using Cortex.Contained.ScenarioEvals.Client;
using Cortex.Contained.ScenarioEvals.Orchestration;
using Cortex.Contained.ScenarioEvals.Results;
using Cortex.Contained.ScenarioEvals.Scoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.ScenarioEvals;

/// <summary>
/// xUnit fixture: resolves configuration, creates clients, initializes the result store,
/// and creates a run record. Shared across all scenario eval tests via collection fixture.
/// </summary>
public sealed class ScenarioEvalFixture : IAsyncLifetime
{
    private SqliteResultStore? _resultStore;
    private BridgeApiClient? _bridgeClient;

    public IBridgeApiClient BridgeClient => _bridgeClient ?? throw new InvalidOperationException("Fixture not initialized");
    public IActorService ActorService { get; private set; } = null!;
    public IScorer Scorer { get; private set; } = null!;
    public IResultStore ResultStore => _resultStore ?? throw new InvalidOperationException("Fixture not initialized");
    public long RunId { get; private set; }
    public string TranscriptDir { get; private set; } = null!;
    public IConfiguration Configuration { get; private set; } = null!;
    public string EvalModel { get; private set; } = null!;

    public ILoggerFactory LoggerFactory { get; } = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
    {
        builder.AddConsole();
        builder.SetMinimumLevel(LogLevel.Information);
    });

    public async Task InitializeAsync()
    {
        // Resolve configuration: env vars → appsettings → local overrides
        Configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.scenarioevals.json", optional: true)
            .AddJsonFile("appsettings.scenarioevals.local.json", optional: true)
            .AddEnvironmentVariables("SCENARIO_EVAL_")
            .Build();

        var bridgeUrl = ResolveConfig("BridgeUrl", "BRIDGE_URL", "http://localhost:5080");
        var bridgePassword = ResolveConfig("BridgePassword", "BRIDGE_PASSWORD", "");
        var apiKey = ResolveConfig("ApiKey", "API_KEY", "");
        var tenantId = ResolveConfig("TenantId", "TENANT_ID", "default");
        var llmApiKey = ResolveConfig("Llm:ApiKey", "LLM_API_KEY", "");
        var llmModel = ResolveConfig("Llm:Model", "LLM_MODEL", "gpt-4.1-nano");
        var llmEndpoint = ResolveConfig("Llm:Endpoint", "LLM_ENDPOINT", "https://api.openai.com/v1");
        var llmApi = ResolveConfig("Llm:Api", "LLM_API", "openai-completions");
        var label = ResolveConfig("Label", "LABEL", "baseline");

        EvalModel = llmModel;

        if (string.IsNullOrEmpty(bridgePassword))
            throw new InvalidOperationException("Bridge password not configured. Set SCENARIO_EVAL_BRIDGE_PASSWORD or configure in appsettings.scenarioevals.local.json");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("API key not configured. Set SCENARIO_EVAL_API_KEY or configure in appsettings.scenarioevals.local.json");

        // GitHub Copilot OAuth: run interactive device flow to get a token (no API key needed)
        if (llmApi == "github-copilot-oauth")
        {
            var oauthClientId = ResolveConfig("Llm:OAuthClientId", "LLM_OAUTH_CLIENT_ID", "");
            llmApiKey = await GitHubDeviceFlow.AuthenticateAsync(
                string.IsNullOrEmpty(oauthClientId) ? null : oauthClientId,
                CancellationToken.None);
        }
        else if (string.IsNullOrEmpty(llmApiKey))
        {
            throw new InvalidOperationException("LLM API key not configured. Set SCENARIO_EVAL_LLM_API_KEY or configure in appsettings.scenarioevals.local.json");
        }

        // Create Bridge API client (logs in for session cookie)
        _bridgeClient = await BridgeApiClient.CreateAsync(
            bridgeUrl, bridgePassword, apiKey, tenantId,
            LoggerFactory.CreateLogger<BridgeApiClient>(),
            CancellationToken.None);

        // Create LLM client for actor/judge
        var evalLlmClient = CreateEvalLlmClient(llmApiKey, llmModel, llmEndpoint, llmApi);

        // Create actor service
        ActorService = new ActorService(
            evalLlmClient, llmModel,
            LoggerFactory.CreateLogger<ActorService>());

        // Create composite scorer
        Scorer = new CompositeScorer([
            new RecallScorer(),
            new JudgeScorer(evalLlmClient, llmModel, LoggerFactory.CreateLogger<JudgeScorer>()),
            new MemoryScorer()
        ]);

        // Initialize result store
        var dbPath = Path.Combine(AppContext.BaseDirectory, "eval-results", "scenario-evals.db");
        _resultStore = await SqliteResultStore.CreateAsync(dbPath);

        TranscriptDir = Path.Combine(AppContext.BaseDirectory, "eval-results");

        // Get git commit for tagging
        string? gitCommit = null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                gitCommit = (await proc.StandardOutput.ReadToEndAsync()).Trim();
                await proc.WaitForExitAsync();
            }
        }
        catch
        {
            // git not available, that's fine
        }

        RunId = await _resultStore.CreateRunAsync(label, gitCommit, null, llmModel);
    }

    public async Task DisposeAsync()
    {
        if (_resultStore is not null)
        {
            await _resultStore.FinishRunAsync(RunId);
            await _resultStore.DisposeAsync();
        }

        _bridgeClient?.Dispose();
        LoggerFactory.Dispose();
    }

    public ScenarioOrchestrator CreateOrchestrator()
    {
        return new ScenarioOrchestrator(
            BridgeClient,
            ActorService,
            Scorer,
            ResultStore,
            RunId,
            TranscriptDir,
            LoggerFactory.CreateLogger<ScenarioOrchestrator>());
    }

    /// <summary>
    /// Resolve config value from ScenarioEval section, then env var fallback, then default.
    /// </summary>
    private string ResolveConfig(string sectionKey, string envSuffix, string defaultValue)
    {
        // Try from config section first
        var value = Configuration[$"ScenarioEval:{sectionKey}"];
        if (!string.IsNullOrEmpty(value))
            return value;

        // Env var (SCENARIO_EVAL_ prefix already handled by AddEnvironmentVariables)
        value = Environment.GetEnvironmentVariable($"SCENARIO_EVAL_{envSuffix}");
        if (!string.IsNullOrEmpty(value))
            return value;

        return defaultValue;
    }

    private static ILlmClient CreateEvalLlmClient(string apiKey, string model, string endpoint, string api)
    {
        var httpClientFactory = new SimpleHttpClientFactory();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<DirectLlmClient>.Instance;
        var client = new DirectLlmClient(httpClientFactory, logger);

        // Determine credential kind and effective API type:
        // - github-copilot-api + PAT → GitHubPat (auto token exchange)
        // - github-copilot-oauth → GitHubOAuth (device flow token used directly)
        // - everything else → ApiKey
        var (kind, effectiveApi) = api switch
        {
            "github-copilot-oauth" => (CredentialKind.GitHubOAuth, "github-copilot-api"),
            "github-copilot-api" => (CredentialKind.GitHubPat, "github-copilot-api"),
            _ => (CredentialKind.ApiKey, api)
        };

        client.ConfigureCredentials(new LlmCredentials
        {
            Providers =
            [
                new LlmProviderCredential
                {
                    Name = "eval-llm",
                    Api = effectiveApi,
                    BaseUrl = endpoint,
                    Kind = kind,
                    // PAT and plain API key go in ApiKey; OAuth token goes in AccessToken
                    ApiKey = kind != CredentialKind.GitHubOAuth ? apiKey : null,
                    AccessToken = kind == CredentialKind.GitHubOAuth ? apiKey : null,
                    Models = [model],
                    DefaultModel = model
                }
            ]
        });

        return client;
    }

    /// <summary>Minimal IHttpClientFactory for standalone use.</summary>
    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

[CollectionDefinition("ScenarioEvals")]
public sealed class ScenarioEvalCollection : ICollectionFixture<ScenarioEvalFixture>;
