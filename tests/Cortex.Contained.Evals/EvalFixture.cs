using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Evals;

/// <summary>
/// Shared fixture for eval tests. Provides a real <see cref="ILlmClient"/>
/// (wrapped in a <see cref="RecordingLlmClient"/>), real Ollama embedding
/// service, per-test isolated memory stores, and an <see cref="EvalRecorder"/>
/// that writes structured results to <c>eval-results/</c>.
/// <para>
/// Credential resolution order (first wins):
/// <list type="number">
///   <item>Environment variables (<c>EVAL_LLM_API_KEY</c>, etc.)</item>
///   <item><c>appsettings.eval.local.json</c> / <c>appsettings.eval.json</c></item>
/// </list>
/// Eval credentials are intentionally separate from production tenant credentials.
/// The Bridge's cortex.yml and DPAPI secrets are never read by the eval harness.
/// </para>
/// </summary>
public sealed class EvalFixture : IAsyncLifetime
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _config;

    /// <summary>The recording wrapper around the real LLM client.</summary>
    public RecordingLlmClient RecordingClient { get; private set; } = null!;

    /// <summary>The LLM client to pass to production code (same instance as <see cref="RecordingClient"/>).</summary>
    public ILlmClient LlmClient => RecordingClient;

    public string Model { get; private set; } = null!;

    /// <summary>API type used (e.g. "anthropic-messages", "openai-completions").</summary>
    public string Api { get; private set; } = null!;

    /// <summary>Ollama endpoint for embedding service instances.</summary>
    public string OllamaEndpoint { get; private set; } = null!;
    public string EmbeddingModel { get; private set; } = null!;
    public int EmbeddingDimensions { get; private set; }

    /// <summary>Shared recorder that collects results across all scenarios in this run.</summary>
    public EvalRecorder Recorder { get; } = new();

    public ILoggerFactory LoggerFactory => _loggerFactory;

    public EvalFixture()
    {
        _config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.eval.json", optional: true)
            .AddJsonFile("appsettings.eval.local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddConsole();
        });
    }

    public Task InitializeAsync()
    {
        // --- LLM client setup ---
        var httpFactory = new SimpleHttpClientFactory();
        var llmLogger = _loggerFactory.CreateLogger<DirectLlmClient>();
        var directClient = new DirectLlmClient(httpFactory, llmLogger);

        // Credential resolution order (first wins):
        //   1. Environment variables (EVAL_LLM_API_KEY, etc.)
        //   2. appsettings.eval.local.json / appsettings.eval.json
        //   3. eval.yml + eval-secrets.json (configured via Evals.Setup web UI)
        // Production credentials (cortex.yml) are NEVER used.
        var endpoint = Env("EVAL_LLM_ENDPOINT") ?? _config["Eval:LlmProvider:Endpoint"];
        var apiKey = Env("EVAL_LLM_API_KEY") ?? _config["Eval:LlmProvider:ApiKey"];
        var api = _config["Eval:LlmProvider:Api"];
        var model = Env("EVAL_LLM_MODEL") ?? _config["Eval:LlmProvider:Model"];
        var tokenType = _config["Eval:LlmProvider:TokenType"] ?? "bearer";

        // Fallback: read from eval.yml + eval-secrets.json (set up via Evals.Setup web UI)
        if (string.IsNullOrEmpty(apiKey))
        {
            var evalConfig = LoadEvalYamlConfig();
            if (evalConfig is not null)
            {
                apiKey = evalConfig.ApiKey;
                endpoint ??= evalConfig.BaseUrl;
                api ??= evalConfig.Api;
                model ??= evalConfig.Model;
            }
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException(
                "No eval LLM API key configured. Either:\n" +
                "  1. Set EVAL_LLM_API_KEY env var\n" +
                "  2. Set Eval:LlmProvider:ApiKey in appsettings.eval.local.json\n" +
                "  3. Run the Eval Setup UI: dotnet run --project tests/Cortex.Contained.Evals.Setup\n" +
                "Eval credentials are separate from production — Bridge credentials are not used.");
        }

        Api = api ?? "openai-completions";
        Model = model ?? "gpt-4.1-nano";

        var kind = ResolveCredentialKind(tokenType, Api);

        directClient.ConfigureCredentials(new LlmCredentials
        {
            Providers =
            [
                new LlmProviderCredential
                {
                    Name = "eval-provider",
                    Api = Api,
                    BaseUrl = endpoint, // null is fine — client resolves default per API type
                    Kind = kind,
                    // For OAuth providers the token goes into AccessToken, not ApiKey
                    ApiKey = kind == CredentialKind.ApiKey ? apiKey : null,
                    AccessToken = kind is CredentialKind.AnthropicOAuth or CredentialKind.GitHubOAuth
                        ? apiKey : null,
                    Models = [Model],
                },
            ],
        });

        // Wrap with recording decorator
        RecordingClient = new RecordingLlmClient(directClient);

        // --- Ollama config ---
        OllamaEndpoint = Env("EVAL_OLLAMA_ENDPOINT")
            ?? _config["Eval:Ollama:Endpoint"]
            ?? "http://localhost:11434";
        EmbeddingModel = _config["Eval:Ollama:EmbeddingModel"] ?? "qwen3-embedding:0.6b";
        EmbeddingDimensions = int.Parse(
            _config["Eval:Ollama:EmbeddingDimensions"] ?? "1024",
            System.Globalization.CultureInfo.InvariantCulture);

        // Populate recorder metadata
        Recorder.Model = Model;
        Recorder.Api = Api;
        Recorder.EmbeddingModel = EmbeddingModel;

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // Write the eval report before tearing down
        try
        {
            var path = Recorder.WriteReport();
            Console.WriteLine($"[EvalFixture] Results written to: {path}");
        }
#pragma warning disable CA1031 // Don't crash disposal on report write failure
        catch (Exception ex)
        {
            Console.WriteLine($"[EvalFixture] Failed to write eval report: {ex.Message}");
        }
#pragma warning restore CA1031

        _loggerFactory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates an isolated memory environment with its own SQLite database and memory directory.
    /// The caller is responsible for disposing the returned <see cref="EvalMemoryEnv"/>.
    /// </summary>
    public EvalMemoryEnv CreateMemoryEnv()
    {
        return new EvalMemoryEnv(this);
    }

    // ── Eval config loading ────────────────────────────────────────

    /// <summary>
    /// Reads eval LLM config from <c>%LOCALAPPDATA%\Cortex\eval.yml</c> and
    /// the API key from <c>%LOCALAPPDATA%\Cortex\secrets\eval-secrets.json</c>.
    /// These are configured via the Evals.Setup web UI.
    /// Returns null if not configured.
    /// </summary>
    private static EvalYamlConfig? LoadEvalYamlConfig()
    {
        var cortexDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cortex");
        var yamlPath = Path.Combine(cortexDir, "eval.yml");

        if (!File.Exists(yamlPath))
            return null;

        var lines = File.ReadAllLines(yamlPath);
        var config = new EvalYamlConfig();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#')) continue;

            var idx = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (idx < 0) continue;

            var key = trimmed[..idx].Trim();
            var val = trimmed[(idx + 1)..].Trim().Trim('"');

            if (key.Equals("api", StringComparison.OrdinalIgnoreCase)) config.Api = val;
            else if (key.Equals("baseUrl", StringComparison.OrdinalIgnoreCase)) config.BaseUrl = val;
            else if (key.Equals("model", StringComparison.OrdinalIgnoreCase)) config.Model = val;
        }

        // Read API key from eval-secrets.json (DPAPI encrypted)
        var secretsPath = Path.Combine(cortexDir, "secrets", "eval-secrets.json");
        if (File.Exists(secretsPath))
        {
            try
            {
                var json = File.ReadAllText(secretsPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ApiKey", out var val))
                {
                    var encrypted = val.GetString();
                    if (!string.IsNullOrEmpty(encrypted))
                    {
                        var store = new Cortex.Contained.Common.Security.DpapiSecretStore();
                        config.ApiKey = store.Unprotect(encrypted);
                    }
                }
            }
            catch
            {
                // Corrupted file or decryption failure — fall through
            }
        }

        return string.IsNullOrEmpty(config.ApiKey) ? null : config;
    }

    private sealed class EvalYamlConfig
    {
        public string? Api { get; set; }
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public string? ApiKey { get; set; }
    }

    // ── Credential helpers ──────────────────────────────────────────

    /// <summary>
    /// Resolves the <see cref="CredentialKind"/> from the provider's token type and API,
    /// mirroring <c>Worker.ResolveCredentialKind</c> in Bridge.
    /// </summary>
    private static CredentialKind ResolveCredentialKind(string tokenType, string api)
    {
        if (!string.Equals(tokenType, "oauth", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(tokenType, "pat", StringComparison.OrdinalIgnoreCase))
        {
            return CredentialKind.ApiKey;
        }

        if (string.Equals(tokenType, "pat", StringComparison.OrdinalIgnoreCase))
            return CredentialKind.GitHubPat;

        // "oauth" — distinguish Anthropic vs GitHub by the API type
        return string.Equals(api, "anthropic-messages", StringComparison.OrdinalIgnoreCase)
            ? CredentialKind.AnthropicOAuth
            : CredentialKind.GitHubOAuth;
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}

/// <summary>
/// Minimal <see cref="IHttpClientFactory"/> that returns a plain <see cref="HttpClient"/>.
/// </summary>
internal sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

/// <summary>
/// An isolated memory environment for a single eval test. Provides
/// <see cref="IMemoryService"/>, <see cref="IEmbeddingService"/>, and
/// <see cref="MemoryExtractionService"/> with their own temp database.
/// <para>
/// The extraction service is started lazily (on first <c>RunExtractionAsync</c> call)
/// and kept alive across multiple extractions within the same scenario. Call
/// <see cref="StopExtractionServiceAsync"/> after all extractions are done, before
/// disposing.
/// </para>
/// </summary>
public sealed class EvalMemoryEnv : IDisposable
{
    private readonly string _tempDir;

    public IMemoryService MemoryService { get; }
    public IEmbeddingService EmbeddingService { get; }
    public MemoryExtractionService ExtractionService { get; }
    public IMemoryStore MemoryStore { get; }
    public MemoryMcpOptions Options { get; }

    /// <summary>
    /// Whether <see cref="ExtractionService"/> has been started via <c>StartAsync</c>.
    /// Set to <see langword="true"/> after the first extraction call.
    /// </summary>
    public bool ExtractionServiceStarted { get; set; }

    /// <summary>
    /// Cancellation token source used to stop the extraction background service.
    /// The token is passed to <see cref="MemoryExtractionService.StartAsync"/>.
    /// </summary>
    public CancellationTokenSource ServiceCts { get; } = new();

    public EvalMemoryEnv(EvalFixture fixture)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "james-evals", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        Options = new MemoryMcpOptions
        {
            DataDirectory = _tempDir,
            DatabaseFileName = "memory.db",
            MemoriesSubdirectory = "memories",
            Ollama = new OllamaOptions
            {
                Endpoint = fixture.OllamaEndpoint,
                Model = fixture.EmbeddingModel,
                Dimensions = fixture.EmbeddingDimensions,
            },
        };

        // Ensure memories directory exists
        Directory.CreateDirectory(Options.MemoriesDirectory);

        var optionsWrapper = Microsoft.Extensions.Options.Options.Create(Options);
        var optionsMonitor = new StaticOptionsMonitor<MemoryMcpOptions>(Options);
        var logger = fixture.LoggerFactory;

        // Embedding service
        EmbeddingService = new OllamaEmbeddingService(optionsMonitor, logger.CreateLogger<OllamaEmbeddingService>());

        // Memory store (SQLite + sqlite-vec)
        MemoryStore = new SqliteVecMemoryStore(optionsWrapper, new NullContentEncryptor(), logger.CreateLogger<SqliteVecMemoryStore>());
        ((SqliteVecMemoryStore)MemoryStore).InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

        // Chunking
        var chunking = new WordChunkingService(optionsWrapper);

        // Memory service (constructor order: chunking, embedding, store, optionsMonitor, logger)
        MemoryService = new MemoryService(
            chunking, EmbeddingService, MemoryStore,
            optionsMonitor,
            logger.CreateLogger<MemoryService>());

        // Consolidation service — LLM-based dedup shared by extraction & ingest
        var consolidationService = new MemoryConsolidationService(
            fixture.LlmClient, MemoryService,
            logger.CreateLogger<MemoryConsolidationService>());

        // Extraction service — uses the recording LLM client from fixture
        ExtractionService = new MemoryExtractionService(
            fixture.LlmClient, EmbeddingService, consolidationService,
            logger.CreateLogger<MemoryExtractionService>());
    }

    /// <summary>Seeds the memory store with pre-existing memories.</summary>
    public async Task<string> SeedMemoryAsync(string content, string? title = null, List<string>? tags = null)
    {
        var result = await MemoryService.IngestAsync(content, title, tags, force: true).ConfigureAwait(false);
        return result.MemoryId!;
    }

    /// <summary>Returns all memories currently in the store (via direct SQLite query).</summary>
    public async Task<List<(string MemoryId, string Content)>> GetAllMemoriesAsync()
    {
        var mgmt = new MemoryManagementService(
            MemoryService,
            Microsoft.Extensions.Options.Options.Create(Options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MemoryManagementService>.Instance);

        var result = await mgmt.ListAsync(limit: 1000).ConfigureAwait(false);
        return result.Items.Select(i => (i.MemoryId, i.Content)).ToList();
    }

    /// <summary>
    /// Stops the extraction background service gracefully if it was started.
    /// Must be called before <see cref="Dispose"/> to ensure the channel is
    /// drained and the consumer loop exits cleanly.
    /// </summary>
    public async Task StopExtractionServiceAsync()
    {
        if (!ExtractionServiceStarted)
            return;

        try
        {
            await ExtractionService.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort stop — don't crash disposal
        catch
        {
            // The service may already be stopped or faulted
        }
#pragma warning restore CA1031

        ExtractionServiceStarted = false;
    }

    public void Dispose()
    {
        // Cancel the service CTS (in case StopAsync wasn't called or timed out)
        try
        {
            ServiceCts.Cancel();
            ServiceCts.Dispose();
        }
#pragma warning disable CA1031
        catch
        {
            // Best-effort
        }
#pragma warning restore CA1031

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}

/// <summary>
/// Simple <see cref="IOptionsMonitor{TOptions}"/> implementation that returns
/// a fixed value. Used in integration/eval tests where NSubstitute is not available.
/// </summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
