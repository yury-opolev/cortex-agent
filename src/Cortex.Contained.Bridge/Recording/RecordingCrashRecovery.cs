using System.Globalization;
using Cortex.Contained.Contracts.Recording;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Recording;

/// <summary>
/// On Bridge startup, finds any recording-session whose <c>manifest.json</c>
/// has no <c>endUtc</c> — i.e. the previous Bridge process died mid-recording.
/// Finalises the WAV header from actual file length, appends
/// <c>auto_stop {reason: "crash"}</c> to <c>events.jsonl</c>, and patches the
/// manifest. Safe to run on a fully-clean tree (no-op).
/// </summary>
public static partial class RecordingCrashRecovery
{
    public static int SweepAndFinalise(string rootDir, ILogger logger)
    {
        if (!Directory.Exists(rootDir))
        {
            return 0;
        }

        var fixedCount = 0;
        // Two-level layout: <root>/<channelFolder>/<sessionId>/{session.wav,events.jsonl,manifest.json}
        foreach (var sessionDir in Directory.EnumerateDirectories(rootDir, "*", SearchOption.AllDirectories))
        {
            var manifestPath = Path.Combine(sessionDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var dir = sessionDir;

            RecordingManifest manifest;
            try
            {
                manifest = RecordingManifest.FromJson(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                LogUnreadableManifest(logger, manifestPath, ex.Message);
                continue;
            }

            if (manifest.EndUtc is not null)
            {
                continue;
            }

            var wavPath = Path.Combine(dir, "session.wav");
            if (File.Exists(wavPath))
            {
                try
                {
                    WavFileWriter.FinaliseFromFile(wavPath);
                }
                catch (Exception ex)
                {
                    LogWavRepairFailed(logger, manifest.Id, ex.Message);
                }
            }

            var endUtc = DateTimeOffset.UtcNow;
            var elapsed = (long)(endUtc - manifest.StartUtc).TotalMilliseconds;

            var eventsPath = Path.Combine(dir, "events.jsonl");
            if (File.Exists(eventsPath))
            {
                try
                {
                    var line = RecordingEvent.AutoStop(
                        elapsed,
                        endUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                        StopReason.Crash).ToJsonLine();
                    File.AppendAllText(eventsPath, line + "\n");
                }
                catch (Exception ex)
                {
                    LogEventsAppendFailed(logger, manifest.Id, ex.Message);
                }
            }

            var fixedManifest = manifest with
            {
                EndUtc = endUtc,
                DurationMs = elapsed,
                Crashed = true,
                StopReason = "crash",
            };
            File.WriteAllText(manifestPath, fixedManifest.ToJson());
            fixedCount++;
            LogFinalised(logger, manifest.Id);
        }

        return fixedCount;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "recording crash-recovery: unreadable manifest at {Path}: {Error}")]
    private static partial void LogUnreadableManifest(ILogger logger, string path, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "recording crash-recovery: WAV header repair failed for {Id}: {Error}")]
    private static partial void LogWavRepairFailed(ILogger logger, string id, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "recording crash-recovery: events.jsonl append failed for {Id}: {Error}")]
    private static partial void LogEventsAppendFailed(ILogger logger, string id, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "recording crash-recovery: finalised {Id}")]
    private static partial void LogFinalised(ILogger logger, string id);
}
