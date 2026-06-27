using Cortex.Contained.Contracts.Recording;

namespace Cortex.Contained.Bridge.Recording;

/// <summary>
/// Active recording session: bundles writers + timing + a per-session lock.
/// Internal to the controller; the public <see cref="ActiveSession"/> DTO is
/// what consumers see across the Contracts seam.
/// </summary>
internal sealed class RecordingSession : IDisposable
{
    public string Id { get; }

    public string ChannelKey { get; }

    public string Label { get; }

    /// <summary>Folder name for the invoking tenant under recording-sessions (e.g. "default"). Stable per session.</summary>
    public string TenantFolder { get; }

    /// <summary>Original tenant id (unsanitised) as resolved from the slash-command invocation.</summary>
    public string? TenantId { get; }

    /// <summary>Folder name for this channel under the tenant folder (e.g. "General", "host"). Stable per session.</summary>
    public string ChannelFolder { get; }

    /// <summary>Human-readable channel name shown in the manifest (e.g. the Discord voice-channel name).</summary>
    public string? ChannelDisplay { get; }

    public DateTimeOffset StartUtc { get; }

    public long CapMs { get; }

    public WavFileWriter Wav { get; }

    public EventsJsonlWriter Events { get; }

    public object WriteLock { get; } = new();

    public bool CapWarned { get; set; }

    public bool AudioStartEmitted { get; set; }

    /// <summary>WAV data bytes written so far (not counting the 44-byte header).</summary>
    public long WavBytesWritten { get; set; }

    /// <summary>Wall-time of the previous <c>RecordCommittedUtterance</c> append, or null before the first one.</summary>
    public DateTimeOffset? LastUtteranceUtc { get; set; }

    private bool disposed;

    public RecordingSession(string id, string channelKey, string label,
        string tenantFolder, string? tenantId,
        string channelFolder, string? channelDisplay,
        DateTimeOffset startUtc, long capMs, WavFileWriter wav, EventsJsonlWriter events)
    {
        this.Id = id;
        this.ChannelKey = channelKey;
        this.Label = label;
        this.TenantFolder = tenantFolder;
        this.TenantId = tenantId;
        this.ChannelFolder = channelFolder;
        this.ChannelDisplay = channelDisplay;
        this.StartUtc = startUtc;
        this.CapMs = capMs;
        this.Wav = wav;
        this.Events = events;
    }

    public long ElapsedMs(DateTimeOffset now) =>
        (long)(now - this.StartUtc).TotalMilliseconds;

    public ActiveSession ToActiveSession() =>
        new(this.Id, this.ChannelKey, this.Label, this.StartUtc, this.CapMs);

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        try
        {
            this.Wav.Dispose();
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            this.Events.Dispose();
        }
        catch
        {
            // Best-effort.
        }
    }
}
