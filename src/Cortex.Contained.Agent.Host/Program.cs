using System.Globalization;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Scheduler;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Config.Yaml;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Options;
using OllamaSharp;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- YAML Configuration Source ---
builder.Configuration.AddYamlFile("cortex.yml", optional: true, reloadOnChange: true);

// --- Structured Logging (Serilog) ---
// Agent Host runs inside Docker — no secrets to redact, but structured output is critical for observability.
builder.Host.UseSerilog((context, loggerConfig) =>
{
    loggerConfig
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "Cortex.Contained.Agent.Host")
        .WriteTo.Console(
            formatProvider: CultureInfo.InvariantCulture,
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: Path.Combine(
                Environment.GetEnvironmentVariable("CORTEX_DATA_PATH") ?? "/app/data",
                "logs", "agent-.log"),
            rollingInterval: Serilog.RollingInterval.Day,
            retainedFileCountLimit: 14,
            formatProvider: CultureInfo.InvariantCulture,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");
});

// --- Configuration via Options Pattern ---
builder.Services.AddOptions<AgentConfig>()
    .Bind(builder.Configuration.GetSection("Agent"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// --- Authentication ---
var hubToken = builder.Configuration["CORTEX_HUB_TOKEN"]
    ?? builder.Configuration["HubToken"]
    ?? builder.Configuration["Agent:Security:HubToken"]
    ?? throw new InvalidOperationException("Hub token not configured. Set CORTEX_HUB_TOKEN environment variable.");

builder.Services.AddAuthentication(HubTokenDefaults.AuthenticationScheme)
    .AddScheme<HubTokenAuthOptions, HubTokenAuthHandler>(
        HubTokenDefaults.AuthenticationScheme,
        options =>
        {
            options.Token = hubToken;

            // Configurable rate limiting
            if (int.TryParse(builder.Configuration["Auth:MaxAttempts"], out var maxAttempts))
            {
                options.MaxAttempts = maxAttempts;
            }

            if (int.TryParse(builder.Configuration["Auth:LockoutSeconds"], out var lockout))
            {
                options.LockoutDuration = TimeSpan.FromSeconds(lockout);
            }
        });
builder.Services.AddAuthorization();

// --- SignalR ---
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = 50 * 1024 * 1024; // 50MB for large import payloads
    options.StreamBufferCapacity = 20;
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

// --- Agent Services ---
var sandboxRoot = builder.Configuration["CORTEX_DATA_PATH"] ?? "/app/data";
var stateRoot = builder.Configuration["CORTEX_STATE_PATH"] ?? "/app/state";

// Initialize tool output truncation (saves oversized results to disk, cleans up stale files)
ToolOutputTruncator.Initialize(sandboxRoot);

// Register SessionConfig as a singleton resolved from AgentConfig.Sessions (backward compat)
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptions<AgentConfig>>().Value.Sessions);

builder.Services.AddSingleton<AgentSessionStore>();

// --- Message Store (per-tenant SQLite persistence) ---
builder.Services.AddSingleton(sp =>
    new Cortex.Contained.Agent.Host.Storage.MessageStore(
        Path.Combine(stateRoot, "messages.db"),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Cortex.Contained.Agent.Host.Storage.MessageStore>()));

// --- Operational metrics (thread-safe singleton; surfaced via health/ping) ---
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Agent.AgentMetrics>();

builder.Services.AddSingleton(sp =>
    new DirectLlmClient(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILogger<DirectLlmClient>>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.AgentMetrics>()));
builder.Services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<DirectLlmClient>());

// HttpClient for direct LLM provider calls.
// Note: .NET's built-in HTTP logging redacts query strings (shows ?*).
// Our LogLlmRequest logs the full URL including query params.
builder.Services.AddHttpClient("llm-direct");

// HttpClient for downloading media attachments (Discord image URLs, etc.)
builder.Services.AddHttpClient("media-download");

// HttpClient for the embedding-endpoint "Test connection" probe. Short timeout so
// the UI fails fast rather than hanging when an endpoint is unreachable.
builder.Services.AddHttpClient("embedding-probe", c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
});

// --- Message Queue ---
builder.Services.AddSingleton(sp =>
    new AgentMessageChannel(sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.AgentMetrics>()));

// --- Memory Services (MemoryMcp.Core) ---
builder.Services.AddMemoryMcpCore(builder.Configuration);

// --- Memory Settings Store (runtime-mutable overrides for memory options) ---
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Memory.MemorySettingsStore>();
builder.Services.AddSingleton<IPostConfigureOptions<MemoryMcpOptions>,
    Cortex.Contained.Agent.Host.Memory.MemoryMcpPostConfigure>();
builder.Services.AddSingleton<IPostConfigureOptions<Cortex.Contained.Agent.Host.Memory.MemoryCompactionOptions>,
    Cortex.Contained.Agent.Host.Memory.MemoryCompactionPostConfigure>();
builder.Services.AddSingleton<IOptionsChangeTokenSource<MemoryMcpOptions>,
    Cortex.Contained.Agent.Host.Memory.MemorySettingsChangeTokenSource<MemoryMcpOptions>>();
builder.Services.AddSingleton<IOptionsChangeTokenSource<Cortex.Contained.Agent.Host.Memory.MemoryCompactionOptions>,
    Cortex.Contained.Agent.Host.Memory.MemorySettingsChangeTokenSource<Cortex.Contained.Agent.Host.Memory.MemoryCompactionOptions>>();

// Skills: specialized workflows stored under data/skills/{name}/SKILL.md
var skillRegistry = new Cortex.Contained.Agent.Host.Agent.SkillRegistry(Path.Combine(sandboxRoot, "skills"));
builder.Services.AddSingleton(skillRegistry);

// --- Agent Tools ---
builder.Services.AddSingleton<IAgentTool>(new FileReadTool(sandboxRoot));
builder.Services.AddSingleton<IAgentTool>(new FileWriteTool(sandboxRoot, skillRegistry));
builder.Services.AddSingleton<IAgentTool>(new FileEditTool(sandboxRoot));
builder.Services.AddSingleton<IAgentTool>(new FileListTool(sandboxRoot));
builder.Services.AddSingleton<IAgentTool>(new FileDeleteTool(sandboxRoot));
builder.Services.AddSingleton<IAgentTool>(new FileFindTool(sandboxRoot));
builder.Services.AddSingleton<IAgentTool>(new RunCommandTool(sandboxRoot));
builder.Services.AddSingleton<IAgentTool>(new GrepTool(sandboxRoot));
builder.Services.AddSingleton<IAgentTool>(new DateTimeTool());
builder.Services.AddSingleton<IAgentTool>(sp =>
    new HistoryListChannelsTool(sp.GetRequiredService<MessageStore>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new HistoryReadTool(sp.GetRequiredService<MessageStore>()));
// ContextBootstrapTool removed — replaced by self-notes (self_notes_read/write)

// Self-notes: agent-managed operational knowledge (injected into system prompt)
var selfNotesStore = new Cortex.Contained.Agent.Host.Agent.SelfNotesStore(Path.Combine(stateRoot, "self-notes.md"));
builder.Services.AddSingleton(selfNotesStore);

// System prompt: user-editable templates + authorable segments (injected into system prompt)
builder.Services.AddSingleton(sp =>
    new Cortex.Contained.Agent.Host.Agent.SystemPromptStore(
        Path.Combine(sandboxRoot, "system-prompt.json"),
        sp.GetRequiredService<ILogger<Cortex.Contained.Agent.Host.Agent.SystemPromptStore>>()));

// --- External Agent (Claude Code) Relay ---
builder.Services.AddOptions<Cortex.Contained.Agent.Host.Coding.CodingAgentOptions>()
    .Bind(builder.Configuration.GetSection("Coding"));
builder.Services.AddSingleton(sp =>
    new Cortex.Contained.Agent.Host.Coding.CodingAgentSessionStore(stateRoot));
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Coding.CodingAgentEventBus>();
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Coding.ICodingAgent>(sp =>
    new Cortex.Contained.Agent.Host.Coding.SignalRCodingAgent(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Hubs.BridgeClientAccessor>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Cortex.Contained.Agent.Host.Coding.CodingAgentOptions>>().Value));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<Cortex.Contained.Agent.Host.Coding.CodingAgentInjectionService>();
builder.Services.AddHostedService<Cortex.Contained.Agent.Host.Coding.CodingAgentExpirySweeper>();

// External agent tools
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding.CodingSessionStartTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.ICodingAgent>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.CodingAgentSessionStore>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding.CodingSessionSendTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.ICodingAgent>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.CodingAgentSessionStore>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding.CodingSessionStatusTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.ICodingAgent>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.CodingAgentSessionStore>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding.CodingSessionHistoryTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.ICodingAgent>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.CodingAgentSessionStore>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding.CodingSessionListTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.ICodingAgent>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding.CodingSessionResumeTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.ICodingAgent>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.CodingAgentSessionStore>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding.CodingSessionEndTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.ICodingAgent>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.CodingAgentSessionStore>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding.CodingSessionInterruptTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.ICodingAgent>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.CodingAgentSessionStore>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding.CodingSessionRespondTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.ICodingAgent>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding.CodingSessionSetGoalTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.ICodingAgent>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.CodingAgentSessionStore>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding.CodingFoldersListTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Coding.ICodingAgent>()));

builder.Services.AddSingleton<IAgentTool>(new SelfNotesReadTool(selfNotesStore));
builder.Services.AddSingleton<IAgentTool>(new SelfNotesWriteTool(selfNotesStore));

// --- Voice speaker identification (Agent.Host side) ---
// The voiceprint SQLite store always exists (so the hub can answer GET
// queries cheaply even when speaker-ID is disabled). The embedder,
// orchestrator, and enrollment tools are only registered when a real ONNX
// model is available — Agent.Host must use the SAME model as the Bridge so
// the voiceprints it stores during enrollment will match what the Bridge's
// verifier computes during verification.
builder.Services.AddSingleton<Cortex.Contained.Speech.SpeakerId.IVoiceprintStore>(sp =>
    new Cortex.Contained.Agent.Host.SpeakerId.SqliteVoiceprintStore(stateRoot));
builder.Services.AddSingleton(sp => Microsoft.Extensions.Options.Options.Create(new Cortex.Contained.Speech.SpeakerId.SpeakerIdOptions()));
builder.Services.AddSingleton<Cortex.Contained.Speech.SpeakerId.FbankExtractor>();

{
    var backend = Enum.TryParse<Cortex.Contained.Speech.SpeakerId.SpeakerIdBackend>(
        builder.Configuration["SpeakerId:Backend"], ignoreCase: true, out var b)
        ? b
        : Cortex.Contained.Speech.SpeakerId.SpeakerIdBackend.Local;

    var embedderRegistered = false;

    if (backend == Cortex.Contained.Speech.SpeakerId.SpeakerIdBackend.Remote)
    {
        var endpoint = builder.Configuration["SpeakerId:RemoteEndpoint"] ?? "http://voice-id:5200";
        var modelId = builder.Configuration["SpeakerId:ModelId"] ?? "eres2netv2-base";
        var embeddingDim = int.TryParse(builder.Configuration["SpeakerId:EmbeddingDim"], out var dim) ? dim : 192;

        builder.Services.AddHttpClient("voice-id", c =>
        {
            c.BaseAddress = new Uri(endpoint);
            c.Timeout = TimeSpan.FromSeconds(5);
        });
        builder.Services.AddSingleton<Cortex.Contained.Speech.SpeakerId.ISpeakerEmbedder>(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            return new Cortex.Contained.Speech.SpeakerId.HttpSpeakerEmbedder(httpFactory, "voice-id", modelId, embeddingDim, sampleRate: 16000);
        });
        embedderRegistered = true;
        Console.WriteLine($"[SpeakerId] Remote backend -> {endpoint} (model={modelId}, dim={embeddingDim}).");
    }
    else
    {
        var modelPath = builder.Configuration["SpeakerId:ModelPath"];
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            modelPath = Path.Combine(AppContext.BaseDirectory, "models", "speaker-id", "eres2netv2-base.onnx");
        }

        if (File.Exists(modelPath))
        {
            var modelId = builder.Configuration["SpeakerId:ModelId"] ?? "eres2netv2-base";
            var inputName = builder.Configuration["SpeakerId:InputName"] ?? "feats";
            var outputName = builder.Configuration["SpeakerId:OutputName"] ?? "embed";
            var embeddingDim = int.TryParse(builder.Configuration["SpeakerId:EmbeddingDim"], out var dim) ? dim : 192;

            builder.Services.AddSingleton<Cortex.Contained.Speech.SpeakerId.ISpeakerEmbedder>(sp =>
                new Cortex.Contained.Speech.SpeakerId.OnnxSpeakerEmbedder(
                    modelPath, modelId, embeddingDim, inputName, outputName,
                    sp.GetRequiredService<Cortex.Contained.Speech.SpeakerId.FbankExtractor>()));
            embedderRegistered = true;
            Console.WriteLine($"[SpeakerId] Local backend -> {modelPath} (dim={embeddingDim}).");
        }
        else
        {
            Console.WriteLine($"[SpeakerId] Voice-id disabled — Local backend chosen but no ONNX model at '{modelPath}'.");
        }
    }

    if (embedderRegistered)
    {
        // KEEP the existing orchestrator + tool registrations EXACTLY as they
        // were. They consume ISpeakerEmbedder via DI, so they don't care which
        // backend won. Just preserve the lines.
        builder.Services.AddSingleton<Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator>(sp =>
            new Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator(
                sp.GetRequiredService<Cortex.Contained.Speech.SpeakerId.IVoiceprintStore>(),
                sp.GetRequiredService<ILogger<Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator>>(),
                timeProvider: null,
                bridgeClientAccessor: sp.GetRequiredService<Cortex.Contained.Agent.Host.Hubs.BridgeClientAccessor>()));

        builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Tools.IConversationToolGate>(sp =>
            sp.GetRequiredService<Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator>());

        builder.Services.AddSingleton<IAgentTool>(sp =>
            new Cortex.Contained.Agent.Host.SpeakerId.StartVoiceEnrollmentTool(sp.GetRequiredService<Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator>()));
        builder.Services.AddSingleton<IAgentTool>(sp =>
            new Cortex.Contained.Agent.Host.SpeakerId.DeclineVoiceEnrollmentTool(sp.GetRequiredService<Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator>()));
        builder.Services.AddSingleton<IAgentTool>(sp =>
            new Cortex.Contained.Agent.Host.SpeakerId.CancelVoiceEnrollmentTool(sp.GetRequiredService<Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator>()));
        builder.Services.AddSingleton<IAgentTool>(sp =>
            new Cortex.Contained.Agent.Host.SpeakerId.RequestVoiceReenrollmentTool(sp.GetRequiredService<Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator>()));
        builder.Services.AddSingleton<IAgentTool>(sp =>
            new Cortex.Contained.Agent.Host.SpeakerId.ConfirmVoiceReenrollmentTool(sp.GetRequiredService<Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator>()));
        builder.Services.AddSingleton<IAgentTool>(sp =>
            new Cortex.Contained.Agent.Host.SpeakerId.ForgetVoiceEnrollmentTool(sp.GetRequiredService<Cortex.Contained.Agent.Host.SpeakerId.EnrollmentOrchestrator>()));
    }
}

// These two are general tool infrastructure, NOT speaker-id-dependent: the voice-only
// gate hides the unconditional speak_after_delay/cancel_delayed_speech tools from
// non-voice conversations, and the conversation resolver is a hard dependency of the
// unconditional TransferSessionTool. Registering them inside the embedder-gated block
// broke any host without a voice-id backend (caught by the integration tests).
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Tools.IConversationToolGate,
    Cortex.Contained.Agent.Host.Tools.VoiceOnlyToolGate>();
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Tools.IConversationToolGate,
    Cortex.Contained.Agent.Host.Tools.MemoryDisabledToolGate>();
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.SpeakerId.SpeakerIdSettingsStore>();
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Tools.IConversationToolGate,
    Cortex.Contained.Agent.Host.Tools.VoiceIdDisabledToolGate>();
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Tools.IChannelConversationResolver,
    Cortex.Contained.Agent.Host.Tools.ChannelConversationResolver>();

// --- Session reminders (voice-only in-session timers) ---
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Reminders.IVoiceCueDeliverer, Cortex.Contained.Agent.Host.Reminders.BridgeVoiceCueDeliverer>();
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Reminders.SessionReminderService>();
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.SpeakAfterDelayTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Reminders.SessionReminderService>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Tools.BuiltIn.CancelDelayedSpeechTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Reminders.SessionReminderService>()));

// Shared proactive-message dispatch (used by SendMessageTool, TransferSessionTool,
// and any future tool that needs to push an agent-initiated message to a channel).
builder.Services.AddSingleton<IProactiveMessageDispatcher>(sp =>
    new ProactiveMessageDispatcher(
        sp.GetRequiredService<BridgeClientAccessor>(),
        sp.GetRequiredService<MessageStore>(),
        sp.GetRequiredService<ILogger<ProactiveMessageDispatcher>>()));

builder.Services.AddSingleton<IAgentTool>(sp =>
    new SendMessageTool(
        sp.GetRequiredService<ActiveChannelStore>(),
        sp.GetRequiredService<IProactiveMessageDispatcher>(),
        new AttachmentLoader(sandboxRoot)));

builder.Services.Configure<TransferSessionOptions>(
    builder.Configuration.GetSection("Agent:TransferSession"));
builder.Services.AddSingleton<ITopicSlicer>(sp =>
    new LlmTopicSlicer(
        sp.GetRequiredService<ILlmClient>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.IModelProvider>(),
        sp.GetRequiredService<ILogger<LlmTopicSlicer>>(),
        sp.GetService<IOptionsMonitor<TransferSessionOptions>>()));

builder.Services.AddSingleton<IAgentTool>(sp =>
    new TransferSessionTool(
        sp.GetRequiredService<AgentSessionStore>(),
        sp.GetRequiredService<ActiveChannelStore>(),
        sp.GetRequiredService<ITopicSlicer>(),
        () => sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.IAgentRuntime>(),
        sp.GetRequiredService<IProactiveMessageDispatcher>(),
        sp.GetRequiredService<MessageStore>(),
        sp.GetRequiredService<ILogger<TransferSessionTool>>(),
        sp.GetRequiredService<IChannelConversationResolver>(),
        sp.GetRequiredService<SubagentSessionStore>()));

builder.Services.AddSingleton<IAgentTool>(sp =>
    new RevertTransferTool(
        () => sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.IAgentRuntime>(),
        sp.GetRequiredService<ILogger<RevertTransferTool>>()));

// --- Memory Consolidation (shared dedup logic for extraction + ingest tool + compaction) ---
builder.Services.AddSingleton(sp =>
    new Cortex.Contained.Agent.Host.Memory.MemoryConsolidationService(
        sp.GetRequiredService<ILlmClient>(),
        sp.GetRequiredService<IMemoryService>(),
        sp.GetRequiredService<ILogger<Cortex.Contained.Agent.Host.Memory.MemoryConsolidationService>>()));

// --- Model Provider (shared between AgentRuntime and memory services/tools) ---
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Agent.IModelProvider, Cortex.Contained.Agent.Host.Agent.ModelProvider>();

// --- Memory Tools ---
builder.Services.AddSingleton<IAgentTool>(sp =>
    new MemoryIngestTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemoryConsolidationService>(),
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.IModelProvider>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new MemoryGetTool(sp.GetRequiredService<IMemoryService>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new MemoryUpdateTool(
        sp.GetRequiredService<IMemoryService>(),
        sp.GetRequiredService<IEmbeddingService>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new MemoryDeleteTool(sp.GetRequiredService<IMemoryService>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new MemorySearchTool(
        sp.GetRequiredService<IMemoryService>(),
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<IOptions<MemoryMcp.Core.Configuration.MemoryMcpOptions>>()));

// --- Memory Management (wraps IMemoryService + raw SQLite listing for web UI) ---
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Memory.MemoryManagementService>();
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Memory.IMemoryManagementService>(
    sp => sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemoryManagementService>());

// --- Automatic Memory Extraction (hosted background service with Channel<T> queue) ---
builder.Services.AddSingleton(sp =>
    new Cortex.Contained.Agent.Host.Memory.MemoryExtractionService(
        sp.GetRequiredService<ILlmClient>(),
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemoryConsolidationService>(),
        sp.GetRequiredService<ILogger<Cortex.Contained.Agent.Host.Memory.MemoryExtractionService>>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.AgentMetrics>()));
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemoryExtractionService>());

// --- Maintenance Store (tracks nightly task run history for catch-up after downtime) ---
builder.Services.AddSingleton(new Cortex.Contained.Agent.Host.Memory.MaintenanceStore(stateRoot));

// --- Memory Compaction (periodic dedup sweep, configurable via "MemoryCompaction" section) ---
builder.Services.Configure<Cortex.Contained.Agent.Host.Memory.MemoryCompactionOptions>(
    builder.Configuration.GetSection(Cortex.Contained.Agent.Host.Memory.MemoryCompactionOptions.SectionName));

builder.Services.AddSingleton(sp =>
    new Cortex.Contained.Agent.Host.Memory.MemoryCompactionService(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.IMemoryManagementService>(),
        sp.GetRequiredService<IMemoryService>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemoryConsolidationService>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemoryExtractionService>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.IModelProvider>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MaintenanceStore>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemorySettingsStore>(),
        sp.GetRequiredService<IOptions<Cortex.Contained.Agent.Host.Memory.MemoryCompactionOptions>>(),
        sp.GetRequiredService<ILogger<Cortex.Contained.Agent.Host.Memory.MemoryCompactionService>>()));
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemoryCompactionService>());

// --- Scheduler ---
builder.Services.AddSingleton(sp =>
    new SchedulerService(
        sp.GetRequiredService<AgentMessageChannel>(),
        stateRoot,
        sp.GetRequiredService<ILogger<SchedulerService>>()));

builder.Services.AddSingleton<IAgentTool>(sp =>
    new ScheduleTaskTool(sp.GetRequiredService<SchedulerService>(), sp.GetRequiredService<ActiveChannelStore>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new ListTasksTool(sp.GetRequiredService<SchedulerService>()));

// --- Async Sub-Agent Infrastructure ---
builder.Services.AddSingleton(sp =>
    new SubagentSessionStore(
        stateRoot,
        sp.GetRequiredService<ILogger<SubagentSessionStore>>()));
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IOptionsMonitor<AgentConfig>>().CurrentValue;
    return new SubagentRunnerRegistry(
        config.MaxConcurrentSubagents,
        sp.GetRequiredService<ILogger<SubagentRunnerRegistry>>());
});

// Generic, content-free subagent worker-pool observability (no prompt/message/result/eval
// content). Self-wires an AgentMetrics provider on construction so /health picks up live
// subagent-pool counts.
builder.Services.AddSingleton(sp =>
    new Cortex.Contained.Agent.Host.Agent.SubagentObservabilityService(
        sp.GetRequiredService<SubagentSessionStore>(),
        sp.GetRequiredService<SubagentRunnerRegistry>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.AgentMetrics>(),
        sp.GetRequiredService<TimeProvider>()));

// The executor builds worker context and drives the registered runner; the coordinator owns
// claiming, atomic concurrency admission, terminal persistence, requeue-on-shutdown, and readiness.
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Agent.ISubagentExecutor>(sp =>
    new Cortex.Contained.Agent.Host.Agent.SubagentExecutor(
        sp.GetRequiredService<SubagentRunnerRegistry>(),
        sp.GetRequiredService<ILlmClient>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.IModelProvider>(),
        stateRoot,
        sp.GetRequiredService<ILogger<Cortex.Contained.Agent.Host.Agent.SubagentExecutor>>(),
        sp.GetRequiredService<IMemoryService>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.SkillRegistry>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.SystemPromptStore>()));

builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Agent.SubagentExecutionCoordinator>(sp =>
{
    var llmClient = sp.GetRequiredService<ILlmClient>();
    var modelProvider = sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.IModelProvider>();
    var agentConfig = sp.GetRequiredService<IOptionsMonitor<AgentConfig>>();
    var subagentStore = sp.GetRequiredService<SubagentSessionStore>();
    var todoStore = sp.GetRequiredService<InMemoryTodoStore>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var runnerLogger = loggerFactory.CreateLogger<SubagentRunner>();
    var toolRegistry = sp.GetRequiredService<ToolRegistry>();

    // The coordinator creates and registers the runner (holding the slot + owning the cancellation
    // token); the executor drives that same registered instance so sub_agent_send injection lands.
    Func<SubagentTask, SubagentRunner> runnerFactory = task => new SubagentRunner(
        llmClient, toolRegistry, agentConfig.CurrentValue.MaxSubagentRounds, runnerLogger,
        subagentStore, task.TaskId, modelProvider, todoStore);

    return new Cortex.Contained.Agent.Host.Agent.SubagentExecutionCoordinator(
        subagentStore,
        sp.GetRequiredService<SubagentRunnerRegistry>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.ISubagentExecutor>(),
        runnerFactory,
        sp.GetRequiredService<AgentMessageChannel>(),
        loggerFactory.CreateLogger<Cortex.Contained.Agent.Host.Agent.SubagentExecutionCoordinator>());
});
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.SubagentExecutionCoordinator>());

builder.Services.AddSingleton<IAgentTool>(sp =>
    new SubAgentStartTool(
        sp.GetRequiredService<SubagentSessionStore>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.SubagentExecutionCoordinator>(),
        sp.GetRequiredService<ILogger<SubAgentStartTool>>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new SubAgentReadTool(
        sp.GetRequiredService<SubagentSessionStore>(),
        sp.GetRequiredService<ILogger<SubAgentReadTool>>(),
        sp.GetRequiredService<InMemoryTodoStore>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new SubAgentSendTool(
        sp.GetRequiredService<SubagentSessionStore>(),
        sp.GetRequiredService<SubagentRunnerRegistry>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.SubagentExecutionCoordinator>(),
        sp.GetRequiredService<ILogger<SubAgentSendTool>>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new SubAgentStopTool(
        sp.GetRequiredService<SubagentSessionStore>(),
        sp.GetRequiredService<SubagentRunnerRegistry>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.SubagentExecutionCoordinator>(),
        sp.GetRequiredService<ILogger<SubAgentStopTool>>()));

// --- Todo List Infrastructure ---
builder.Services.AddSingleton(sp =>
    new SqliteTodoStore(
        stateRoot,
        sp.GetRequiredService<ILogger<SqliteTodoStore>>()));
builder.Services.AddSingleton(sp =>
    new InMemoryTodoStore(
        sp.GetRequiredService<ILogger<InMemoryTodoStore>>()));
builder.Services.AddSingleton(sp =>
    new TodoStoreResolver(
        sp.GetRequiredService<SqliteTodoStore>(),
        sp.GetRequiredService<InMemoryTodoStore>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new TodosWriteTool(
        sp.GetRequiredService<TodoStoreResolver>(),
        sp.GetRequiredService<ILogger<TodosWriteTool>>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new TodosReadTool(
        sp.GetRequiredService<TodoStoreResolver>(),
        sp.GetRequiredService<ILogger<TodosReadTool>>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new TodosDeleteTool(
        sp.GetRequiredService<TodoStoreResolver>(),
        sp.GetRequiredService<ILogger<TodosDeleteTool>>()));

// --- MCP plugin gateway + dynamic tool store ---
// The Bridge (MCP host) pushes a namespaced tool catalog; McpToolStore holds the
// dynamic mcp__server__tool proxies, which ToolRegistry merges with its static tools.
builder.Services.AddOptions<Cortex.Contained.Agent.Host.Mcp.McpGatewayOptions>()
    .Bind(builder.Configuration.GetSection("Mcp"));
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Mcp.IMcpGateway>(sp =>
    new Cortex.Contained.Agent.Host.Mcp.SignalRMcpGateway(
        sp.GetRequiredService<BridgeClientAccessor>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Cortex.Contained.Agent.Host.Mcp.McpGatewayOptions>>().Value,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Cortex.Contained.Agent.Host.Mcp.SignalRMcpGateway>()));
builder.Services.AddSingleton(sp =>
    new Cortex.Contained.Agent.Host.Mcp.McpToolStore(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Mcp.IMcpGateway>(),
        sp.GetRequiredService<ILoggerFactory>()));

// Native MCP action tools: follow up on approval-gated mutations by action id — the safe
// alternative to ever repeating a mutating MCP tool call.
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Mcp.McpActionStatusTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Mcp.IMcpGateway>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Cortex.Contained.Agent.Host.Mcp.McpActionStatusTool>()));
builder.Services.AddSingleton<IAgentTool>(sp =>
    new Cortex.Contained.Agent.Host.Mcp.McpActionCancelTool(
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Mcp.IMcpGateway>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Cortex.Contained.Agent.Host.Mcp.McpActionCancelTool>()));

builder.Services.AddSingleton<ToolRegistry>();

builder.Services.AddSingleton<BridgeClientAccessor>();
builder.Services.AddSingleton<ActiveChannelStore>();

// --- Image aging (ContextManager) ---
builder.Services.Configure<ImageAgingConfig>(
    builder.Configuration.GetSection("Agent:ImageAging"));
builder.Services.AddSingleton<IPostConfigureOptions<ImageAgingConfig>,
    Cortex.Contained.Agent.Host.Memory.ImageAgingPostConfigure>();
builder.Services.AddSingleton<IOptionsChangeTokenSource<ImageAgingConfig>,
    Cortex.Contained.Agent.Host.Memory.MemorySettingsChangeTokenSource<ImageAgingConfig>>();
builder.Services.AddSingleton<Cortex.Contained.Agent.Host.Agent.IImageDescriber,
    Cortex.Contained.Agent.Host.Agent.LlmImageDescriber>();

// --- Conversation compaction (AgentRuntime.CompactConversationAsync) ---
builder.Services.Configure<ConversationCompactionConfig>(
    builder.Configuration.GetSection("Agent:ConversationCompaction"));
builder.Services.AddSingleton<IPostConfigureOptions<ConversationCompactionConfig>,
    Cortex.Contained.Agent.Host.Memory.ConversationCompactionPostConfigure>();
builder.Services.AddSingleton<IOptionsChangeTokenSource<ConversationCompactionConfig>,
    Cortex.Contained.Agent.Host.Memory.MemorySettingsChangeTokenSource<ConversationCompactionConfig>>();

builder.Services.AddSingleton(sp =>
    new AgentRuntime(
        sp.GetRequiredService<AgentSessionStore>(),
        sp.GetRequiredService<ILlmClient>(),
        sp.GetRequiredService<ToolRegistry>(),
        sp.GetRequiredService<SessionConfig>(),
        sp.GetRequiredService<AgentMessageChannel>(),
        sp.GetRequiredService<BridgeClientAccessor>(),
        sp.GetRequiredService<ActiveChannelStore>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sandboxRoot,
        stateRoot,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentRuntime>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.IModelProvider>(),
        sp.GetRequiredService<IOptionsMonitor<ImageAgingConfig>>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemoryExtractionService>(),
        sp.GetRequiredService<IMemoryService>(),
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Storage.MessageStore>(),
        sp.GetRequiredService<SubagentSessionStore>(),
        sp.GetRequiredService<TodoStoreResolver>(),
        selfNotesStore,
        sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.SystemPromptStore>(),
        skillRegistry,
        imageDescriber: sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.IImageDescriber>(),
        compactionOptions: sp.GetRequiredService<IOptionsMonitor<ConversationCompactionConfig>>(),
        metrics: sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.AgentMetrics>(),
        loggerFactory: sp.GetRequiredService<ILoggerFactory>(),
        memorySettingsStore: sp.GetRequiredService<Cortex.Contained.Agent.Host.Memory.MemorySettingsStore>(),
        subagentRegistry: sp.GetRequiredService<SubagentRunnerRegistry>(),
        // Loop-back edge of the at-least-once protocol: releasing a completion notification
        // must wake the coordinator's dispatch loop so it re-scans and redelivers.
        wakeSubagentCoordinator: sp.GetRequiredService<Cortex.Contained.Agent.Host.Agent.SubagentExecutionCoordinator>().SignalWorkAvailable));
builder.Services.AddSingleton<IAgentRuntime>(sp => sp.GetRequiredService<AgentRuntime>());

// Bootstrap context store removed — replaced by self-notes

// --- Consumer loop (processes enqueued messages) + session snapshot persistence ---
builder.Services.AddSingleton<IHostedService>(sp =>
    new AgentProcessingService(
        sp.GetRequiredService<IAgentRuntime>(),
        sp.GetRequiredService<AgentSessionStore>(),
        stateRoot,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentProcessingService>()));

var app = builder.Build();

// --- Wire the sandbox security-audit hook so blocked escape attempts are always logged,
//     even when a tool's own catch(ArgumentException) swallows the exception. ---
{
    var sandboxAuditLogger = app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Cortex.Contained.Agent.Host.Tools.SandboxPathResolver");
    var logSandboxEscapeBlocked = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1801, "SandboxEscapeBlocked"),
        "Sandbox escape attempt blocked: {Reason}");
    Cortex.Contained.Agent.Host.Tools.SandboxPathResolver.EscapeAudit =
        reason => logSandboxEscapeBlocked(sandboxAuditLogger, reason, null);
}

// --- Initialize Memory Store (creates SQLite schema + data directories) ---
using (var scope = app.Services.CreateScope())
{
    var memoryStore = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
    await memoryStore.InitializeAsync().ConfigureAwait(false);
}

// Queued/recovered subagent tasks are now dispatched by SubagentExecutionCoordinator (a hosted
// service) once Bridge + credentials + MCP-catalog readiness are all signaled — no startup call here.

// Ollama model check/pull runs in the background so it doesn't block startup.
// Memory tools will gracefully fail until the model is available.
_ = Task.Run(async () =>
{
    var memoryOpts = app.Services.GetRequiredService<IOptions<MemoryMcpOptions>>().Value;
    var ollamaEndpoint = new Uri(memoryOpts.Ollama.Endpoint);
    var modelName = memoryOpts.Ollama.Model;

    // Give Ollama a moment to start (it may still be initialising).
    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

    try
    {
        using var ollamaClient = new OllamaApiClient(ollamaEndpoint);
        if (await ollamaClient.IsRunningAsync().ConfigureAwait(false))
        {
            var localModels = await ollamaClient.ListLocalModelsAsync().ConfigureAwait(false);
            var modelFound = localModels.Any(m =>
                string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name, modelName + ":latest", StringComparison.OrdinalIgnoreCase) ||
                m.Name.StartsWith(modelName + ":", StringComparison.OrdinalIgnoreCase));

            if (!modelFound)
            {
                Log.Information("Embedding model '{Model}' not found locally. Pulling from Ollama...", modelName);
                await foreach (var status in ollamaClient.PullModelAsync(modelName).ConfigureAwait(false))
                {
                    // Consume the pull progress stream until complete
                }

                Log.Information("Successfully pulled embedding model '{Model}'.", modelName);
            }
            else
            {
                Log.Information("Embedding model '{Model}' is available.", modelName);
            }
        }
        else
        {
            Log.Warning(
                "Ollama is not reachable at {Endpoint}. Memory tools will fail until Ollama is available with model '{Model}'.",
                ollamaEndpoint, modelName);
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex,
            "Failed to check/pull Ollama model '{Model}' at startup. Memory tools may not work until the model is available.",
            modelName);
    }
});

// --- Health endpoint (unauthenticated) ---
app.MapGet("/health", (ToolRegistry toolRegistry, AgentSessionStore sessionStore) =>
{
    var toolCount = toolRegistry.Count;
    var sessionCount = sessionStore.GetAll().Count;
    var healthy = toolCount > 0;

    return Results.Ok(new
    {
        healthy,
        timestamp = DateTimeOffset.UtcNow,
        version = typeof(AgentHub).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        subsystems = new
        {
            toolRegistry = new { status = toolCount > 0 ? "ok" : "degraded", toolCount },
            sessionStore = new { status = "ok", sessionCount },
        },
    });
});

app.MapGet("/", () => "Cortex Agent Host is running.");

// --- SignalR Hub ---
app.UseAuthentication();
app.UseAuthorization();
app.MapHub<AgentHub>("/hub/agent");

app.Run();

