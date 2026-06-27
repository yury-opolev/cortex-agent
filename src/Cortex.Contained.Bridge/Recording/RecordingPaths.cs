using Cortex.Contained.Contracts.Recording;

namespace Cortex.Contained.Bridge.Recording;

/// <summary>
/// Canonical filesystem paths for recording sessions. Three-level layout:
/// <c>&lt;root&gt;\&lt;tenant&gt;\&lt;channel&gt;\&lt;sessionId&gt;\</c> where
/// <list type="bullet">
///   <item><c>tenant</c> is the sanitised tenant id (the invoking user's pairing);</item>
///   <item><c>channel</c> is the sanitised channel display name (Discord
///         voice-channel name) or the literal <c>host</c> for the local
///         voice channel;</item>
///   <item><c>sessionId</c> is <c>&lt;label&gt;-yyyyMMdd-HHmmss</c>.</item>
/// </list>
/// Each session folder contains <c>session.wav</c>, <c>events.jsonl</c>,
/// <c>manifest.json</c>.
/// </summary>
public static class RecordingPaths
{
    public static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cortex",
        "recording-sessions");

    /// <summary>Sanitised tenant folder name. Falls back to <c>unpaired</c> when blank.</summary>
    public static string TenantFolder(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return "unpaired";
        }

        var safe = SessionIdFactory.Sanitise(tenantId);
        return string.Equals(safe, "session", StringComparison.Ordinal) ? "unpaired" : safe;
    }

    /// <summary>
    /// Folder name for a channel. Prefers the (sanitised) display name when
    /// provided; otherwise derives a stable name from the channel-key
    /// (<c>host</c> stays <c>host</c>, <c>discord:&lt;id&gt;</c> becomes
    /// <c>discord-&lt;id&gt;</c>).
    /// </summary>
    public static string ChannelFolder(string channelKey, string? channelDisplay)
    {
        if (!string.IsNullOrWhiteSpace(channelDisplay))
        {
            var safe = SessionIdFactory.Sanitise(channelDisplay);
            if (!string.Equals(safe, "session", StringComparison.Ordinal))
            {
                return safe;
            }
        }

        if (string.Equals(channelKey, ChannelKey.Host, StringComparison.Ordinal))
        {
            return ChannelKey.Host;
        }

        return channelKey.Replace(':', '-');
    }

    public static string SessionDir(string tenantFolder, string channelFolder, string id) =>
        Path.Combine(RootDir, tenantFolder, channelFolder, id);

    public static string Wav(string tenantFolder, string channelFolder, string id) =>
        Path.Combine(SessionDir(tenantFolder, channelFolder, id), "session.wav");

    public static string EventsJsonl(string tenantFolder, string channelFolder, string id) =>
        Path.Combine(SessionDir(tenantFolder, channelFolder, id), "events.jsonl");

    public static string Manifest(string tenantFolder, string channelFolder, string id) =>
        Path.Combine(SessionDir(tenantFolder, channelFolder, id), "manifest.json");
}
