using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Channels.Voice;

/// <summary>
/// WPF overlay window that displays voice pipeline status as a small floating pill.
/// Shows a round action button (mic/pause/play icon) and 3 animated equalizer bars
/// driven by real-time audio levels. A separate stop button appears during Speaking/Paused.
/// </summary>
/// <remarks>
/// <para>
/// Visible only during active states (Listening, Processing, Speaking, Paused).
/// Hidden during Idle.
/// </para>
/// <para>
/// The window is always-on-top, frameless, transparent, and draggable.
/// Position is remembered within the session (resets on restart).
/// </para>
/// </remarks>
public sealed partial class VoiceOverlayWindow : Window, IVoiceOverlay
{
    private static readonly Duration FastDuration = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration FadeInDuration = new(TimeSpan.FromMilliseconds(250));
    private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(400));

    // Bar height constraints (in pixels, before scaling)
    private const double BarMinHeight = 4.0;
    private const double BarMaxHeight = 28.0;
    private const double BarCanvasHeight = 32.0;

    // RMS normalization — typical speech RMS is 0.01..0.15; we map to 0..1
    private const float RmsFloor = 0.005f;
    private const float RmsCeiling = 0.15f;

    private readonly ILogger<VoiceOverlayWindow> logger;
    private readonly Action? onButtonClick;
    private readonly Action? onStopClick;

    private VoiceState currentState = VoiceState.Idle;
    private bool isVisible;
    private DispatcherTimer? speakingAnimationTimer;
    private readonly Random speakingRandom = new();

    /// <summary>
    /// Creates the voice overlay window.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="onButtonClick">
    /// Callback invoked when the user clicks the main action button.
    /// During Speaking this pauses; during Paused this resumes;
    /// during Listening it stops listening; during Idle it starts listening.
    /// </param>
    /// <param name="onStopClick">
    /// Callback invoked when the user clicks the stop button (visible during Speaking/Paused).
    /// Cancels TTS playback entirely and returns to idle.
    /// </param>
    public VoiceOverlayWindow(ILogger<VoiceOverlayWindow> logger, Action? onButtonClick = null, Action? onStopClick = null)
    {
        this.logger = logger;
        this.onButtonClick = onButtonClick;
        this.onStopClick = onStopClick;

        InitializeComponent();
        PositionBottomRight();
    }

    // ── IVoiceOverlay implementation ─────────────────────────────

    /// <inheritdoc />
    public void OnVoiceStateChanged(VoiceStateChange e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnVoiceStateChanged(e));
            return;
        }

        this.currentState = e.NewState;
        ApplyVisualState(e.NewState);
    }

    /// <inheritdoc />
    public void OnAudioLevelChanged(float rmsLevel)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnAudioLevelChanged(rmsLevel), DispatcherPriority.Render);
            return;
        }

        if (this.currentState != VoiceState.Listening)
        {
            return;
        }

        var normalized = NormalizeRms(rmsLevel);
        UpdateBarHeights(normalized);
    }

    // ── Visual state management ──────────────────────────────────

    private void ApplyVisualState(VoiceState state)
    {
        switch (state)
        {
            case VoiceState.Listening:
                ShowOverlay();
                StopSpeakingAnimation();
                SetButtonIcon(ButtonIcon.Mic);
                SetButtonColor(FindBrush("ListeningGreenBrush"));
                SetButtonGlow(FindColor("ListeningGreen"), 0.4);
                SetBarColor(FindBrush("ListeningGreenBrush"));
                StopButton.Visibility = Visibility.Collapsed;
                ActionButton.IsEnabled = true;
                ActionButton.ToolTip = "Click to stop listening";
                break;

            case VoiceState.Processing:
                ShowOverlay();
                StopSpeakingAnimation();
                SetButtonIcon(ButtonIcon.Mic);
                SetButtonColor(FindBrush("ProcessingYellowBrush"));
                SetButtonGlow(FindColor("ProcessingYellow"), 0.3);
                SetBarColor(FindBrush("ProcessingYellowBrush"));
                AnimateBarsToMinimum();
                StopButton.Visibility = Visibility.Collapsed;
                ActionButton.IsEnabled = false;
                ActionButton.ToolTip = "Processing...";
                break;

            case VoiceState.Speaking:
                ShowOverlay();
                SetButtonIcon(ButtonIcon.Pause);
                SetButtonColor(FindBrush("SpeakingBlueBrush"));
                SetButtonGlow(FindColor("SpeakingBlue"), 0.3);
                SetBarColor(FindBrush("SpeakingBlueBrush"));
                StartSpeakingAnimation();
                StopButton.Visibility = Visibility.Visible;
                ActionButton.IsEnabled = true;
                ActionButton.ToolTip = "Click to pause";
                break;

            case VoiceState.Paused:
                ShowOverlay();
                StopSpeakingAnimation();
                SetButtonIcon(ButtonIcon.Play);
                SetButtonColor(FindBrush("PausedAmberBrush"));
                SetButtonGlow(FindColor("PausedAmber"), 0.3);
                SetBarColor(FindBrush("PausedAmberBrush"));
                // Freeze bars at a mid-level position
                AnimateBar(Bar1, BarMinHeight + 0.3 * (BarMaxHeight - BarMinHeight));
                AnimateBar(Bar2, BarMinHeight + 0.5 * (BarMaxHeight - BarMinHeight));
                AnimateBar(Bar3, BarMinHeight + 0.2 * (BarMaxHeight - BarMinHeight));
                StopButton.Visibility = Visibility.Visible;
                ActionButton.IsEnabled = true;
                ActionButton.ToolTip = "Click to resume";
                break;

            case VoiceState.Idle:
            default:
                StopSpeakingAnimation();
                SetButtonIcon(ButtonIcon.Mic);
                StopButton.Visibility = Visibility.Collapsed;
                HideOverlay();
                ActionButton.IsEnabled = true;
                ActionButton.ToolTip = "Click to start listening";
                break;
        }
    }

    // ── Button icon management ───────────────────────────────────

    private enum ButtonIcon
    {
        Mic,
        Pause,
        Play,
    }

    private void SetButtonIcon(ButtonIcon icon)
    {
        var mic = FindButtonElement<UIElement>("MicIcon");
        var pause = FindButtonElement<UIElement>("PauseIcon");
        var play = FindButtonElement<UIElement>("PlayIcon");

        if (mic is not null)
        {
            mic.Visibility = icon == ButtonIcon.Mic ? Visibility.Visible : Visibility.Collapsed;
        }

        if (pause is not null)
        {
            pause.Visibility = icon == ButtonIcon.Pause ? Visibility.Visible : Visibility.Collapsed;
        }

        if (play is not null)
        {
            play.Visibility = icon == ButtonIcon.Play ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ── Show / Hide with fade ────────────────────────────────────

    private void ShowOverlay()
    {
        if (this.isVisible)
        {
            return;
        }

        this.isVisible = true;
        Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0, 1, FadeInDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fadeIn);
    }

    private void HideOverlay()
    {
        if (!this.isVisible)
        {
            return;
        }

        this.isVisible = false;

        var fadeOut = new DoubleAnimation(Opacity, 0, FadeOutDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        fadeOut.Completed += (_, _) =>
        {
            if (!this.isVisible)
            {
                Visibility = Visibility.Hidden;
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    // ── Bar animation ────────────────────────────────────────────

    private void UpdateBarHeights(double normalized)
    {
        // Add slight variation per bar for visual interest
        var h1 = BarMinHeight + (normalized * 0.8 * (BarMaxHeight - BarMinHeight));
        var h2 = BarMinHeight + (normalized * 1.0 * (BarMaxHeight - BarMinHeight));
        var h3 = BarMinHeight + (normalized * 0.6 * (BarMaxHeight - BarMinHeight));

        AnimateBar(Bar1, h1);
        AnimateBar(Bar2, h2);
        AnimateBar(Bar3, h3);
    }

    private void AnimateBarsToMinimum()
    {
        AnimateBar(Bar1, BarMinHeight);
        AnimateBar(Bar2, BarMinHeight);
        AnimateBar(Bar3, BarMinHeight);
    }

    private static void AnimateBar(Rectangle bar, double targetHeight)
    {
        targetHeight = Math.Clamp(targetHeight, BarMinHeight, BarMaxHeight);

        // Position bar from the bottom of the canvas
        var targetTop = BarCanvasHeight - targetHeight;

        var heightAnim = new DoubleAnimation(bar.Height, targetHeight, FastDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        bar.BeginAnimation(HeightProperty, heightAnim);

        var currentTop = System.Windows.Controls.Canvas.GetTop(bar);
        var topAnim = new DoubleAnimation(
            double.IsNaN(currentTop) ? targetTop : currentTop,
            targetTop,
            FastDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        bar.BeginAnimation(System.Windows.Controls.Canvas.TopProperty, topAnim);
    }

    // ── Speaking animation (synthetic equalizer) ─────────────────

    private void StartSpeakingAnimation()
    {
        if (this.speakingAnimationTimer is not null)
        {
            return;
        }

        this.speakingAnimationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        this.speakingAnimationTimer.Tick += OnSpeakingAnimationTick;
        this.speakingAnimationTimer.Start();
    }

    private void StopSpeakingAnimation()
    {
        if (this.speakingAnimationTimer is null)
        {
            return;
        }

        this.speakingAnimationTimer.Stop();
        this.speakingAnimationTimer.Tick -= OnSpeakingAnimationTick;
        this.speakingAnimationTimer = null;
    }

    private void OnSpeakingAnimationTick(object? sender, EventArgs e)
    {
        if (this.currentState != VoiceState.Speaking)
        {
            StopSpeakingAnimation();
            return;
        }

        // Generate random bar heights to simulate speaking activity
        var h1 = BarMinHeight + (this.speakingRandom.NextDouble() * 0.7 * (BarMaxHeight - BarMinHeight));
        var h2 = BarMinHeight + (this.speakingRandom.NextDouble() * 0.9 * (BarMaxHeight - BarMinHeight));
        var h3 = BarMinHeight + (this.speakingRandom.NextDouble() * 0.5 * (BarMaxHeight - BarMinHeight));

        AnimateBar(Bar1, h1);
        AnimateBar(Bar2, h2);
        AnimateBar(Bar3, h3);
    }

    // ── Button visual updates ────────────────────────────────────

    private void SetButtonColor(Brush brush)
    {
        var circle = FindButtonElement<Ellipse>("ButtonCircle");
        circle?.SetValue(Shape.FillProperty, brush);
    }

    private void SetButtonGlow(Color color, double opacity)
    {
        var glow = FindButtonElement<Ellipse>("ButtonGlow");
        if (glow is null)
        {
            return;
        }

        glow.Fill = new RadialGradientBrush(color, Colors.Transparent);
        var anim = new DoubleAnimation(glow.Opacity, opacity, FastDuration);
        glow.BeginAnimation(OpacityProperty, anim);
    }

    private void SetBarColor(Brush brush)
    {
        Bar1.Fill = brush;
        Bar2.Fill = brush;
        Bar3.Fill = brush;
    }

    // ── Event handlers ───────────────────────────────────────────

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            this.onButtonClick?.Invoke();
        }
#pragma warning disable CA1031 // Overlay must not crash
        catch (Exception ex)
        {
            this.LogButtonClickError(ex);
        }
#pragma warning restore CA1031
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            this.onStopClick?.Invoke();
        }
#pragma warning disable CA1031 // Overlay must not crash
        catch (Exception ex)
        {
            this.LogStopClickError(ex);
        }
#pragma warning restore CA1031
    }

    private void RootPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Allow dragging the overlay by its background (not the buttons)
        if (e.Source != ActionButton && e.Source != StopButton)
        {
            DragMove();
        }
    }

    private void RootPanel_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click to hide (can be re-shown by state change)
        HideOverlay();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private void PositionBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Bottom - Height - 20;
    }

    private static float NormalizeRms(float rms)
    {
        if (rms <= RmsFloor)
        {
            return 0f;
        }

        if (rms >= RmsCeiling)
        {
            return 1f;
        }

        return (rms - RmsFloor) / (RmsCeiling - RmsFloor);
    }

    private T? FindButtonElement<T>(string name) where T : UIElement
    {
        var template = ActionButton.Template;
        return template.FindName(name, ActionButton) as T;
    }

    private SolidColorBrush FindBrush(string key)
    {
        return (SolidColorBrush)FindResource(key);
    }

    private Color FindColor(string key)
    {
        return (Color)FindResource(key);
    }

    // ── IDisposable ──────────────────────────────────────────────

    void IDisposable.Dispose()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ((IDisposable)this).Dispose());
            return;
        }

        StopSpeakingAnimation();
        Close();
    }

    // ── LoggerMessage ────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice overlay button click error")]
    private partial void LogButtonClickError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice overlay stop click error")]
    private partial void LogStopClickError(Exception ex);
}
