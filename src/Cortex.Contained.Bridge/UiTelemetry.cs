using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge;

/// <summary>
/// Source-generated <see cref="LoggerMessage"/> delegates for the settings /
/// telemetry surface. Kept centralized so <c>Program.cs</c> (top-level
/// statements, can't host generated partials itself) can still participate
/// in high-performance logging.
/// </summary>
internal static partial class BridgeLogMessages
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed models for provider '{Provider}': before={BeforeCount} after={AfterCount} added=[{Added}] removed=[{Removed}]")]
    public static partial void LogModelsRefreshed(ILogger logger, string provider, int beforeCount, int afterCount, string added, string removed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Persisted models for provider '{Provider}': before={BeforeCount} after={AfterCount} added=[{Added}] removed=[{Removed}]")]
    public static partial void LogModelsPersisted(ILogger logger, string provider, int beforeCount, int afterCount, string added, string removed);

    [LoggerMessage(Level = LogLevel.Information, Message = "[UI] {Source} {Event} {Properties}")]
    public static partial void LogUiTelemetryInfo(ILogger logger, string source, string @event, string properties);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[UI] {Source} {Event} {Properties}")]
    public static partial void LogUiTelemetryWarn(ILogger logger, string source, string @event, string properties);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart requested via Web UI; scheduling graceful shutdown")]
    public static partial void LogRestartRequested(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to refresh live Copilot endpoint metadata for provider '{Provider}': {Error}")]
    public static partial void LogEndpointOverlayRefreshFailed(ILogger logger, string provider, string error);
}

/// <summary>
/// Event payload sent from the web UI to <c>/api/telemetry/ui</c>. The
/// shape is deliberately loose — the UI decides what properties are useful
/// for a given event, and they land in the bridge log as structured JSON
/// so we can correlate UI state with server-side activity without having
/// to ask the user to open browser devtools.
/// </summary>
public sealed class UiTelemetryEvent
{
    /// <summary>Source script / component that emitted the event (e.g. "global-settings.js").</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>Event name in "area.action" style (e.g. "refreshModels.success").</summary>
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    /// <summary>Arbitrary structured properties.</summary>
    [JsonPropertyName("properties")]
    public JsonElement? Properties { get; set; }
}

/// <summary>
/// Pure helpers for the settings surface. Kept here for unit testability.
/// </summary>
internal static class SettingsDiff
{
    /// <summary>
    /// Compute the symmetric difference between two model-ID lists.
    /// Order-insensitive, duplicate-insensitive (sets). Returned lists are
    /// sorted for stable log output.
    /// </summary>
    public static (List<string> Added, List<string> Removed) ModelDiff(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after)
    {
        var beforeSet = new HashSet<string>(before, StringComparer.Ordinal);
        var afterSet = new HashSet<string>(after, StringComparer.Ordinal);
        var added = afterSet.Except(beforeSet).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var removed = beforeSet.Except(afterSet).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return (added, removed);
    }
}
