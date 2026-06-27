using System.Globalization;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Setup;

/// <summary>
/// Shared helpers for persisting the runtime <see cref="BridgeConfig"/> back to <c>cortex.yml</c>
/// and for redacting API keys in API responses. Extracted from <c>Program.cs</c> so the feature
/// endpoint modules (settings, speech, channels, …) and the DI persist callback all share one
/// implementation.
/// </summary>
internal static class BridgeSettingsWriter
{
    /// <summary>Redact an API key for display, showing only first 4 and last 3 characters.</summary>
    public static string? RedactApiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (key.Length <= 8)
        {
            return "****";
        }
        return string.Concat(key.AsSpan(0, 4), "****", key.AsSpan(key.Length - 3));
    }

    /// <summary>Persist current BridgeConfig settings to james.yml.</summary>
    public static void PersistSettingsToYaml(BridgeConfig config, string yamlPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Cortex Configuration");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"agentHubUrl: {config.AgentHubUrl}");
        sb.AppendLine();
        sb.AppendLine("webUi:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  enabled: {(config.WebUi.Enabled ? "true" : "false")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  port: {config.WebUi.Port}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  bindAddress: {config.WebUi.BindAddress}");
        sb.AppendLine();
        sb.AppendLine("llmProviders:");

        foreach (var p in config.LlmProviders)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  - name: {p.Name}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    api: {p.Api}");

            if (!string.IsNullOrEmpty(p.BaseUrl))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    baseUrl: {p.BaseUrl}");
            }

            if (!string.IsNullOrEmpty(p.TokenType) && p.TokenType != "bearer")
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    tokenType: {p.TokenType}");
            }

            if (!string.IsNullOrEmpty(p.ClientId))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    clientId: {p.ClientId}");
            }

            if (!string.IsNullOrEmpty(p.ApiKeyFrom))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    apiKeyFrom: {p.ApiKeyFrom}");
            }

            if (!string.IsNullOrEmpty(p.DefaultModel))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    defaultModel: {p.DefaultModel}");
            }

            if (!string.IsNullOrEmpty(p.MemoryModel))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    memoryModel: {p.MemoryModel}");
            }

            if (p.Models.Count > 0)
            {
                sb.AppendLine("    models:");
                foreach (var m in p.Models)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      - {m}");
                }
            }

            if (p.ModelDefinitions.Count > 0)
            {
                sb.AppendLine("    modelDefinitions:");
                foreach (var def in p.ModelDefinitions)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      - id: {def.Id}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        contextWindow: {def.ContextWindow}");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        maxOutputTokens: {def.MaxOutputTokens}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("llmProxy:");

        if (config.LlmProxy.FallbackOrder.Count > 0)
        {
            sb.AppendLine("  fallbackOrder:");
            foreach (var name in config.LlmProxy.FallbackOrder)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    - {name}");
            }
        }

        // Speech section (only written if non-default values are configured).
        // Predicate extracted + unit-tested in SpeechYamlPolicy — see the
        // 2026-05-24 per-language-voice persistence regression.
        var speech = config.Speech;
        var hasSpeechConfig = Cortex.Contained.Bridge.Setup.SpeechYamlPolicy.ShouldWriteSpeechSection(speech);

        if (hasSpeechConfig)
        {
            sb.AppendLine();
            sb.AppendLine("speech:");
            sb.AppendLine("  stt:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    engine: {speech.Stt.Engine}");
            if (!string.IsNullOrWhiteSpace(speech.Stt.WhisperModelPath))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    whisperModelPath: {speech.Stt.WhisperModelPath}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"    language: {speech.Stt.Language}");
            sb.AppendLine("  tts:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    engine: {speech.Tts.Engine}");
            if (speech.Tts.Engine == "kokoro" || speech.Tts.KokoroVoice != "af_heart")
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    kokoroVoice: {speech.Tts.KokoroVoice}");
            }

            if (!string.IsNullOrWhiteSpace(speech.Tts.KokoroModelPath))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    kokoroModelPath: {speech.Tts.KokoroModelPath}");
            }

            if (!string.IsNullOrWhiteSpace(speech.Tts.WindowsVoiceName))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    windowsVoiceName: {speech.Tts.WindowsVoiceName}");
            }

            if (speech.Tts.WindowsSpeechRate != 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    windowsSpeechRate: {speech.Tts.WindowsSpeechRate}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"    defaultLanguage: {speech.Tts.DefaultLanguage}");

            if (speech.Tts.Languages.Count > 0)
            {
                sb.AppendLine("    languages:");
                foreach (var (lang, langConfig) in speech.Tts.Languages)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      {lang}:");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        maleVoice: \"{langConfig.MaleVoice}\"");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        femaleVoice: \"{langConfig.FemaleVoice}\"");
                }
            }
        }

        // Sub-agent settings
        if (config.MaxSubagentRounds > 0)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"maxSubagentRounds: {config.MaxSubagentRounds}");
        }

        // Memory settings section
        MemoryConfigYamlWriter.AppendMemorySection(sb, config.Memory);

        // Channels section (exclude secrets like BotToken — those live in DPAPI)
        if (config.Channels.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("channels:");
            foreach (var (channelName, channelConfig) in config.Channels)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {channelName}:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    enabled: {(channelConfig.Enabled ? "true" : "false")}");
                var nonSecretSettings = channelConfig.Settings
                    .Where(kvp => !string.Equals(kvp.Key, "BotToken", StringComparison.OrdinalIgnoreCase));
                if (nonSecretSettings.Any())
                {
                    sb.AppendLine("    settings:");
                    foreach (var (key, value) in nonSecretSettings)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      {key}: {value}");
                    }
                }
            }
        }

        // Tenants section
        if (config.Tenants.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("tenants:");
            foreach (var (tenantId, tenant) in config.Tenants)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {tenantId}:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    endpoint: {tenant.Endpoint}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    default: {(tenant.Default ? "true" : "false")}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    enabled: {(tenant.Enabled ? "true" : "false")}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    port: {tenant.Port}");
                if (!string.IsNullOrEmpty(tenant.ImageVersion))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    imageVersion: {tenant.ImageVersion}");
                }

                if (tenant.IdleTimeoutMinutes > 0)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    idleTimeoutMinutes: {tenant.IdleTimeoutMinutes}");
                }
                if (tenant.Channels.Count > 0)
                {
                    sb.AppendLine("    channels:");
                    foreach (var ch in tenant.Channels)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      - {ch}");
                    }
                }
                if (!string.IsNullOrEmpty(tenant.DiscordUserId))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    discordUserId: \"{tenant.DiscordUserId}\"");
                }

                if (!string.IsNullOrEmpty(tenant.DiscordUsername))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    discordUsername: \"{tenant.DiscordUsername}\"");
                }

                if (!string.IsNullOrEmpty(tenant.DiscordGuildId))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    discordGuildId: \"{tenant.DiscordGuildId}\"");
                }

                if (!string.IsNullOrEmpty(tenant.DiscordVoiceChannelId))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    discordVoiceChannelId: \"{tenant.DiscordVoiceChannelId}\"");
                }

                if (!string.IsNullOrEmpty(tenant.VoiceGreeting))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    voiceGreeting: \"{tenant.VoiceGreeting}\"");
                }

                if (tenant.VoiceGender != "female")
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    voiceGender: {tenant.VoiceGender}");
                }

                if (tenant.ApiEnabled)
                {
                    sb.AppendLine("    apiEnabled: true");
                }
            }
        }

        var yamlDir = Path.GetDirectoryName(yamlPath);
        if (yamlDir is not null && !Directory.Exists(yamlDir))
        {
            Directory.CreateDirectory(yamlDir);
        }

        // Every YAML write goes through the store so a backup is taken first
        // (recoverable from cortex.yml.bak-<stamp> if anything goes wrong).
        Cortex.Contained.Bridge.Setup.CortexConfigStore.WriteWithBackup(yamlPath, sb.ToString());
    }
}
