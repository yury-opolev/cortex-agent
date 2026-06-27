using System.Collections.Concurrent;
using System.Globalization;
using Cortex.Contained.Contracts.Recording;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Recording;

/// <summary>
/// In-memory per-channel recording state. The single tap point —
/// <see cref="RecordCommittedUtterance"/> — is called once per committed
/// utterance by each audio source. The controller pre-pads inter-utterance
/// silence so the continuous <c>session.wav</c> stays aligned with the
/// session timeline, then writes the PCM and emits a <c>commit</c> event
/// whose <c>audioOffsetMs.{start,end}</c> reflect the WAV.
///
/// Lifecycle: <see cref="IHostedService.StartAsync"/> runs the crash-recovery
/// sweep and starts the cap timer; <see cref="IHostedService.StopAsync"/>
/// finalises every active session with <see cref="StopReason.Shutdown"/>.
/// </summary>
public sealed partial class RecordingController : IRecordingController, IHostedService, IAsyncDisposable
{
    public const int Pcm16kSampleRate = 16000;
    public const int Pcm16kBytesPerMs = Pcm16kSampleRate * 2 / 1000; // 32 bytes / ms
    public const long DefaultCapMs = 60 * 60 * 1000; // 60 minutes
    public const double CapWarningRatio = 0.9;

    private readonly ILogger<RecordingController> logger;
    private readonly TimeProvider clock;
    private readonly Func<string> rootDir;
    private readonly long capMs;

    private readonly ConcurrentDictionary<string, RecordingSession> sessions = new();
    private ITimer? capTimer;
    private bool shuttingDown;

    public RecordingController(
        ILogger<RecordingController> logger,
        TimeProvider clock,
        Func<string>? rootDir = null,
        long capMs = DefaultCapMs)
    {
        this.logger = logger;
        this.clock = clock;
        this.rootDir = rootDir ?? (() => RecordingPaths.RootDir);
        this.capMs = capMs;
    }

    public IReadOnlyCollection<ActiveSession> AllActive =>
        this.sessions.Values.Select(s => s.ToActiveSession()).ToArray();

    public ActiveSession? GetActive(string channelKey) =>
        this.sessions.TryGetValue(channelKey, out var s) ? s.ToActiveSession() : null;

    public async Task<StartResult> StartAsync(
        string channelKey,
        string? label,
        CancellationToken ct,
        string? channelDisplay = null,
        string? tenantId = null)
    {
        if (!ChannelKey.IsValid(channelKey))
        {
            return new StartResult.Rejected("invalid channel key");
        }

        if (this.shuttingDown)
        {
            return new StartResult.Rejected("controller is shutting down");
        }

        var now = this.clock.GetUtcNow();
        if (this.sessions.TryGetValue(channelKey, out var existing))
        {
            return new StartResult.AlreadyActive(existing.Id, existing.StartUtc);
        }

        var tenantFolder = RecordingPaths.TenantFolder(tenantId);
        var channelFolder = RecordingPaths.ChannelFolder(channelKey, channelDisplay);
        var parentDir = Path.Combine(this.rootDir(), tenantFolder, channelFolder);

        var baseId = SessionIdFactory.Create(label, now);
        var id = baseId;
        var sessionDir = Path.Combine(parentDir, id);
        for (var n = 2; Directory.Exists(sessionDir); n++)
        {
            id = $"{baseId}-{n}";
            sessionDir = Path.Combine(parentDir, id);
        }

        Directory.CreateDirectory(sessionDir);

        var wav = WavFileWriter.Create(Path.Combine(sessionDir, "session.wav"), Pcm16kSampleRate);
        var events = EventsJsonlWriter.Open(Path.Combine(sessionDir, "events.jsonl"));
        var sessionLabel = SessionIdFactory.Sanitise(label);
        var session = new RecordingSession(
            id, channelKey, sessionLabel,
            tenantFolder, tenantId,
            channelFolder, channelDisplay,
            now, this.capMs, wav, events);

        if (!this.sessions.TryAdd(channelKey, session))
        {
            // TryAdd race-loser: tear down everything we just created so we
            // don't leave orphan artifacts on disk.
            session.Dispose();
            try
            {
                Directory.Delete(sessionDir, recursive: true);
            }
            catch
            {
                // Best-effort.
            }

            var raced = this.sessions[channelKey];
            return new StartResult.AlreadyActive(raced.Id, raced.StartUtc);
        }

        lock (session.WriteLock)
        {
            session.Events.WriteLine(RecordingEvent.SessionStart(
                0, FormatUtc(now), channelKey, sessionLabel, this.capMs).ToJsonLine());
        }

        await File.WriteAllTextAsync(
            Path.Combine(sessionDir, "manifest.json"),
            new RecordingManifest
            {
                Id = id,
                Label = sessionLabel,
                ChannelKey = channelKey,
                ChannelDisplay = channelDisplay,
                StartUtc = now,
                CapMs = this.capMs,
            }.ToJson(),
            ct).ConfigureAwait(false);

        this.LogSessionStarted(id, channelKey);
        return new StartResult.Started(id, channelKey, now, this.capMs);
    }

    public async Task<StopResult> StopAsync(string channelKey, StopReason reason, CancellationToken ct)
    {
        if (!this.sessions.TryRemove(channelKey, out var s))
        {
            return new StopResult.NotActive();
        }

        var now = this.clock.GetUtcNow();
        var elapsed = s.ElapsedMs(now);

        // Finalise the WAV header and emit the auto_stop event under the
        // session lock so a concurrent (but still-in-flight)
        // RecordCommittedUtterance sees the writers either both intact or
        // both disposed — never one open and the other gone.
        lock (s.WriteLock)
        {
            try
            {
                s.Events.WriteLine(RecordingEvent.AutoStop(elapsed, FormatUtc(now), reason).ToJsonLine());
            }
            catch
            {
                // Already-disposed: nothing more we can do.
            }

            s.Wav.Finalise();
            s.Dispose();
        }

        var sessionDir = Path.Combine(this.rootDir(), s.TenantFolder, s.ChannelFolder, s.Id);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDir, "manifest.json"),
            new RecordingManifest
            {
                Id = s.Id,
                Label = s.Label,
                ChannelKey = channelKey,
                ChannelDisplay = s.ChannelDisplay,
                StartUtc = s.StartUtc,
                EndUtc = now,
                DurationMs = elapsed,
                CapMs = s.CapMs,
                CapReached = reason == StopReason.Cap,
                Crashed = false,
                StopReason = reason.ToString().ToLowerInvariant(),
            }.ToJson(),
            ct).ConfigureAwait(false);

        this.LogSessionStopped(s.Id, channelKey, reason);
        return new StopResult.Stopped(s.Id, Path.Combine(sessionDir, "session.wav"), elapsed, reason);
    }

    public void RecordCommittedUtterance(
        string channelKey,
        ReadOnlySpan<byte> pcm16k,
        string utteranceId,
        string text,
        string reason)
    {
        if (!this.sessions.TryGetValue(channelKey, out var s))
        {
            return;
        }

        var now = this.clock.GetUtcNow();
        var sessionElapsedMs = s.ElapsedMs(now);

        // Pre-pad inter-utterance silence so wav-duration ≈ session-elapsed.
        // Captures the silence between commits (used by the EOU eval harness
        // to label false-splits like the d9533b3f+81a2721d case where the
        // detector wrongly committed during a trailing-off pause).
        var wavDurationMs = s.WavBytesWritten / Pcm16kBytesPerMs;
        var silencePadMs = Math.Max(0, sessionElapsedMs - wavDurationMs);

        lock (s.WriteLock)
        {
            try
            {
                if (silencePadMs > 0)
                {
                    var silenceBytes = checked((int)(silencePadMs * Pcm16kBytesPerMs));
                    if (silenceBytes % 2 != 0)
                    {
                        silenceBytes--;
                    }

                    if (silenceBytes > 0)
                    {
                        s.Wav.Append(new byte[silenceBytes]);
                        s.WavBytesWritten += silenceBytes;
                    }
                }

                if (!s.AudioStartEmitted)
                {
                    s.AudioStartEmitted = true;
                    s.Events.WriteLine(RecordingEvent.AudioStart(sessionElapsedMs, FormatUtc(now)).ToJsonLine());
                }

                var audioStartOffsetMs = s.WavBytesWritten / Pcm16kBytesPerMs;
                s.Wav.Append(pcm16k);
                s.WavBytesWritten += pcm16k.Length;
                var audioEndOffsetMs = s.WavBytesWritten / Pcm16kBytesPerMs;
                s.LastUtteranceUtc = now;

                var commit = RecordingEvent.Commit(
                    t: sessionElapsedMs,
                    wallUtc: FormatUtc(now),
                    silenceMs: null, // V1: not plumbed; readers see absence as "no data".
                    pEou: null,
                    reason: reason,
                    utteranceId: utteranceId,
                    text: text,
                    audioStartMs: audioStartOffsetMs,
                    audioEndMs: audioEndOffsetMs);
                s.Events.WriteLine(commit.ToJsonLine());
            }
            catch (ObjectDisposedException)
            {
                // Concurrent Stop disposed the writers between our TryGetValue
                // and the lock acquisition — nothing more to do.
            }
        }
    }

    private static string FormatUtc(DateTimeOffset t) =>
        t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    // ── IHostedService ──────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            RecordingCrashRecovery.SweepAndFinalise(this.rootDir(), this.logger);
        }
        catch (Exception ex)
        {
            this.LogCrashRecoveryFailed(ex.Message);
        }

        // Tick every 250 ms. cap_warning fires at 90% of cap; auto_stop fires at 100%.
        this.capTimer = this.clock.CreateTimer(
            this.OnCapTick,
            state: null,
            dueTime: TimeSpan.FromMilliseconds(250),
            period: TimeSpan.FromMilliseconds(250));
        return Task.CompletedTask;
    }

    private void OnCapTick(object? state)
    {
        var now = this.clock.GetUtcNow();
        foreach (var s in this.sessions.Values.ToArray())
        {
            try
            {
                this.HandleSessionTick(s, now);
            }
            catch (Exception ex)
            {
                this.LogCapTickFailed(s.Id, ex.Message);
            }
        }
    }

    private void HandleSessionTick(RecordingSession s, DateTimeOffset now)
    {
        var elapsed = s.ElapsedMs(now);

        if (!s.CapWarned && elapsed >= (long)(s.CapMs * CapWarningRatio))
        {
            // Re-check membership — a user-Stop in flight may already have
            // disposed the session's writers.
            if (!this.sessions.ContainsKey(s.ChannelKey))
            {
                return;
            }

            s.CapWarned = true;
            lock (s.WriteLock)
            {
                try
                {
                    s.Events.WriteLine(
                        RecordingEvent.CapWarning(elapsed, FormatUtc(now), elapsed, s.CapMs).ToJsonLine());
                }
                catch (ObjectDisposedException)
                {
                    // Raced with Stop — nothing more to do.
                }
            }

            this.LogCapWarning(s.Id, elapsed, s.CapMs);
        }

        if (elapsed >= s.CapMs && this.sessions.ContainsKey(s.ChannelKey))
        {
            try
            {
                this.StopAsync(s.ChannelKey, StopReason.Cap, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                this.LogCapStopFailed(s.ChannelKey, ex.Message);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        this.shuttingDown = true;
        foreach (var key in this.sessions.Keys.ToArray())
        {
            try
            {
                await this.StopAsync(key, StopReason.Shutdown, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogShutdownStopFailed(key, ex.Message);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        this.capTimer?.Dispose();
        foreach (var s in this.sessions.Values)
        {
            try
            {
                s.Dispose();
            }
            catch
            {
                // Best-effort.
            }
        }

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "recording: session {Id} started on {ChannelKey}")]
    private partial void LogSessionStarted(string id, string channelKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "recording: session {Id} stopped on {ChannelKey} ({Reason})")]
    private partial void LogSessionStopped(string id, string channelKey, StopReason reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "recording: shutdown stop failed for {ChannelKey}: {Error}")]
    private partial void LogShutdownStopFailed(string channelKey, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "recording: cap_warning session {Id} elapsed={ElapsedMs}ms cap={CapMs}ms")]
    private partial void LogCapWarning(string id, long elapsedMs, long capMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "recording: cap auto-stop failed for {ChannelKey}: {Error}")]
    private partial void LogCapStopFailed(string channelKey, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "recording: cap-tick exception for session {Id}: {Error}")]
    private partial void LogCapTickFailed(string id, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "recording: startup crash-recovery sweep failed: {Error}")]
    private partial void LogCrashRecoveryFailed(string error);
}
