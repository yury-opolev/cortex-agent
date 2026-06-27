using Cortex.Contained.Channels.Voice;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the voice-state endpoints (<c>/api/voice/*</c>) used by the web UI overlay to read
/// and control the local voice channel. All require authorization.
/// </summary>
internal static class VoiceStateEndpoints
{
    /// <summary>Maps the <c>/api/voice/*</c> endpoints onto <paramref name="app"/>.</summary>
    public static void MapVoiceStateEndpoints(this WebApplication app)
    {
        // --- Voice State API (for web UI overlay) ---

        app.MapGet("/api/voice/state", (IServiceProvider sp) =>
        {
            var voiceChannel = sp.GetService<VoiceChannel>();
            if (voiceChannel is null)
            {
                return Results.Ok(new { enabled = false, state = "disabled" });
            }

            return Results.Ok(new
            {
                enabled = voiceChannel.Status == Cortex.Contained.Contracts.Channels.ChannelStatus.Connected,
                state = voiceChannel.CurrentVoiceStateName,
            });
        }).RequireAuthorization();

        app.MapPost("/api/voice/start-listening", (IServiceProvider sp) =>
        {
            var voiceChannel = sp.GetService<VoiceChannel>();
            if (voiceChannel is null)
            {
                return Results.Json(new { success = false, error = "Voice channel not available" }, statusCode: 404);
            }

            voiceChannel.StartListening();
            return Results.Ok(new { success = true, state = voiceChannel.CurrentVoiceStateName });
        }).RequireAuthorization();

        app.MapPost("/api/voice/stop-listening", async (IServiceProvider sp) =>
        {
            var voiceChannel = sp.GetService<VoiceChannel>();
            if (voiceChannel is null)
            {
                return Results.Json(new { success = false, error = "Voice channel not available" }, statusCode: 404);
            }

            await voiceChannel.StopListeningAsync().ConfigureAwait(false);
            return Results.Ok(new { success = true, state = voiceChannel.CurrentVoiceStateName });
        }).RequireAuthorization();
    }
}
