using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Stt;
using Cortex.Contained.Speech.Tts;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the speech subsystem endpoints (<c>/api/speech/*</c>): STT/TTS status, model downloads,
/// voice/language selection, and the composite-TTS language configuration. All require authorization.
/// </summary>
internal static class SpeechEndpoints
{
    /// <summary>
    /// Maps the <c>/api/speech/*</c> endpoints onto <paramref name="app"/>.
    /// </summary>
    /// <param name="app">The web application to register endpoints on.</param>
    /// <param name="cortexConfigPath">Absolute path to <c>cortex.yml</c> for persistence.</param>
    public static void MapSpeechEndpoints(this WebApplication app, string cortexConfigPath)
    {
        // --- Speech API ---

        // Check speech subsystem status (STT/TTS model availability)
        app.MapGet("/api/speech/status", (BridgeConfig bridgeConfig, IServiceProvider sp) =>
        {
            // Determine the running TTS engine from config (provider-based, no type checking needed)
            var runningEngine = bridgeConfig.Speech.Tts.Engine?.ToLowerInvariant() ?? "kokoro";

            var localModelsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cortex", "models");

            var whisperModelPath = Path.Combine(localModelsDir, "ggml-base.bin");
            var whisperReady = File.Exists(whisperModelPath);
            var whisperSize = whisperReady ? new FileInfo(whisperModelPath).Length : 0;

            // Provider-based TTS status: generic, no hardcoded engine checks
            var providers = sp.GetService<IReadOnlyList<ITtsProvider>>() ?? [];
            var currentTtsEngine = runningEngine;

            // Find the active provider for readiness check
            var activeProvider = currentTtsEngine == "auto"
                ? providers.FirstOrDefault(p => p.IsReady)  // any ready provider means auto is ready
                : providers.FirstOrDefault(p => string.Equals(p.Name, currentTtsEngine, StringComparison.OrdinalIgnoreCase));

            var ttsReady = currentTtsEngine == "auto"
                ? providers.Any(p => p.IsReady)
                : activeProvider?.IsReady == true;
            var ttsDetail = currentTtsEngine == "auto"
                ? $"Auto mode: {providers.Count(p => p.IsReady)} of {providers.Count} providers ready"
                : activeProvider?.StatusDetail ?? $"Engine '{currentTtsEngine}' not available";

            // Per-provider status (generic — works for any provider)
            var providerStatuses = providers.Select(p => new
            {
                name = p.Name,
                ready = p.IsReady,
                detail = p.StatusDetail,
                voiceCount = p.Voices.Count,
                canDownload = p.CanDownloadModel,
                downloadLabel = p.DownloadLabel,
                voices = p.Voices.Select(v => new { v.Name, v.Language, gender = v.Gender.ToString().ToLowerInvariant(), v.Description }),
            }).ToArray();

            return Results.Ok(new
            {
                stt = new
                {
                    engine = "whisper",
                    ready = whisperReady,
                    modelPath = whisperModelPath,
                    modelSize = whisperSize,
                    detail = whisperReady ? "Whisper model ready" : "Whisper model not found. Click Download to install.",
                },
                tts = new
                {
                    engine = currentTtsEngine,
                    ready = ttsReady,
                    detail = ttsDetail,
                },
                providers = providerStatuses,
                runningEngine = "auto",
            });
        }).RequireAuthorization();

        // Download a speech model. Accepts "whisper" or "kokoro" as the model name.
        app.MapPost("/api/speech/download-model/{modelName}", async (string modelName, IHttpClientFactory httpFactory) =>
        {
            var modelsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cortex", "models");

            string modelPath;
            string downloadUrl;

            switch (modelName.ToLowerInvariant())
            {
                case "whisper":
                    modelPath = Path.Combine(modelsDir, "ggml-base.bin");
                    downloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";
                    break;
                case "kokoro":
                    modelPath = Path.Combine(modelsDir, KokoroTextToSpeech.DefaultModelFileName);
                    downloadUrl = KokoroTextToSpeech.DefaultModelDownloadUrl;
                    break;
                default:
                    return Results.Json(new { success = false, error = $"Unknown model: {modelName}" }, statusCode: 400);
            }

            if (File.Exists(modelPath))
            {
                return Results.Ok(new { success = true, message = "Model already exists", path = modelPath });
            }

            try
            {
                Directory.CreateDirectory(modelsDir);

                using var httpClient = httpFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromMinutes(15);

                using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var tempPath = modelPath + ".tmp";
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fileStream).ConfigureAwait(false);
                }

                File.Move(tempPath, modelPath, overwrite: true);

                return Results.Ok(new { success = true, message = $"{modelName} model downloaded successfully", path = modelPath });
            }
            catch (Exception ex)
            {
                var tempPath = modelPath + ".tmp";
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                return Results.Json(new { success = false, error = $"Download failed: {ex.Message}" }, statusCode: 500);
            }
        }).RequireAuthorization();

        // Get available TTS voices and current voice
        app.MapGet("/api/speech/voices", (IServiceProvider sp) =>
        {
            var tts = sp.GetService<ITextToSpeech>();
            if (tts is null)
            {
                return Results.Json(new { available = false, detail = "TTS engine not available. Download the model first." }, statusCode: 503);
            }

            return Results.Ok(new
            {
                available = true,
                currentVoice = tts.CurrentVoice,
                voices = tts.GetAvailableVoices(),
            });
        }).RequireAuthorization();

        // Change TTS voice at runtime and persist to YAML
        app.MapPost("/api/speech/voice", (VoiceChangeRequest request, IServiceProvider sp, BridgeConfig config, IHostEnvironment env) =>
        {
            if (string.IsNullOrWhiteSpace(request.Voice))
            {
                return Results.Json(new { error = "Voice name is required" }, statusCode: 400);
            }

            var tts = sp.GetService<ITextToSpeech>();
            var currentEngine = config.Speech.Tts.Engine?.ToLowerInvariant() ?? "kokoro";

            // If TTS is loaded and matches the configured engine, apply at runtime
            if (tts is not null)
            {
                var available = tts.GetAvailableVoices();
                if (available.Contains(request.Voice))
                {
                    tts.SetVoice(request.Voice);
                }
            }

            // Persist to config + YAML — find which provider owns this voice and update the right property
            var providers = sp.GetService<IReadOnlyList<ITtsProvider>>() ?? [];
            var owningProvider = providers.FirstOrDefault(p =>
                p.Voices.Any(v => string.Equals(v.Name, request.Voice, StringComparison.OrdinalIgnoreCase)));

            var providerName = owningProvider?.Name ?? currentEngine;
            switch (providerName)
            {
                case "silero":
                    config.Speech.Tts.SileroVoice = request.Voice;
                    break;
                case "windows-sapi":
                    config.Speech.Tts.WindowsVoiceName = request.Voice;
                    break;
                default:
                    config.Speech.Tts.KokoroVoice = request.Voice;
                    break;
            }

            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);

            return Results.Ok(new { success = true, currentVoice = request.Voice });
        }).RequireAuthorization();

        // Change STT language at runtime and persist to YAML
        app.MapGet("/api/speech/languages", (IServiceProvider sp) =>
        {
            var stt = sp.GetService<ISpeechToText>();
            if (stt is null)
            {
                return Results.Json(new { available = false, detail = "STT engine not available. Download the model first." }, statusCode: 503);
            }

            return Results.Ok(new
            {
                available = true,
                currentLanguage = stt.Language,
                languages = stt.SupportedLanguages,
            });
        }).RequireAuthorization();

        app.MapPost("/api/speech/language", (SttLanguageChangeRequest request, IServiceProvider sp, BridgeConfig config, IHostEnvironment env) =>
        {
            if (string.IsNullOrWhiteSpace(request.Language))
            {
                return Results.Json(new { error = "Language code is required" }, statusCode: 400);
            }

            var stt = sp.GetService<ISpeechToText>();
            if (stt is null)
            {
                return Results.Json(new { error = "STT engine not available" }, statusCode: 503);
            }

            try
            {
                stt.SetLanguage(request.Language);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }

            // Persist to config + YAML
            config.Speech.Stt.Language = stt.Language;
            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);

            return Results.Ok(new { success = true, currentLanguage = stt.Language });
        }).RequireAuthorization();

        // Get current TTS engine and all TTS settings
        app.MapGet("/api/speech/tts-engine", (BridgeConfig bridgeConfig, IServiceProvider sp) =>
        {
            var providers = sp.GetService<IReadOnlyList<ITtsProvider>>() ?? [];
            var defaultTenant = bridgeConfig.Tenants.Values.FirstOrDefault(t => t.Default);

            return Results.Ok(new
            {
                currentEngine = "auto",
                defaultLanguage = bridgeConfig.Speech.Tts.DefaultLanguage ?? "en",
                voiceGender = defaultTenant?.VoiceGender ?? "female",
                providers = providers.Select(p => new
                {
                    name = p.Name,
                    ready = p.IsReady,
                    voices = p.Voices.Select(v => new
                    {
                        name = v.Name,
                        language = v.Language,
                        gender = v.Gender.ToString().ToLowerInvariant(),
                        description = v.Description,
                    }),
                }),
                languages = bridgeConfig.Speech.Tts.Languages.ToDictionary(
                    kv => kv.Key,
                    kv => new { kv.Value.MaleVoice, kv.Value.FemaleVoice }),
            });
        }).RequireAuthorization();

        // Change voice gender for composite TTS (persists to YAML; requires restart)
        app.MapPost("/api/speech/voice-gender", async (HttpContext ctx, BridgeConfig config) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            var gender = body?.GetValueOrDefault("gender")?.Trim().ToLowerInvariant();

            if (gender is not "male" and not "female")
            {
                return Results.Json(new { error = "Valid genders: male, female" }, statusCode: 400);
            }

            // Update default tenant's voice gender
            var defaultTenant = config.Tenants.Values.FirstOrDefault(t => t.Default);
            if (defaultTenant is not null)
            {
                defaultTenant.VoiceGender = gender;
            }

            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);

            return Results.Ok(new { success = true, gender, restartRequired = true });
        }).RequireAuthorization();

        // Change default language for composite TTS (persists to YAML; requires restart)
        app.MapPost("/api/speech/default-language", async (HttpContext ctx, BridgeConfig config) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            var language = body?.GetValueOrDefault("language")?.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(language))
            {
                return Results.Json(new { error = "Language code is required" }, statusCode: 400);
            }

            config.Speech.Tts.DefaultLanguage = language;
            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);

            return Results.Ok(new { success = true, language, restartRequired = true });
        }).RequireAuthorization();

        // Save full language voice configuration (provider:voice references per language)
        app.MapPost("/api/speech/language-config", async (HttpContext ctx, BridgeConfig config, IServiceProvider sp) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<LanguageConfigRequest>();
            if (body?.Languages is null)
            {
                return Results.Json(new { error = "languages is required" }, statusCode: 400);
            }

            var defaultLang = body.DefaultLanguage ?? config.Speech.Tts.DefaultLanguage ?? "en";
            config.Speech.Tts.DefaultLanguage = defaultLang;

            config.Speech.Tts.Languages.Clear();
            Dictionary<string, LanguageVoiceConfig>? languageConfigs = null;
            if (body.Languages.Count > 0)
            {
                languageConfigs = new Dictionary<string, LanguageVoiceConfig>(StringComparer.OrdinalIgnoreCase);
                foreach (var (lang, voiceConfig) in body.Languages)
                {
                    config.Speech.Tts.Languages[lang] = new LanguageTtsConfig
                    {
                        MaleVoice = voiceConfig.MaleVoice,
                        FemaleVoice = voiceConfig.FemaleVoice,
                    };
                    languageConfigs[lang] = new LanguageVoiceConfig
                    {
                        MaleVoice = voiceConfig.MaleVoice,
                        FemaleVoice = voiceConfig.FemaleVoice,
                    };
                }
            }

            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);

            // Converge the Danish TTS sidecar with the new language map: configuring `da`
            // (roest-da) starts the container; removing it stops the container.
            // Fire-and-forget: a first-time `docker compose up` can block up to the
            // compose-start timeout (~60s) — we don't want the HTTP save to hang on it.
            // The container starts in the background; the provider reports not-ready
            // until the sidecar is up and the UI reflects readiness. ReconcileAsync is
            // self-contained (catches and logs its own failures), so this can't produce
            // an unobserved task exception.
            var danishLifecycle = sp.GetRequiredService<Cortex.Contained.Bridge.Speech.DanishTtsLifecycle>();
            _ = Task.Run(() => danishLifecycle.ReconcileAsync(config.Speech.Tts, CancellationToken.None));

            // Hot-reload the composite engine's language config (no restart needed)
            var tts = sp.GetService<ITextToSpeech>();
            if (tts is CompositeTtsEngine composite)
            {
                var defaultTenant = config.Tenants.Values.FirstOrDefault(t => t.Default);
                var genderStr = defaultTenant?.VoiceGender ?? "female";
                var gender = string.Equals(genderStr, "male", StringComparison.OrdinalIgnoreCase)
                    ? VoiceGender.Male : VoiceGender.Female;

                await composite.ReloadLanguageConfigAsync(defaultLang, gender, languageConfigs).ConfigureAwait(false);
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // Download a TTS provider's model files (provider-based — each provider knows its own download)
        app.MapPost("/api/speech/download-provider/{providerName}", async (string providerName, IServiceProvider sp, IHttpClientFactory httpFactory) =>
        {
            var providers = sp.GetService<IReadOnlyList<ITtsProvider>>() ?? [];
            var provider = providers.FirstOrDefault(p =>
                string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));

            if (provider is null)
            {
                return Results.NotFound(new { error = $"Provider '{providerName}' not found" });
            }

            if (!provider.CanDownloadModel)
            {
                return Results.Json(new { error = $"Provider '{providerName}' does not support model download" }, statusCode: 400);
            }

            if (provider.IsReady)
            {
                return Results.Ok(new { success = true, message = "Model already downloaded" });
            }

            using var httpClient = httpFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(15);

            var success = await provider.DownloadModelAsync(httpClient, cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return success
                ? Results.Ok(new { success = true, message = $"{providerName} model downloaded successfully. Restart required.", restartRequired = true })
                : Results.Json(new { error = $"Failed to download {providerName} model" }, statusCode: 500);
        }).RequireAuthorization();
    }
}
