using System.Globalization;
using System.Threading.RateLimiting;
using Cortex.Contained.Bridge;
using Cortex.Contained.Bridge.Auth;
using Cortex.Contained.Bridge.Channels;
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Bridge.Logging;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Common.Auth;
using Cortex.Contained.Common.Security;
// Bridge.Storage removed — all message persistence handled by Agent Host
using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Channels.WebChat;
using Cortex.Contained.Channels.Voice;
using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Config.Yaml;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Stt;
using Cortex.Contained.Speech.Tts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Serilog;

// Pin the content root to the app's install directory rather than the current
// working directory. When the Launcher spawns the Bridge as a child process (MSIX
// startup task), the inherited CWD is C:\Windows, which would make ASP.NET resolve
// WebRootPath to C:\Windows\wwwroot — so every static file (the entire web UI) 404s
// while API endpoints still work. AppContext.BaseDirectory is the published Bridge
// folder, whose wwwroot is the real one. This also keeps dev (`dotnet run`) correct.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService();

// --- Runtime Data Directory ---
// All runtime data (config, database, secrets, logs, models) lives under %LOCALAPPDATA%\Cortex.
// Nothing is written to the source / content-root directory.
var cortexDataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cortex");
Directory.CreateDirectory(cortexDataDir);
var cortexConfigPath = Path.Combine(cortexDataDir, "cortex.yml");

// Valid TTS engine names for API responses and validation.
// TTS is always in composite/auto mode — no single-engine selection needed.

// --- YAML Configuration Source ---
// Add cortex.yml (optional, reload-on-change) from the data directory so env vars / CLI args override
builder.Configuration.AddYamlFile(cortexConfigPath, optional: true, reloadOnChange: true);

// --- Structured Logging (Serilog) ---
// All sinks are wrapped with RedactingSink to prevent API keys, tokens, and PII from appearing in logs.
builder.Host.UseSerilog((context, loggerConfig) =>
{
    loggerConfig
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "Cortex.Contained.Bridge")
        .WriteTo.Redacted(wt => wt.Console(
            formatProvider: CultureInfo.InvariantCulture,
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"))
        .WriteTo.Redacted(wt => wt.File(
            path: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cortex", "logs", "bridge-.log"),
            rollingInterval: Serilog.RollingInterval.Day,
            retainedFileCountLimit: 14,
            formatProvider: CultureInfo.InvariantCulture,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"));
});

// --- Secret Management ---
var secretStore = new DpapiSecretStore();
var secretManager = new SecretManager(
    secretStore,
    LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SecretManager>());

// --- Configuration via Options Pattern ---
builder.Services.AddOptions<BridgeConfig>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Post-configure: resolve DPAPI secrets and env-var fallbacks that can't be expressed as simple bindings
builder.Services.PostConfigure<BridgeConfig>(config =>
{
    // Hub token: explicit config > DPAPI-encrypted storage (auto-generated on first run)
    if (string.IsNullOrEmpty(config.HubToken))
    {
        config.HubToken = builder.Configuration["CORTEX_HUB_TOKEN"]
            ?? secretManager.GetOrCreateHubToken();
    }

    // LLM providers: if none bound from YAML/config, fall back to env-var-based single provider
    if (config.LlmProviders.Count == 0)
    {
        config.LlmProviders = LoadLlmProviders(builder.Configuration, secretManager);
    }
    else
    {
        // Resolve DPAPI-stored API keys for providers that have no key in config
        foreach (var provider in config.LlmProviders)
        {
            if (string.IsNullOrEmpty(provider.ApiKey))
            {
                provider.ApiKey = secretManager.GetApiKey(provider.Name);
            }

            // Also load OAuth refresh token + expiry for Anthropic OAuth providers
            if (string.Equals(provider.TokenType, "oauth", StringComparison.OrdinalIgnoreCase))
            {
                provider.RefreshToken ??= secretManager.GetRefreshToken(provider.Name);
                if (provider.TokenExpiresAt == 0)
                {
                    provider.TokenExpiresAt = secretManager.GetTokenExpiry(provider.Name);
                }
            }
        }

        // Second pass: resolve ApiKeyFrom references for providers still missing a key
        foreach (var provider in config.LlmProviders)
        {
            if (string.IsNullOrEmpty(provider.ApiKey) && !string.IsNullOrEmpty(provider.ApiKeyFrom))
            {
                var source = config.LlmProviders.Find(p =>
                    string.Equals(p.Name, provider.ApiKeyFrom, StringComparison.OrdinalIgnoreCase));
                if (source is not null && !string.IsNullOrEmpty(source.ApiKey))
                {
                    provider.ApiKey = source.ApiKey;
                }
            }
        }
    }
});

// Register the resolved BridgeConfig as a singleton for direct injection (backward compat with Worker)
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<BridgeConfig>>().Value);

builder.Services.AddSingleton<ISecretStore>(secretStore);
builder.Services.AddSingleton(secretManager);
builder.Services.AddSingleton<Cortex.Contained.Bridge.RemoteServices.RemoteServiceResolver>();

// --- Authentication ---
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddAuthentication(CortexSessionAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, CortexSessionAuthHandler>(
        CortexSessionAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

// --- Rate Limiting (login endpoint) ---
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
            }));
    options.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many login attempts. Try again later." }, ct).ConfigureAwait(false);
    };
});

// --- Early config snapshot (for Kestrel / channel setup that must happen before Build) ---
var earlyConfig = new BridgeConfig();
builder.Configuration.Bind(earlyConfig);

// --- Kestrel ---
if (earlyConfig.WebUi.Enabled)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(
            System.Net.IPAddress.Parse(earlyConfig.WebUi.BindAddress),
            earlyConfig.WebUi.Port);
    });
}

// --- HTTP Client for OAuth token refresh (used by Worker to refresh Anthropic tokens) ---
builder.Services.AddHttpClient("oauth-refresh");

// --- HTTP Client for Discord CDN downloads (used by Discord channel for voice messages) ---
builder.Services.AddHttpClient("discord-cdn");

// --- HTTP Client for embedding-provider connectivity probe ---
builder.Services.AddHttpClient("embedding-probe", c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
});

// --- Shared Speech Services (STT/TTS) ---
// Registered once, consumed by Voice channel, Discord channel, and any future voice-enabled channels.
// Configuration comes from the top-level "speech:" YAML section.
var speechConfig = earlyConfig.Speech;
var voiceConfig = earlyConfig.Channels.GetValueOrDefault("voice");

// Determine if any channel needs speech services
var speechNeeded = voiceConfig is { Enabled: true }
    || (earlyConfig.Channels.GetValueOrDefault("discord") is { Enabled: true }
        && bool.TryParse(earlyConfig.Channels.GetValueOrDefault("discord")?.Settings.GetValueOrDefault("DmVoiceTranscription"), out var evmCheck) && evmCheck);

var sttRegistered = false;
var ttsRegistered = false;

if (speechNeeded)
{
    // --- STT: whisper-stt sidecar (container cortex-stt, HTTP) ---
    // In-process Whisper.net was removed: STT now runs in the whisper-stt sidecar
    // (lib/whisper-stt). RemoteSpeechToText is ALWAYS available — the sidecar holds
    // the model (no host model file), and SttSidecarLifecycle starts the container
    // on demand. Language stays auto-detect (Whisper is natively multilingual).
    var defaultModelsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cortex", "models");

    builder.Services.AddSingleton<ISpeechToText>(sp =>
        new Cortex.Contained.Speech.Stt.RemoteSpeechToText(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("stt-sidecar"),
            sp.GetRequiredService<ILoggerFactory>()));

    // Streaming STT wrapper. Wraps the batch ISpeechToText (now remote) — the
    // same instance the VAD-driven consumers (Discord voice handler, local voice
    // channel) use, so there is one logical STT engine behind the sidecar.
    //
    // autoTranscribeThresholdBytes = 8000 → trigger a background pass every 0.25s
    // of new 16kHz mono 16-bit PCM (8000 bytes = 0.25s × 32kB/s). Required by the
    // LiveKit turn detector: it polls GetPartialResult() during silence, which only
    // returns text if background transcription has been running. Tighter cadence =
    // fresher partials when the user stops speaking = earlier commit. Each pass only
    // re-transcribes audio AFTER the LA-2-committed prefix (trim-by-committed-token,
    // see WhisperStreamingSpeechToText) so cost stays roughly constant regardless of
    // utterance length. The trigger self-throttles via backgroundTranscription.IsCompleted.
    builder.Services.AddSingleton<IStreamingSpeechToText>(sp =>
        new WhisperStreamingSpeechToText(
            sp.GetRequiredService<ISpeechToText>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<WhisperStreamingSpeechToText>(),
            autoTranscribeThresholdBytes: 8000));

    // Turn detector. Registered as ITurnDetector. Uses the LiveKit ONNX model
    // when it's been downloaded (via SpeechModels.targets); otherwise falls
    // back to NullTurnDetector so consumers can treat the service as always
    // available without null checks. Loading is eager — the ~400 MB model map
    // happens once at startup so the first voice turn doesn't pay the cost.
    var turnDetectorDir = Path.Combine(defaultModelsDir, "turn-detector");
    var turnDetectorModelOk = Directory.Exists(turnDetectorDir)
        && File.Exists(Path.Combine(turnDetectorDir, "model_q8.onnx"))
        && File.Exists(Path.Combine(turnDetectorDir, "tokenizer.json"))
        && File.Exists(Path.Combine(turnDetectorDir, "languages.json"));

    if (!turnDetectorModelOk)
    {
        Console.WriteLine(
            $"[WARNING] Turn detector model files not found in {turnDetectorDir}. " +
            "Using NullTurnDetector fallback (voice handlers will use fixed silence timeouts). " +
            "Build Bridge once to trigger SpeechModels.targets and download them.");
    }

    builder.Services.AddSingleton<ITurnDetector>(sp =>
    {
        if (!turnDetectorModelOk)
        {
            return new NullTurnDetector();
        }

        try
        {
            return LiveKitTurnDetector.Load(turnDetectorDir, sp.GetRequiredService<ILoggerFactory>());
        }
#pragma warning disable CA1031 // Broad catch: a detector-load failure must not crash Bridge startup.
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to load LiveKitTurnDetector from {turnDetectorDir}: {ex.Message}. Using NullTurnDetector fallback.");
            return new NullTurnDetector();
        }
#pragma warning restore CA1031
    });

    // STT is the always-available whisper-stt sidecar (RemoteSpeechToText), so it
    // never blocks voice registration; the SttSidecarLifecycle ensures the
    // container is up.
    sttRegistered = true;

    // --- TTS: Provider-based registration (always auto/composite mode) ---
    // All available TTS providers are created. The composite engine auto-detects
    // language and routes to the correct provider based on language config.
    // All TTS now runs in the unified uni-voices GPU sidecar (lib/uni-voices),
    // reached over HTTP via the "danish-tts" named client (127.0.0.1:8000). One
    // RemoteTtsProvider per engine; CompositeTtsEngine routes the engine:voice
    // refs from speech.tts.languages by provider Name. This replaces the
    // in-process Silero/Kokoro/Røst providers — no GPU inference runs in the
    // Bridge process anymore (the DirectML crash fix).
    builder.Services.AddSingleton<IReadOnlyList<ITtsProvider>>(sp =>
    {
        var lf = sp.GetRequiredService<ILoggerFactory>();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        HttpClient Client() => httpFactory.CreateClient("danish-tts");
        return new List<ITtsProvider>
        {
            new Cortex.Contained.Speech.Tts.RemoteTtsProvider("kokoro", Client(), lf,
            [
                new TtsVoiceInfo("af_heart", "en", VoiceGender.Female, "American — Heart"),
                new TtsVoiceInfo("am_adam", "en", VoiceGender.Male, "American — Adam"),
            ]),
            new Cortex.Contained.Speech.Tts.RemoteTtsProvider("roest-da", Client(), lf,
            [
                new TtsVoiceInfo("nic", "da", VoiceGender.Female, "Røst-v3 Danish (female)"),
                new TtsVoiceInfo("mic", "da", VoiceGender.Male, "Røst-v3 Danish (male)"),
            ]),
            new Cortex.Contained.Speech.Tts.RemoteTtsProvider("silero-v5-russian", Client(), lf,
            [
                new TtsVoiceInfo("kseniya", "ru", VoiceGender.Female, "Silero RU — Kseniya"),
                new TtsVoiceInfo("aidar", "ru", VoiceGender.Male, "Silero RU — Aidar"),
            ]),
        };
    });

    // Register ITextToSpeech: always composite (auto-detect language, route to correct provider)
    builder.Services.AddSingleton<ITextToSpeech>(sp =>
    {
        var providers = sp.GetRequiredService<IReadOnlyList<ITtsProvider>>();
        var lf = sp.GetRequiredService<ILoggerFactory>();

        var defaultLang = speechConfig.Tts.DefaultLanguage ?? "en";
        var genderStr = earlyConfig.Tenants.Values.FirstOrDefault(t => t.Default)?.VoiceGender ?? "female";
        var gender = string.Equals(genderStr, "male", StringComparison.OrdinalIgnoreCase)
            ? VoiceGender.Male : VoiceGender.Female;

        // Build language voice configs from YAML (voice references in "provider:voice" format)
        Dictionary<string, LanguageVoiceConfig>? languageConfigs = null;
        if (speechConfig.Tts.Languages is { Count: > 0 } langs)
        {
            languageConfigs = langs.ToDictionary(
                kv => kv.Key,
                kv => new LanguageVoiceConfig
                {
                    MaleVoice = kv.Value.MaleVoice,
                    FemaleVoice = kv.Value.FemaleVoice,
                },
                StringComparer.OrdinalIgnoreCase);
        }

        return new CompositeTtsEngine(
            providers,
            defaultLang,
            gender,
            languageConfigs,
            lf.CreateLogger<CompositeTtsEngine>());
    });

    // ttsRegistered is determined after providers are created (at service resolution time).
    // For now, mark as true — the VoiceChannel guard will check at runtime via GetService<ITextToSpeech>.
    ttsRegistered = true;

    // Language detector + per-channel sticky-current-language store.
    // Used by DiscordVoiceHandler to route TTS by detected language instead
    // of re-running Lingua per sentence (which misroutes short text).
    builder.Services.AddSingleton<Cortex.Contained.Speech.Tts.ILanguageDetector>(_ =>
    {
        // Always include the default language so the fallback set is non-empty.
        var fallback = speechConfig.Tts.DefaultLanguage ?? "en";
        var configuredCodes = speechConfig.Tts.Languages is { Count: > 0 } langs
            ? langs.Keys.ToArray()
            : [fallback];
        if (!configuredCodes.Contains(fallback, StringComparer.Ordinal))
        {
            configuredCodes = [.. configuredCodes, fallback];
        }
        return new Cortex.Contained.Speech.Tts.LinguaLanguageDetector(configuredCodes, fallback);
    });
    builder.Services.AddSingleton(_ =>
        new Cortex.Contained.Speech.Tts.ChannelLanguageStore(speechConfig.Tts.DefaultLanguage ?? "en"));
}

// NOTE: Bridge MessageStore has been removed. All message persistence is
// handled by the Agent Host's own MessageStore. The Bridge is a stateless
// message router — it does not maintain its own message history.

// --- Multi-Tenant ---
builder.Services.AddSingleton(sp =>
{
    var bridgeConfig = sp.GetRequiredService<BridgeConfig>();
    return new Cortex.Contained.Bridge.Tenants.TenantRegistry(
        bridgeConfig,
        () =>
        {
            // Persist callback: sync tenants back to config, then save to YAML
            sp.GetRequiredService<Cortex.Contained.Bridge.Tenants.TenantRegistry>().SyncToConfig(bridgeConfig);
            Cortex.Contained.Bridge.Setup.BridgeSettingsWriter.PersistSettingsToYaml(bridgeConfig, cortexConfigPath);
        },
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Cortex.Contained.Bridge.Tenants.TenantRegistry>());
});

builder.Services.AddSingleton(sp =>
    new Cortex.Contained.Bridge.Tenants.TenantRouter(
        sp.GetRequiredService<Cortex.Contained.Bridge.Tenants.TenantRegistry>(),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Cortex.Contained.Bridge.Tenants.TenantRouter>()));

// Register as singleton first, then as hosted service pointing to the same instance.
// This lets endpoints inject TenantHealthService while the host manages its lifecycle.
builder.Services.AddSingleton<Cortex.Contained.Bridge.Tenants.TenantHealthService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Cortex.Contained.Bridge.Tenants.TenantHealthService>());

// --- Channel Services ---
builder.Services.AddSingleton<ChannelManager>();
builder.Services.AddSingleton(sp =>
    new HubMessageDispatcher(
        sp.GetRequiredService<ChannelManager>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.Tenants.TenantRouter>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<HubMessageDispatcher>()));

// --- WebChat Channel ---
builder.Services.AddSingleton<WebChatChannel>();
builder.Services.AddSingleton<IWebChatHubProxy, WebChatHubProxyAdapter>();
builder.Services.AddHostedService<WebChatEventRelay>();

// --- Voice Channel ---
if (voiceConfig is { Enabled: true } && sttRegistered && ttsRegistered)
{
    var voiceOptions = new VoiceChannelOptions
    {
        PushToTalkHotkey = voiceConfig.Settings.GetValueOrDefault("PushToTalkHotkey")
            ?? builder.Configuration["Voice:PushToTalkHotkey"]
            ?? "Ctrl+Space",
        ChannelId = voiceConfig.Settings.GetValueOrDefault("ChannelId")
            ?? "voice-default",
    };

    if (bool.TryParse(voiceConfig.Settings.GetValueOrDefault("PushToTalk"), out var ptt))
    {
        voiceOptions = voiceOptions with { PushToTalk = ptt };
    }

    if (int.TryParse(voiceConfig.Settings.GetValueOrDefault("InputDeviceIndex"), out var inputIdx))
    {
        voiceOptions = voiceOptions with { InputDeviceIndex = inputIdx };
    }

    if (int.TryParse(voiceConfig.Settings.GetValueOrDefault("OutputDeviceIndex"), out var outputIdx))
    {
        voiceOptions = voiceOptions with { OutputDeviceIndex = outputIdx };
    }

    builder.Services.AddSingleton(voiceOptions);
    builder.Services.AddSingleton<IAudioCapture>(sp =>
        new WindowsAudioCapture(sp.GetRequiredService<ILoggerFactory>().CreateLogger<WindowsAudioCapture>(), voiceOptions.InputDeviceIndex));
    builder.Services.AddSingleton<IAudioPlayback>(sp =>
        new WindowsAudioPlayback(sp.GetRequiredService<ILoggerFactory>().CreateLogger<WindowsAudioPlayback>(), voiceOptions.OutputDeviceIndex));

    // Register VoiceChannel with an explicit factory so we can pass null for the
    // optional GlobalHotkeyListener that DI can't resolve as nullable.
    var pushToTalkEnabled = voiceOptions.PushToTalk;
    var pushToTalkHotkey = voiceOptions.PushToTalkHotkey;

    builder.Services.AddSingleton(sp =>
    {
        GlobalHotkeyListener? hotkeyListener = null;
        if (pushToTalkEnabled)
        {
            hotkeyListener = new GlobalHotkeyListener(
                pushToTalkHotkey,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<GlobalHotkeyListener>());
        }

        // Create the voice channel first (overlay callback needs a reference)
        VoiceChannel? channelRef = null;

        // Create desktop overlay for voice status indicator on a dedicated STA thread
        var overlayLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<VoiceOverlayWindow>();
        var overlay = VoiceOverlayFactory.Create(overlayLogger, () =>
        {
            // Main button click action depends on the current voice state
            if (channelRef is null)
            {
                return;
            }

            switch (channelRef.CurrentVoiceState)
            {
                case VoiceState.Listening:
                    // Stop listening and trigger STT processing
                    _ = channelRef.StopListeningAsync();
                    break;

                case VoiceState.Speaking:
                    // Pause TTS playback
                    channelRef.PauseSpeaking();
                    break;

                case VoiceState.Paused:
                    // Resume TTS playback
                    channelRef.ResumeSpeaking();
                    break;

                case VoiceState.Idle:
                    // Start listening
                    channelRef.StartListening();
                    break;

                case VoiceState.Processing:
                    // Do nothing — STT is in progress
                    break;
            }
        },
        () =>
        {
            // Stop button: cancel TTS entirely (visible during Speaking/Paused)
            channelRef?.CancelSpeaking();
        });

        // Wire the optional speaker-verification gate. When the model isn't
        // configured the verifier is null and the gate is inert.
        var voiceOptionsWithVerifier = voiceOptions with
        {
            SpeakerVerifier = Cortex.Contained.Bridge.Speech.VoiceIdVerifierSelector.Select(
                sp.GetService<Cortex.Contained.Speech.SpeakerId.ISpeakerVerifier>(),
                sp.GetRequiredService<BridgeConfig>().Speech),
            VerificationMetrics = sp.GetRequiredService<Cortex.Contained.Speech.SpeakerId.VerificationMetrics>(),
        };

        channelRef = new VoiceChannel(
            sp.GetRequiredService<IAudioCapture>(),
            sp.GetRequiredService<IAudioPlayback>(),
            sp.GetRequiredService<ISpeechToText>(),
            sp.GetRequiredService<ITextToSpeech>(),
            hotkeyListener,
            overlay,
            voiceOptionsWithVerifier,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<VoiceChannel>());

        return channelRef;
    });
}
else if (voiceConfig is { Enabled: true } && (!sttRegistered || !ttsRegistered))
{
    if (!sttRegistered)
    {
        Console.WriteLine("[WARNING] Voice channel is enabled but STT model is not available. Voice channel will not start. Download the model via Settings > Speech.");
    }

    if (!ttsRegistered)
    {
        Console.WriteLine("[WARNING] Voice channel is enabled but TTS model is not available. Voice channel will not start. Download or place the model via Settings > Speech.");
    }
}

// --- Discord Channel ---
var discordConfig = earlyConfig.Channels.GetValueOrDefault("discord");
if (discordConfig is { Enabled: true })
{
    // Discord bot token: DPAPI first, then YAML/config fallback (auto-migrate to DPAPI)
    var botToken = secretManager.GetDiscordBotToken() ?? "";
    if (string.IsNullOrWhiteSpace(botToken))
    {
        botToken = discordConfig.Settings.GetValueOrDefault("BotToken")
            ?? builder.Configuration["Discord:BotToken"]
            ?? "";

        // Migrate plaintext token to DPAPI-encrypted storage on first run
        if (!string.IsNullOrWhiteSpace(botToken))
        {
            secretManager.SetDiscordBotToken(botToken);
            // Remove from in-memory settings so it won't be persisted back to YAML
            discordConfig.Settings.Remove("BotToken");
        }
    }

    if (!string.IsNullOrWhiteSpace(botToken))
    {
        // Log migration notice if old global voice settings exist
        if (discordConfig.Settings.ContainsKey("VoiceChannelId") || discordConfig.Settings.ContainsKey("EnableVoice"))
        {
            Log.Information("Global Discord voice channel configuration has been removed. Configure voice channels per-tenant in the web UI.");
        }

        var discordOptions = new DiscordChannelOptions
        {
            BotToken = botToken,
            DmVoiceTranscription = bool.TryParse(
                discordConfig.Settings.GetValueOrDefault("DmVoiceTranscription")
                    ?? builder.Configuration["Discord:DmVoiceTranscription"],
                out var enableVoice) && enableVoice, // default false
            DmVoiceReplyMode = discordConfig.Settings.GetValueOrDefault("DmVoiceReplyMode")
                ?? builder.Configuration["Discord:DmVoiceReplyMode"]
                ?? "text",
            SilenceTimeoutMs = int.TryParse(
                discordConfig.Settings.GetValueOrDefault("SilenceTimeoutMs")
                    ?? builder.Configuration["Discord:SilenceTimeoutMs"],
                out var silenceTimeout) ? silenceTimeout : 1500,
            EnableBargeIn = !bool.TryParse(
                discordConfig.Settings.GetValueOrDefault("EnableBargeIn")
                    ?? builder.Configuration["Discord:EnableBargeIn"],
                out var enableBargeIn) || enableBargeIn, // default true
            EnableVoiceDaveEncryption = !bool.TryParse(
                discordConfig.Settings.GetValueOrDefault("EnableVoiceDaveEncryption")
                    ?? builder.Configuration["Discord:EnableVoiceDaveEncryption"],
                out var enableVoiceDave) || enableVoiceDave, // default true
            UseStreamingStt = !bool.TryParse(
                discordConfig.Settings.GetValueOrDefault("UseStreamingStt")
                    ?? builder.Configuration["Discord:UseStreamingStt"],
                out var useStreamingStt) || useStreamingStt, // default true
            UseTurnDetector = !bool.TryParse(
                discordConfig.Settings.GetValueOrDefault("UseTurnDetector")
                    ?? builder.Configuration["Discord:UseTurnDetector"],
                out var useTurnDetector) || useTurnDetector, // default true
            OutputGain = float.TryParse(
                discordConfig.Settings.GetValueOrDefault("OutputGain")
                    ?? builder.Configuration["Discord:OutputGain"],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var outputGain) && float.IsFinite(outputGain) && outputGain >= 0f
                    ? outputGain : 1.0f, // default 1.0 (no change)
            BargeInOnsetGuardMs = int.TryParse(
                discordConfig.Settings.GetValueOrDefault("BargeInOnsetGuardMs")
                    ?? builder.Configuration["Discord:BargeInOnsetGuardMs"],
                out var bargeInOnsetGuardMs) && bargeInOnsetGuardMs >= 0
                    ? bargeInOnsetGuardMs : 150, // default 150ms
            BargeInClassifierMode = Enum.TryParse<BargeInClassifierMode>(
                discordConfig.Settings.GetValueOrDefault("BargeInClassifierMode")
                    ?? builder.Configuration["Discord:BargeInClassifierMode"],
                ignoreCase: true,
                out var bargeInClassifierMode)
                    ? bargeInClassifierMode
                    : BargeInClassifierMode.HeuristicPlusLlm, // default
        };

        builder.Services.AddSingleton(discordOptions);
        builder.Services.AddSingleton<DiscordChannel>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<DiscordChannel>();

            // Inject speech services when DM voice transcription is enabled or any tenant has voice
            ISpeechToText? stt = sp.GetService<ISpeechToText>();
            IStreamingSpeechToText? streamingStt = sp.GetService<IStreamingSpeechToText>();
            ITurnDetector? turnDetector = sp.GetService<ITurnDetector>();
            ITextToSpeech? tts = sp.GetService<ITextToSpeech>();

            // HttpClient is needed to download voice message attachments from Discord CDN
            HttpClient? httpClient = (stt is not null)
                ? sp.GetRequiredService<IHttpClientFactory>().CreateClient("discord-cdn")
                : null;

            return new DiscordChannel(logger, discordOptions, stt, tts, httpClient, streamingStt, turnDetector);
        });

        // Enrollment progress notifier: posts mid-wizard status messages to the Discord
        // text channel where /voice-id enroll was issued. Depends on DiscordChannel to
        // reach the underlying DiscordSocketClient (which is not a DI singleton).
        builder.Services.AddSingleton<Cortex.Contained.Channels.Discord.IEnrollmentProgressNotifier>(sp =>
            new Cortex.Contained.Channels.Discord.DiscordEnrollmentProgressNotifier(
                sp.GetRequiredService<Cortex.Contained.Channels.Discord.DiscordChannel>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Cortex.Contained.Channels.Discord.DiscordEnrollmentProgressNotifier>>()));
    }
}

// --- Cloud Messaging Channel (AI Messenger cloud service connector) ---
// Off by default: requires channels:cloud-messaging:enabled: true in cortex.yml.
// Credentials (all required):
//   channels:cloud-messaging:settings:ClientId    — the bridge's registered OAuth client ID.
//   channels:cloud-messaging:settings:ServiceBaseUrl — AI Messenger service base URL.
//   DPAPI secret "cloud-messaging-private-key"    — PEM-encoded RSA private key for private_key_jwt.
//     Store it via: SecretManager.StoreApiKey("cloud-messaging-private-key", "<pem>")
// The Bridge exchanges a signed JWT assertion for a short-lived S2S access token
// via POST {ServiceBaseUrl}/oauth2/token, then connects to the SignalR hub.
var cloudMsgConfig = earlyConfig.Channels.GetValueOrDefault("cloud-messaging");
if (cloudMsgConfig is { Enabled: true })
{
    var serviceBaseUrl = cloudMsgConfig.Settings.GetValueOrDefault("ServiceBaseUrl") ?? "";
    if (!string.IsNullOrWhiteSpace(serviceBaseUrl))
    {
        var cloudMsgClientId = cloudMsgConfig.Settings.GetValueOrDefault("ClientId") ?? "";
        var cloudMsgPrivateKeyPem = secretManager.GetApiKey("cloud-messaging-private-key") ?? "";

        if (!string.IsNullOrWhiteSpace(cloudMsgClientId) && !string.IsNullOrWhiteSpace(cloudMsgPrivateKeyPem))
        {
            var channelId = cloudMsgConfig.Settings.GetValueOrDefault("ChannelId")
                ?? "cloud-messaging-default";

            var channelOptions = new Cortex.Contained.Channels.CloudMessaging.CloudMessagingChannelOptions
            {
                ChannelId = channelId,
                ServiceBaseUrl = serviceBaseUrl,
            };

            builder.Services.AddSingleton(channelOptions);

            builder.Services.AddHttpClient("cloud-messaging-token", c =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);
            });

            builder.Services.AddHttpClient("cloud-messaging-negotiate", c =>
            {
                c.Timeout = TimeSpan.FromSeconds(30);
            });

            builder.Services.AddSingleton<Cortex.Contained.Channels.CloudMessaging.Auth.IBridgeCredentialProvider>(sp =>
                new Cortex.Contained.Channels.CloudMessaging.Auth.PrivateKeyJwtBridgeCredentialProvider(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("cloud-messaging-token"),
                    tokenEndpointUrl: serviceBaseUrl.TrimEnd('/') + "/oauth2/token",
                    clientId: cloudMsgClientId,
                    rsaPrivateKeyPem: cloudMsgPrivateKeyPem));

            builder.Services.AddSingleton<Cortex.Contained.Channels.CloudMessaging.Negotiate.ICloudNegotiateClient>(sp =>
                new Cortex.Contained.Channels.CloudMessaging.Negotiate.CloudNegotiateClient(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("cloud-messaging-negotiate"),
                    sp.GetRequiredService<Cortex.Contained.Channels.CloudMessaging.Auth.IBridgeCredentialProvider>(),
                    serviceBaseUrl));

            builder.Services.AddSingleton<Cortex.Contained.Channels.CloudMessaging.Transport.ICloudTransport>(sp =>
                new Cortex.Contained.Channels.CloudMessaging.Transport.SignalRCloudTransport(
                    async () => (string?)await sp.GetRequiredService<Cortex.Contained.Channels.CloudMessaging.Auth.IBridgeCredentialProvider>()
                                                  .GetTokenAsync().ConfigureAwait(false),
                    sp.GetRequiredService<ILogger<Cortex.Contained.Channels.CloudMessaging.Transport.SignalRCloudTransport>>()));

            builder.Services.AddSingleton<Cortex.Contained.Channels.CloudMessaging.CloudMessagingChannel>(sp =>
                new Cortex.Contained.Channels.CloudMessaging.CloudMessagingChannel(
                    sp.GetRequiredService<ILogger<Cortex.Contained.Channels.CloudMessaging.CloudMessagingChannel>>(),
                    sp.GetRequiredService<Cortex.Contained.Channels.CloudMessaging.CloudMessagingChannelOptions>(),
                    sp.GetRequiredService<Cortex.Contained.Channels.CloudMessaging.Negotiate.ICloudNegotiateClient>(),
                    sp.GetRequiredService<Cortex.Contained.Channels.CloudMessaging.Transport.ICloudTransport>(),
                    sp.GetRequiredService<Cortex.Contained.Channels.WebChat.WebChatChannel>()));
        }
        else
        {
            Console.WriteLine("[WARNING] Cloud messaging channel is enabled but client credentials are missing. " +
                "Set 'ClientId' in channels:cloud-messaging:settings and store the RSA private key via " +
                "DPAPI key 'cloud-messaging-private-key' using SecretManager.");
        }
    }
    else
    {
        Console.WriteLine("[WARNING] Cloud messaging channel is enabled but 'ServiceBaseUrl' is not set in channels:cloud-messaging:settings.");
    }
}

// --- Voice speaker identification (Bridge-side cache) ---
// The cache resolves per-tenant HubClient instances via TenantRouter on every
// call, since HubClient is per-tenant (not a singleton in DI).
builder.Services.AddSingleton<Cortex.Contained.Bridge.SpeakerId.ITenantRouterForCache,
    Cortex.Contained.Bridge.SpeakerId.TenantRouterCacheAdapter>();
builder.Services.AddSingleton<Cortex.Contained.Bridge.SpeakerId.SignalRVoiceprintCache>();
builder.Services.AddSingleton<Cortex.Contained.Speech.SpeakerId.IVoiceprintStore>(sp =>
    sp.GetRequiredService<Cortex.Contained.Bridge.SpeakerId.SignalRVoiceprintCache>());

builder.Services.AddSingleton<Cortex.Contained.Speech.SpeakerId.FbankExtractor>();
builder.Services.AddSingleton<Cortex.Contained.Speech.SpeakerId.VerificationMetrics>();
builder.Services.AddSingleton(sp => Microsoft.Extensions.Options.Options.Create(new Cortex.Contained.Speech.SpeakerId.SpeakerIdOptions()));

// Runtime voice recording (replaces the deleted CaptureVoiceSamples flag).
// Controlled via Discord /voice-record; state is in-memory only.
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<Cortex.Contained.Bridge.Recording.RecordingController>();
builder.Services.AddSingleton<Cortex.Contained.Contracts.Recording.IRecordingController>(
    sp => sp.GetRequiredService<Cortex.Contained.Bridge.Recording.RecordingController>());
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<Cortex.Contained.Bridge.Recording.RecordingController>());

// Optional ONNX embedder. The path is read from the SpeakerId:ModelPath
// config; when not configured we fall back to the path the
// SpeechModels.targets MSBuild target downloads to. When the file is
// missing nothing is registered, the verifier resolves to null at the
// consumer, and the gate is inert — matches the Phase 1 fail-open
// contract.
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
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            modelPath = System.IO.Path.Combine(localAppData, "Cortex", "models", "speaker-id", "eres2netv2-base.onnx");
        }

        if (!string.IsNullOrWhiteSpace(modelPath) && System.IO.File.Exists(modelPath))
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
            Console.WriteLine($"[SpeakerId] Voice-id gate inactive — Local backend chosen but no ONNX model at '{modelPath}'.");
        }
    }

    if (embedderRegistered)
    {
        builder.Services.AddSingleton<Cortex.Contained.Speech.SpeakerId.ISpeakerVerifier>(sp =>
            new Cortex.Contained.Speech.SpeakerId.SpeakerVerifier(
                sp.GetRequiredService<Cortex.Contained.Speech.SpeakerId.ISpeakerEmbedder>(),
                sp.GetRequiredService<Cortex.Contained.Speech.SpeakerId.IVoiceprintStore>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Cortex.Contained.Speech.SpeakerId.SpeakerIdOptions>>(),
                sp.GetRequiredService<ILogger<Cortex.Contained.Speech.SpeakerId.SpeakerVerifier>>()));
    }
}

// --- SignalR for browser-facing WebChatHub ---
builder.Services.AddSignalR();

// --- Model Catalog (models.dev metadata) ---
builder.Services.AddSingleton<ModelCatalog>();

// --- Container lifecycle ---
builder.Services.AddSingleton<Cortex.Contained.Bridge.Tenants.IContainerManager,
    Cortex.Contained.Bridge.Tenants.DockerContainerManager>();

// --- Coda Coding Engine ---
builder.Services.AddOptions<Cortex.Contained.Bridge.Coding.CodaOptions>()
    .Bind(builder.Configuration.GetSection("Coding:Coda"));
builder.Services.AddSingleton(sp => Cortex.Contained.Bridge.Coding.CodingFoldersStore.Default());
builder.Services.AddSingleton(sp => Cortex.Contained.Bridge.Coding.CodaMcpSettingsStore.Default());
builder.Services.AddSingleton(sp => Cortex.Contained.Bridge.Coding.CodaSourceStore.Default());
// KILL_ON_JOB_CLOSE job so no coda process can outlive the Bridge (belt to the per-session reap).
builder.Services.AddSingleton<Cortex.Contained.Bridge.Coding.ICodaProcessGroup>(sp =>
    new Cortex.Contained.Bridge.Coding.WindowsJobProcessGroup(
        sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Coding.WindowsJobProcessGroup>>()));
builder.Services.AddSingleton<Cortex.Contained.Bridge.Coding.CodaSessionManager>();
builder.Services.AddSingleton<Cortex.Contained.Bridge.Coding.CodingHubBinder>();

// --- Token refresh (per-provider OAuth refresh strategies + service) ---
builder.Services.AddSingleton<Cortex.Contained.Bridge.Tokens.ITokenRefreshStrategy,
    Cortex.Contained.Bridge.Tokens.AnthropicOAuthRefreshStrategy>();
builder.Services.AddSingleton<Cortex.Contained.Bridge.Tokens.ITokenRefreshStrategy,
    Cortex.Contained.Bridge.Tokens.CopilotTokenExchangeStrategy>();
builder.Services.AddSingleton<Cortex.Contained.Bridge.Tokens.TokenRefreshService>();

// Proactive token-refresh sweep: re-mints expiring OAuth tokens ahead of expiry and
// re-pushes fresh credentials to connected agents. Registered as both a resolvable
// singleton and a hosted service so the host manages its lifecycle.
builder.Services.AddSingleton<Cortex.Contained.Bridge.Tokens.TokenRefreshBackgroundService>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Cortex.Contained.Bridge.Tokens.TokenRefreshBackgroundService>());

// --- Worker collaborators (focused lifecycle responsibilities) ---
// CredentialsPusher has an INTERNAL constructor (it takes the internal TokenRefreshService),
// so the DI container — which activates only via PUBLIC constructors — cannot build it from a
// plain type registration. Register it with an explicit factory: this assembly can call the
// internal ctor and reference the internal TokenRefreshService.
builder.Services.AddSingleton<Cortex.Contained.Bridge.Hosting.CredentialsPusher>(sp =>
    new Cortex.Contained.Bridge.Hosting.CredentialsPusher(
        sp.GetRequiredService<TenantRouter>(),
        sp.GetRequiredService<BridgeConfig>(),
        sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
        sp.GetRequiredService<SecretManager>(),
        sp.GetRequiredService<ModelCatalog>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.RemoteServices.RemoteServiceResolver>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.Tokens.TokenRefreshService>(),
        sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Hosting.CredentialsPusher>>()));
// Seam used by the proactive token-refresh sweep to re-push credentials. Resolves to the
// same CredentialsPusher singleton above.
builder.Services.AddSingleton<Cortex.Contained.Bridge.Tokens.ICredentialReplisher>(sp =>
    sp.GetRequiredService<Cortex.Contained.Bridge.Hosting.CredentialsPusher>());
builder.Services.AddSingleton<Cortex.Contained.Bridge.Hosting.TenantConnectionBootstrapper>();
builder.Services.AddSingleton<Cortex.Contained.Bridge.Hosting.ChannelLifecycleManager>();

// --- MCP plugin system (Bridge is the MCP host + credential boundary) ---
// Static auth (none/apiKey) resolves secrets from DPAPI; OAuth (oauth/auto) is layered on below.
builder.Services.AddHttpClient("mcp-oauth");
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.Auth.IMcpSecretResolver>(sp =>
    new Cortex.Contained.Bridge.Mcp.Auth.SecretManagerMcpSecretResolver(sp.GetRequiredService<SecretManager>()));
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.Auth.IMcpTokenSecretStore>(sp =>
    new Cortex.Contained.Bridge.Mcp.Auth.SecretManagerMcpTokenSecretStore(sp.GetRequiredService<SecretManager>()));
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.Auth.McpTokenStore>(sp =>
    new Cortex.Contained.Bridge.Mcp.Auth.McpTokenStore(
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.Auth.IMcpTokenSecretStore>(),
        sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Mcp.Auth.McpTokenStore>>()));
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.Auth.McpOAuthOptions>();
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.Auth.IMcpOAuthManager>(sp =>
    new Cortex.Contained.Bridge.Mcp.Auth.McpOAuthManager(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.Auth.McpTokenStore>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.Auth.McpOAuthOptions>(),
        TimeProvider.System,
        sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Mcp.Auth.McpOAuthManager>>()));
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.Auth.IMcpAuthManager>(sp =>
    new Cortex.Contained.Bridge.Mcp.Auth.McpStaticAuth(
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.Auth.IMcpSecretResolver>(),
        sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Mcp.Auth.McpStaticAuth>>()));
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.IMcpServerConnectionFactory>(sp =>
    new Cortex.Contained.Bridge.Mcp.McpServerConnectionFactory(
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.Auth.IMcpAuthManager>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.Auth.IMcpOAuthManager>(),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Mcp.McpServerConnectionFactory>>()));
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.McpHostService>(sp =>
    new Cortex.Contained.Bridge.Mcp.McpHostService(
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.IMcpServerConnectionFactory>(),
        sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Mcp.McpHostService>>()));
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.McpConfigStore>(sp =>
    new Cortex.Contained.Bridge.Mcp.McpConfigStore(
        sp.GetRequiredService<BridgeConfig>(),
        cortexConfigPath,
        sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Mcp.McpConfigStore>>()));
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.McpCatalogPusher>(sp =>
    new Cortex.Contained.Bridge.Mcp.McpCatalogPusher(
        sp.GetRequiredService<TenantRouter>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.McpHostService>(),
        sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Mcp.McpCatalogPusher>>()));
builder.Services.AddHostedService<Cortex.Contained.Bridge.Mcp.McpHostBootstrapper>();

// --- MCP action store (durable, encrypted store of record for approval-gated MCP mutations) ---
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.Actions.IMcpActionStore>(sp =>
    new Cortex.Contained.Bridge.Mcp.Actions.SqliteMcpActionStore(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cortex", "mcp", "actions.db"),
        sp.GetRequiredService<SecretManager>().GetOrCreateDatabaseKey(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Mcp.Actions.SqliteMcpActionStore>>()));

// --- MCP approval service + outbox dispatcher ---
// Every agent MCP invocation routes through McpActionService: read tools dispatch directly,
// mutation-classified tools persist a proposed action (NO remote call) and the dispatcher
// outbox dispatches ONLY human-approved actions — exactly the stored canonical arguments,
// at-most-once, with crash recovery to outcome_unknown.
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.IMcpInvocationTarget>(sp =>
    sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.McpHostService>());
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.Actions.McpActionDispatchRegistry>();
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.Actions.McpActionService>(sp =>
    new Cortex.Contained.Bridge.Mcp.Actions.McpActionService(
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.Actions.IMcpActionStore>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.IMcpInvocationTarget>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.McpConfigStore>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.Actions.McpActionDispatchRegistry>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Mcp.Actions.McpActionService>>()));
builder.Services.AddSingleton<Cortex.Contained.Bridge.Mcp.Actions.McpActionDispatcher>(sp =>
    new Cortex.Contained.Bridge.Mcp.Actions.McpActionDispatcher(
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.Actions.IMcpActionStore>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.IMcpInvocationTarget>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.McpConfigStore>(),
        sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.Actions.McpActionDispatchRegistry>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<Cortex.Contained.Bridge.Mcp.Actions.McpActionDispatcher>>()));
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Cortex.Contained.Bridge.Mcp.Actions.McpActionDispatcher>());

// --- Worker ---
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());

// Restart coordinator — shared state between POST /api/control/restart-bridge and
// the ApplicationStopped lifetime hook (see below) that decides exit code 73 vs 0.
builder.Services.AddSingleton<Cortex.Contained.Bridge.Control.RestartCoordinator>();

// --- Danish TTS sidecar (roest-da) ---
// Named HttpClient targets the docker-compose sidecar. Long timeout because
// generation can take a couple seconds per sentence.
builder.Services.AddHttpClient("danish-tts", c =>
{
    c.BaseAddress = new Uri("http://127.0.0.1:8000");
    c.Timeout = TimeSpan.FromSeconds(120); // generation can take a couple seconds/sentence
});

// --- STT sidecar (whisper-stt) ---
// Named HttpClient for the cortex-stt sidecar. Generous timeout because a long
// utterance transcription can take a couple of seconds.
builder.Services.AddHttpClient("stt-sidecar", c =>
{
    c.BaseAddress = new Uri("http://127.0.0.1:5300");
    c.Timeout = TimeSpan.FromSeconds(60);
});

// Resolvable singleton so the readiness probe shares the SAME provider instance
// that lands in the IReadOnlyList<ITtsProvider> list. Dispose is a no-op on the
// provider (it doesn't own the injected HttpClient), so being both a DI singleton
// and a member of CompositeTtsEngine's disposed list is safe.
builder.Services.AddSingleton<Cortex.Contained.Speech.Tts.RoestDanishTtsProvider>(sp =>
    new Cortex.Contained.Speech.Tts.RoestDanishTtsProvider(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("danish-tts"),
        sp.GetRequiredService<ILoggerFactory>()));

// Background probe that polls /health so the provider's live IsReady reflects the
// on-demand sidecar's actual state for CompositeTtsEngine's synth-time routing.
builder.Services.AddHostedService<Cortex.Contained.Bridge.Speech.DanishTtsReadinessProbe>();

// One DockerComposeCommandRunner instance backs both sidecar seams (TTS + STT),
// sharing the underlying `docker compose` shell-out.
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.DockerComposeCommandRunner>();
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.IComposeCommandRunner>(
    sp => sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DockerComposeCommandRunner>());
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.ISttComposeRunner>(
    sp => sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DockerComposeCommandRunner>());
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.IEmbeddingsComposeRunner>(
    sp => sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DockerComposeCommandRunner>());
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.IVoiceIdComposeRunner>(
    sp => sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DockerComposeCommandRunner>());
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.DanishTtsLifecycle>();
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.SttSidecarLifecycle>();
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.EmbeddingsSidecarLifecycle>();
builder.Services.AddSingleton<Cortex.Contained.Bridge.Speech.VoiceIdSidecarLifecycle>();

var app = builder.Build();

// If a Web-UI restart was requested before this graceful shutdown, exit with
// code 73 so the Launcher's BridgeProcessService respawns us. ApplicationStopped
// fires after Kestrel and hosted services have stopped — Environment.Exit at
// that point is safe and doesn't race in-flight requests.
{
    var restartCoord = app.Services.GetRequiredService<Cortex.Contained.Bridge.Control.RestartCoordinator>();
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopped.Register(() =>
    {
        if (restartCoord.IsRestartRequested)
        {
            Environment.Exit(Cortex.Contained.Bridge.Control.RestartCoordinator.RestartExitCode);
        }
    });
}

// --- Model Catalog (load models.dev metadata for context-window / max-output-token limits) ---
var modelCatalog = app.Services.GetRequiredService<ModelCatalog>();
_ = modelCatalog.InitializeAsync(); // Fire-and-forget; data will be ready before credentials are pushed

// --- TTS sidecar lifecycle: converge with config at startup (fire-and-forget) ---
{
    var danishLifecycle = app.Services.GetRequiredService<Cortex.Contained.Bridge.Speech.DanishTtsLifecycle>();
    var speech = app.Services.GetRequiredService<BridgeConfig>().Speech;
    _ = Task.Run(() => danishLifecycle.ReconcileAsync(SpeechToggles.EffectiveTts(speech), CancellationToken.None));
}

// --- STT sidecar lifecycle: converge with config at startup (fire-and-forget) ---
{
    var sttLifecycle = app.Services.GetRequiredService<Cortex.Contained.Bridge.Speech.SttSidecarLifecycle>();
    var speech = app.Services.GetRequiredService<BridgeConfig>().Speech;
    _ = Task.Run(() => sttLifecycle.ReconcileAsync(SpeechToggles.EffectiveStt(speech), CancellationToken.None));
}

// --- Embeddings sidecar lifecycle: converge with memory enable flag at startup ---
{
    var embeddingsLifecycle = app.Services.GetRequiredService<Cortex.Contained.Bridge.Speech.EmbeddingsSidecarLifecycle>();
    var memEnabled = app.Services.GetRequiredService<BridgeConfig>().Memory.Enabled;
    _ = Task.Run(() => embeddingsLifecycle.ReconcileAsync(memEnabled, CancellationToken.None));
}

// --- Voice-id sidecar lifecycle: converge with effective voice-id flag at startup ---
{
    var voiceIdLifecycle = app.Services.GetRequiredService<Cortex.Contained.Bridge.Speech.VoiceIdSidecarLifecycle>();
    var speech = app.Services.GetRequiredService<BridgeConfig>().Speech;
    _ = Task.Run(() => voiceIdLifecycle.ReconcileAsync(SpeechToggles.EffectiveVoiceId(speech), CancellationToken.None));
}

// --- Middleware Pipeline ---
app.UseRateLimiter();

// --- Health endpoint (unauthenticated) ---
app.MapHealthEndpoints();

// --- Auth Endpoints (unauthenticated) ---
app.MapAuthEndpoints();

// --- Setup API ---
app.MapSetupEndpoints(cortexConfigPath);
// --- Tenant API ---
app.MapTenantEndpoints();

// --- Coding Folders API ---
app.MapCodingFoldersEndpoints();
app.MapCodingMcpEndpoints();
app.MapCodaSourceEndpoints();

// --- Settings API ---
app.MapSettingsEndpoints(cortexConfigPath);

// --- UI telemetry + Control API ---
app.MapControlEndpoints();

// --- Channel Configuration API ---
app.MapChannelConfigEndpoints(cortexConfigPath);

// --- Voice State API (for web UI overlay) ---
app.MapVoiceStateEndpoints();

// --- Speech API ---
app.MapSpeechEndpoints(cortexConfigPath);

// --- Memory Management API ---
app.MapMemoryEndpoints(cortexConfigPath);

// --- MCP server management API ---
app.MapMcpEndpoints();
// --- MCP action approval API (list/approve/reject/cancel/reconcile gated mutations) ---
app.MapMcpActionEndpoints();
// --- Generic operational observability (subagent worker pool + MCP action history, no
//     prompt/message/result/args/eval content) — powers a future operations dashboard / the agent's
//     own workspace files. ---
app.MapOperationsEndpoints();
// --- MCP OAuth loopback callback (unauthenticated; state-protected) ---
app.MapMcpOAuthCallbackEndpoint();
// --- Message History API ---
// All message persistence is owned by the Agent Host. The Bridge proxies
// read/delete requests via the tenant's HubClient (SignalR).

// Tenant history endpoints are in TenantEndpoints.cs

// Resolve the validated config from DI to configure middleware
var bridgeConfig = app.Services.GetRequiredService<BridgeConfig>();

if (bridgeConfig.WebUi.Enabled)
{
    // Serve static files from wwwroot (login page must be accessible without auth)
    app.UseDefaultFiles();

    // Register .md so help articles under wwwroot/help are served with a real
    // content type. UseStaticFiles only serves known extensions; without this
    // mapping every help markdown fetch would 404.
    var staticContentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    staticContentTypeProvider.Mappings[".md"] = "text/markdown";
    app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticContentTypeProvider });

    // SPA fallback: all UI routes serve app.html (the unified SPA shell).
    // Alpine.js handles client-side routing based on the URL path.
    // Routes are registered without trailing slash; trailing slashes are handled
    // by RoutingOptions at the top (AppendTrailingSlash = false is the default,
    // and ASP.NET matches both /chat and /chat/ to the same endpoint).
    string[] spaRoutes = ["/chat", "/tenants", "/settings", "/help"];
    foreach (var route in spaRoutes)
    {
        app.MapGet(route, ServeSpa);
    }

    static IResult ServeSpa(IWebHostEnvironment env)
    {
        var filePath = Path.Combine(env.WebRootPath, "app.html");
        return Results.File(filePath, "text/html");
    }

    app.MapGet("/tenants/{**slug}", ServeSpa);
    app.MapGet("/help/{**slug}", ServeSpa);

    // Authentication + Authorization middleware (after static files so HTML/CSS/JS load freely)
    app.UseAuthentication();
    app.UseAuthorization();

    // Map the browser-facing SignalR hub (requires auth)
    app.MapHub<WebChatHub>("/hub/webchat").RequireAuthorization();

}

app.Run();

/// <summary>
/// Load LLM provider configurations from the config section.
/// Supports both structured config (appsettings.json) and env vars.
/// </summary>
static List<LlmProviderConfig> LoadLlmProviders(IConfiguration configuration, SecretManager secretManager)
{
    var providers = new List<LlmProviderConfig>();

    // Try loading from structured config section
    var section = configuration.GetSection("LlmProviders");
    if (section.Exists())
    {
        var children = section.GetChildren();
        foreach (var child in children)
        {
            var name = child["Name"];
            var api = child["Api"];
            if (name is not null && api is not null)
            {
                // Try config first, then DPAPI-encrypted storage
                var apiKey = child["ApiKey"] ?? secretManager.GetApiKey(name);

                providers.Add(new LlmProviderConfig
                {
                    Name = name,
                    Api = api,
                    BaseUrl = child["BaseUrl"],
                    ApiKey = apiKey,
                    Models = child.GetSection("Models").Get<List<string>>() ?? [],
                });
            }
        }
    }

    // Fallback: single OpenAI provider from env vars
    if (providers.Count == 0)
    {
        var apiKey = configuration["OPENAI_API_KEY"]
            ?? configuration["OpenAI:ApiKey"]
            ?? secretManager.GetApiKey("openai");

        if (apiKey is not null)
        {
            providers.Add(new LlmProviderConfig
            {
                Name = "openai",
                Api = "openai-completions",
                ApiKey = apiKey,
                BaseUrl = configuration["OPENAI_BASE_URL"] ?? configuration["OpenAI:BaseUrl"],
                Models = ["gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "gpt-3.5-turbo"],
            });
        }
    }

    return providers;
}
