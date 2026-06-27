using System.IO;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Cortex.Contained.Channels.Voice;

/// <summary>
/// Plays audio through a Windows output device using NAudio <see cref="WaveOutEvent"/>.
/// Accepts mono 16-bit PCM data at any sample rate.
/// </summary>
public sealed partial class WindowsAudioPlayback : IAudioPlayback
{
    private readonly ILogger<WindowsAudioPlayback> logger;
    private readonly int deviceIndex;
    private volatile bool isPlaying;
    private volatile bool isPaused;
    private CancellationTokenSource? playbackCts;
    private WaveOutEvent? activeWaveOut;
    private bool disposed;

    public WindowsAudioPlayback(ILogger<WindowsAudioPlayback> logger, int deviceIndex = -1)
    {
        this.logger = logger;
        this.deviceIndex = deviceIndex;
    }

    /// <inheritdoc />
    public bool IsPlaying => this.isPlaying;

    /// <inheritdoc />
    public bool IsPaused => this.isPaused;

    /// <inheritdoc />
    public async Task PlayAsync(byte[] pcmData, int sampleRate, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentNullException.ThrowIfNull(pcmData);

        if (pcmData.Length == 0)
        {
            return;
        }

        StopPlayback();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.playbackCts = cts;

        try
        {
            this.isPlaying = true;
            this.isPaused = false;
            await PlayInternalAsync(pcmData, sampleRate, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            this.isPlaying = false;
            this.isPaused = false;
            this.activeWaveOut = null;
            this.playbackCts = null;
            cts.Dispose();
        }
    }

    /// <inheritdoc />
    public void StopPlayback()
    {
        this.isPaused = false;
        var cts = this.playbackCts;
        if (cts is not null)
        {
#pragma warning disable CA1849 // Call async methods when in an async method — Stop() is synchronous by contract
            cts.Cancel();
#pragma warning restore CA1849
        }
    }

    /// <inheritdoc />
    public void PausePlayback()
    {
        if (!this.isPlaying || this.isPaused)
        {
            return;
        }

        var waveOut = this.activeWaveOut;
        if (waveOut is not null)
        {
            waveOut.Pause();
            this.isPaused = true;
        }
    }

    /// <inheritdoc />
    public void ResumePlayback()
    {
        if (!this.isPaused)
        {
            return;
        }

        var waveOut = this.activeWaveOut;
        if (waveOut is not null)
        {
            waveOut.Play();
            this.isPaused = false;
        }
    }

    /// <inheritdoc />
    public static IReadOnlyList<AudioDeviceInfo> GetAvailableDevices()
    {
        var count = WaveOut.DeviceCount;
        var devices = new List<AudioDeviceInfo>(count);

        for (var i = 0; i < count; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            devices.Add(new AudioDeviceInfo(i, caps.ProductName));
        }

        return devices;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        StopPlayback();
        GC.SuppressFinalize(this);
    }

    private Task PlayInternalAsync(byte[] pcmData, int sampleRate, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var waveFormat = new WaveFormat(sampleRate, AudioHelpers.BytesPerSample * 8, AudioHelpers.Channels);
        var waveProvider = new RawSourceWaveStream(new MemoryStream(pcmData), waveFormat);
        var waveOut = new WaveOutEvent { DeviceNumber = this.deviceIndex };
        this.activeWaveOut = waveOut;

        waveOut.PlaybackStopped += (_, args) =>
        {
            waveOut.Dispose();
            waveProvider.Dispose();

            if (args.Exception is not null)
            {
                this.LogPlaybackError(args.Exception.Message);
                tcs.TrySetException(args.Exception);
            }
            else
            {
                tcs.TrySetResult();
            }
        };

        var registration = cancellationToken.Register(() =>
        {
            waveOut.Stop();
            tcs.TrySetCanceled(cancellationToken);
        });

        // Ensure registration is disposed when playback completes
        tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        waveOut.Init(waveProvider);
        waveOut.Play();

        this.LogPlaybackStarted(pcmData.Length);
        return tcs.Task;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Audio playback started, {ByteCount} bytes")]
    private partial void LogPlaybackStarted(int byteCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio playback error: {ErrorMessage}")]
    private partial void LogPlaybackError(string errorMessage);
}
