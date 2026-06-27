using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Cortex.Contained.Channels.Voice;

/// <summary>
/// Captures audio from a Windows input device using NAudio <see cref="WaveInEvent"/>.
/// Produces 16kHz mono 16-bit PCM buffers suitable for Whisper.
/// </summary>
public sealed partial class WindowsAudioCapture : IAudioCapture
{
    private readonly ILogger<WindowsAudioCapture> logger;
    private readonly int deviceIndex;
    private WaveInEvent? waveIn;
    private bool disposed;

    public WindowsAudioCapture(ILogger<WindowsAudioCapture> logger, int deviceIndex = -1)
    {
        this.logger = logger;
        this.deviceIndex = deviceIndex;
    }

    /// <inheritdoc />
    public bool IsCapturing => this.waveIn is not null;

    /// <inheritdoc />
    public event EventHandler<AudioBufferEventArgs>? AudioBufferReady;

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        if (this.waveIn is not null)
        {
            return;
        }

        var waveIn = new WaveInEvent
        {
            DeviceNumber = this.deviceIndex,
            WaveFormat = new WaveFormat(AudioHelpers.SampleRate, AudioHelpers.BytesPerSample * 8, AudioHelpers.Channels),
            BufferMilliseconds = 100,
        };

        waveIn.DataAvailable += OnDataAvailable;

        this.waveIn = waveIn;
        waveIn.StartRecording();

        this.LogCaptureStarted(this.deviceIndex);
    }

    /// <inheritdoc />
    public void StopCapture()
    {
        if (this.waveIn is null)
        {
            return;
        }

        this.waveIn.StopRecording();
        this.waveIn.DataAvailable -= OnDataAvailable;
        this.waveIn.Dispose();
        this.waveIn = null;

        this.LogCaptureStopped(this.deviceIndex);
    }

    /// <inheritdoc />
    public static IReadOnlyList<AudioDeviceInfo> GetAvailableDevices()
    {
        var count = WaveInEvent.DeviceCount;
        var devices = new List<AudioDeviceInfo>(count);

        for (var i = 0; i < count; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
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
        StopCapture();
        GC.SuppressFinalize(this);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        var buffer = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);

        AudioBufferReady?.Invoke(this, new AudioBufferEventArgs
        {
            Buffer = buffer,
            BytesRecorded = e.BytesRecorded,
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audio capture started on device {DeviceIndex}")]
    private partial void LogCaptureStarted(int deviceIndex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Audio capture stopped on device {DeviceIndex}")]
    private partial void LogCaptureStopped(int deviceIndex);
}
