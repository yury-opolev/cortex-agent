using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Channels.Voice;

/// <summary>
/// Creates and manages the <see cref="VoiceOverlayWindow"/> on a dedicated STA thread.
/// WPF requires an STA thread with a message pump; this factory handles all the threading
/// complexity so that the Bridge (which runs on an ASP.NET thread pool) does not need
/// WPF dependencies or STA thread awareness.
/// </summary>
public static class VoiceOverlayFactory
{
    /// <summary>
    /// Creates a <see cref="VoiceOverlayWindow"/> on a dedicated STA thread.
    /// Returns immediately with an <see cref="IVoiceOverlay"/> that marshals all calls
    /// to the WPF dispatcher.
    /// </summary>
    /// <param name="logger">Logger for the overlay window.</param>
    /// <param name="onButtonClick">Callback invoked when the main action button is clicked.</param>
    /// <param name="onStopClick">Callback invoked when the stop button is clicked (cancels TTS).</param>
    /// <returns>The overlay instance, or null if creation failed.</returns>
    public static IVoiceOverlay? Create(ILogger<VoiceOverlayWindow> logger, Action? onButtonClick, Action? onStopClick)
    {
        VoiceOverlayWindow? overlay = null;
        var ready = new ManualResetEventSlim(false);
        Exception? startupError = null;

        var thread = new Thread(() =>
        {
            try
            {
                // WPF requires a Dispatcher/Application on the STA thread.
                // If no Application exists yet, create a lightweight one.
                if (Application.Current is null)
                {
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                }

                overlay = new VoiceOverlayWindow(logger, onButtonClick, onStopClick);
                overlay.Show();
                ready.Set();

                // Run the WPF message pump on this thread
                Dispatcher.Run();
            }
#pragma warning disable CA1031 // Factory must not crash the host
            catch (Exception ex)
            {
                startupError = ex;
                ready.Set();
            }
#pragma warning restore CA1031
        })
        {
            Name = "VoiceOverlay",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Wait for the overlay to be created (or fail)
        ready.Wait(TimeSpan.FromSeconds(5));

        if (startupError is not null || overlay is null)
        {
#pragma warning disable CA1848 // Static class cannot use LoggerMessage source generator
            logger.LogWarning(startupError, "Failed to create voice overlay window");
#pragma warning restore CA1848
            return null;
        }

        return overlay;
    }
}
