using System.Globalization;
using Cortex.Contained.Bridge.Channels;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the channel-configuration endpoints (<c>/api/channels*</c>, <c>/api/discord/bot-info</c>).
/// Lists available channels, exposes Discord bot info, and updates channel enable/settings state.
/// All require authorization.
/// </summary>
internal static class ChannelConfigEndpoints
{
    /// <summary>
    /// Maps the channel-configuration endpoints onto <paramref name="app"/>.
    /// </summary>
    /// <param name="app">The web application to register endpoints on.</param>
    /// <param name="cortexConfigPath">Absolute path to <c>cortex.yml</c> for persistence.</param>
    public static void MapChannelConfigEndpoints(this WebApplication app, string cortexConfigPath)
    {
        // --- Channel Configuration API ---

        // Returns all available channel types with their current status and config
        app.MapGet("/api/channels", (BridgeConfig config, SecretManager secretManager) =>
        {
            var channels = new List<object>();

            // WebChat is always present and cannot be disabled
            channels.Add(new
            {
                type = "webchat",
                displayName = "Web Chat",
                description = "Browser-based chat interface at localhost",
                enabled = config.WebUi.Enabled,
                isDefault = true,
                status = "connected",
                settings = new Dictionary<string, object>(),
            });

            // Voice
            var voiceConf = config.Channels.GetValueOrDefault("voice");
            var voiceEnabled = voiceConf is { Enabled: true };
            channels.Add(new
            {
                type = "voice",
                displayName = "Voice",
                description = "Voice input and output",
                enabled = voiceEnabled,
                isDefault = false,
                status = voiceEnabled ? "configured" : "disabled",
                settings = voiceConf?.Settings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            });

            // Discord
            var discordConf = config.Channels.GetValueOrDefault("discord");
            var discordEnabled = discordConf is { Enabled: true };
            var discordSettings = new Dictionary<string, string>(
                discordConf?.Settings ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            // Indicate whether a bot token is stored in DPAPI (without exposing the actual value)
            discordSettings["BotToken"] = secretManager.GetDiscordBotToken() is not null ? "********" : "";
            channels.Add(new
            {
                type = "discord",
                displayName = "Discord",
                description = "Discord bot that responds to DMs and channel mentions",
                enabled = discordEnabled,
                isDefault = false,
                status = discordEnabled ? "configured" : "disabled",
                settings = discordSettings,
            });

            return Results.Ok(channels);
        }).RequireAuthorization();

        // Returns the Discord bot's username and ID (for setup instructions).
        // Available after the Discord channel has connected.
        app.MapGet("/api/discord/bot-info", (ChannelManager channelManager) =>
        {
            // Find a Discord channel instance and read its bot info
            foreach (var channel in channelManager.GetAllChannels())
            {
                if (channel is Cortex.Contained.Channels.Discord.DiscordChannel discord
                    && discord.BotUsername is not null)
                {
                    return Results.Ok(new
                    {
                        username = discord.BotUsername,
                        userId = discord.BotUserId?.ToString(CultureInfo.InvariantCulture),
                        applicationId = discord.ApplicationId?.ToString(CultureInfo.InvariantCulture),
                        searchHint = discord.BotUsername, // what users should search for in Discord
                    });
                }
            }

            return Results.Ok(new { username = (string?)null, userId = (string?)null, applicationId = (string?)null, searchHint = (string?)null });
        }).RequireAuthorization();

        // Update a channel's enabled state and settings. Persists to cortex.yml.
        // Since channels are registered at DI startup, a restart is required for changes to take effect.
        app.MapPost("/api/channels/{channelType}", (
            string channelType,
            ChannelUpdateRequest request,
            BridgeConfig config,
            SecretManager secretManager,
            IHostEnvironment env) =>
        {
            channelType = channelType.ToLowerInvariant();

            if (channelType == "webchat")
            {
                return Results.Json(new { error = "Web Chat is a default channel and cannot be modified." }, statusCode: 400);
            }

            if (channelType is not "voice" and not "discord")
            {
                return Results.Json(new { error = $"Unknown channel type: {channelType}" }, statusCode: 400);
            }

            // Update or create the channel config entry
            if (!config.Channels.TryGetValue(channelType, out var channelConfig))
            {
                channelConfig = new ChannelConfig();
                config.Channels[channelType] = channelConfig;
            }

            channelConfig.Enabled = request.Enabled;

            if (request.Settings is not null)
            {
                foreach (var (key, value) in request.Settings)
                {
                    // Intercept secrets: store in DPAPI instead of config/YAML.
                    // Skip masked values ("********") and empty values — only update if a real new token is provided.
                    if (channelType == "discord" && string.Equals(key, "BotToken", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(value) && !value.Contains('*'))
                        {
                            secretManager.SetDiscordBotToken(value);
                        }

                        // Never store in channel settings — it lives in DPAPI only
                        channelConfig.Settings.Remove(key);
                        continue;
                    }

                    channelConfig.Settings[key] = value;
                }
            }

            // Persist to YAML
            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);

            return Results.Ok(new { success = true, restartRequired = true });
        }).RequireAuthorization();

        // Check prerequisites for a channel
        app.MapGet("/api/channels/{channelType}/prerequisites", async (string channelType) =>
        {
            channelType = channelType.ToLowerInvariant();

            if (channelType == "voice")
            {
                var checks = new List<object>();

                // Windows only for voice channel (audio capture/playback)
                var isWindows = OperatingSystem.IsWindows();
                checks.Add(new { name = "Windows Platform", ok = isWindows, detail = isWindows ? "OK" : "Voice channel requires Windows" });

                return Results.Ok(new { ready = isWindows, checks });
            }

            return Results.Json(new { error = $"Unknown channel type: {channelType}" }, statusCode: 400);
        }).RequireAuthorization();
    }
}
