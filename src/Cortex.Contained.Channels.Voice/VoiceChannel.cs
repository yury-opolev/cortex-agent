using System.Globalization;
using System.IO;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Messages;
using Cortex.Contained.Speech;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Channels.Voice;

/// <summary>
/// Voice channel implementing <see cref="IChannel"/>. Orchestrates the full audio pipeline:
/// <list type="number">
///   <item>Captures audio from microphone via <see cref="IAudioCapture"/></item>
///   <item>Starts capture via push-to-talk (hotkey/overlay) or open-mic</item>
///   <item>Accumulates speech buffers with VAD silence detection</item>
///   <item>Transcribes speech via <see cref="ISpeechToText"/></item>
///   <item>Fires <see cref="MessageReceived"/> with transcribed text</item>
///   <item>Receives outbound text via <see cref="SendMessageAsync"/></item>
///   <item>Synthesizes speech via <see cref="ITextToSpeech"/></item>
///   <item>Plays audio via <see cref="IAudioPlayback"/></item>
/// </list>
/// </summary>
public sealed partial class VoiceChannel : IChannel
{
    private readonly IAudioCapture capture;
    private readonly IAudioPlayback playback;
    private readonly ISpeechToText stt;
    private readonly ITextToSpeech tts;
    private readonly GlobalHotkeyListener? hotkeyListener;
    private readonly IVoiceOverlay? overlay;
    private readonly VoiceChannelOptions options;
    private readonly ILogger<VoiceChannel> logger;

    // State machine
    private ChannelStatus status = ChannelStatus.Disconnected;
    private VoiceState voiceState = VoiceState.Idle;
    private readonly Lock stateLock = new();

    // Audio accumulation
    private MemoryStream audioAccumulator = new();
    private DateTimeOffset lastSpeechTime = DateTimeOffset.MinValue;
    private DateTimeOffset lastAudioLevelReport = DateTimeOffset.MinValue;
    private readonly int silenceTimeoutBytes;

    // Conversation tracking — voice uses a single persistent conversation
    private readonly string conversationId;
    private int messageCounter;

    // Fire-and-forget playback: SendMessageAsync returns immediately and playback
    // runs on a background task. The gate serializes concurrent messages so the
    // audio device is only driven by one speaker at a time.
    private readonly SemaphoreSlim playbackGate = new(1, 1);
    private CancellationTokenSource? activePlaybackCts;

    /// <summary>
    /// The most-recent background speech synthesis + playback task created by
    /// <see cref="SendMessageAsync"/>. Exposed so callers (and tests) can await
    /// playback completion without blocking the send. Null until the first
    /// non-empty message is sent.
    /// </summary>
    public Task? ActivePlaybackTask { get; private set; }

    public VoiceChannel(
        IAudioCapture capture,
        IAudioPlayback playback,
        ISpeechToText stt,
        ITextToSpeech tts,
        GlobalHotkeyListener? hotkeyListener,
        IVoiceOverlay? overlay,
        VoiceChannelOptions options,
        ILogger<VoiceChannel> logger)
    {
        this.capture = capture;
        this.playback = playback;
        this.stt = stt;
        this.tts = tts;
        this.hotkeyListener = hotkeyListener;
        this.overlay = overlay;
        this.options = options;
        this.logger = logger;

        this.silenceTimeoutBytes = AudioHelpers.MillisecondsToBytes(options.SilenceTimeoutMs);
        this.conversationId = options.ChannelId;

        // Wire overlay to voice events
        if (this.overlay is not null)
        {
            VoiceStateChanged += this.overlay.OnVoiceStateChanged;
            AudioLevelChanged += this.overlay.OnAudioLevelChanged;
        }
    }

    // ── IChannel properties ──────────────────────────────────────────

    /// <inheritdoc />
    public string ChannelId => this.options.ChannelId;

    /// <summary>Tenant this host voice channel is bound to (from <see cref="VoiceChannelOptions.TenantId"/>). Used by the Bridge to scope <c>/voice-record</c> autocomplete to the tenant that owns this channel.</summary>
    public string TenantId => this.options.TenantId;

    /// <inheritdoc />
    public ChannelType Type => ChannelType.Voice;

    /// <inheritdoc />
    public ChannelStatus Status => this.status;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities { get; } = new()
    {
        SupportsStreaming = false,
        SupportsRichText = false,
        SupportsMedia = false,
        SupportsEditing = false,
        SupportsDeletion = false,
        MaxMessageLength = 10_000,
    };

    // ── IChannel events ──────────────────────────────────────────────

    /// <inheritdoc />
    public event Func<InboundMessage, Task>? MessageReceived;

    /// <inheritdoc />
    public event Func<ChannelStatusChange, Task>? StatusChanged;

    // ── Voice state ──────────────────────────────────────────────────

    /// <summary>Current voice pipeline state (for overlay and diagnostics).</summary>
    public VoiceState CurrentVoiceState => this.voiceState;

    /// <summary>
    /// Current voice state as a string, for the web UI overlay API.
    /// </summary>
    public string CurrentVoiceStateName => this.voiceState.ToString().ToLowerInvariant();

    /// <summary>
    /// Raised when the voice pipeline state changes (e.g. Idle -> Listening -> Speaking).
    /// Used by the desktop overlay to show/hide and update visual state.
    /// </summary>
    public event Action<VoiceStateChange>? VoiceStateChanged;

    /// <summary>
    /// Raised when a new audio level (RMS) is computed from the microphone input.
    /// Used by the desktop overlay to animate equalizer bars in real-time.
    /// Fires at approximately 10 Hz during active capture.
    /// </summary>
    public event Action<float>? AudioLevelChanged;

    // ── IChannel methods ─────────────────────────────────────────────

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (!this.stt.IsReady)
        {
            SetStatus(ChannelStatus.Error, "STT engine is not ready");
            throw new InvalidOperationException("Speech-to-text engine is not ready. Ensure the Whisper model is loaded.");
        }

        this.capture.AudioBufferReady += OnAudioBufferReady;
        this.capture.Start();

        // Wire up hotkey for push-to-talk (toggle mode)
        if (this.options.PushToTalk && this.hotkeyListener is not null)
        {
            this.hotkeyListener.HotkeyPressed += OnHotkeyToggle;
        }

        // Wake-word was removed: desktop voice starts in push-to-talk (Idle — wait
        // for the hotkey/overlay) or open-mic (Listening), per options.PushToTalk.
        var initialState = this.options.PushToTalk ? VoiceState.Idle : VoiceState.Listening;

        SetVoiceState(initialState);
        SetStatus(ChannelStatus.Connected, "Voice channel connected");
        this.LogConnected(ChannelId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            this.activePlaybackCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        this.capture.AudioBufferReady -= OnAudioBufferReady;
        this.capture.StopCapture();
        this.playback.StopPlayback();

        // Unhook hotkey
        if (this.hotkeyListener is not null)
        {
            this.hotkeyListener.HotkeyPressed -= OnHotkeyToggle;
        }

        SetVoiceState(VoiceState.Idle);
        SetStatus(ChannelStatus.Disconnected, "Voice channel disconnected");
        this.LogDisconnected(ChannelId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Fire-and-forget: returns as soon as the message is queued for synthesis.
    /// Actual TTS + playback runs on a background task so the caller is not
    /// blocked until audio finishes. Concurrent calls are serialized by
    /// <see cref="playbackGate"/> so audio plays in order.
    /// </remarks>
    public Task<SendResult> SendMessageAsync(OutboundMessage message, CancellationToken ct = default)
    {
        var text = message.Content.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(SendResult.Ok(message.MessageId));
        }

        this.LogSpeaking(ChannelId, text.Length);

        this.ActivePlaybackTask = Task.Run(() => SpeakAsync(text, ct), CancellationToken.None);

        return Task.FromResult(SendResult.Ok(message.MessageId));
    }

    private async Task SpeakAsync(string text, CancellationToken callerCt)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        var gateAcquired = false;

        try
        {
            await this.playbackGate.WaitAsync(cts.Token).ConfigureAwait(false);
            gateAcquired = true;
            this.activePlaybackCts = cts;

            // Pause capture while speaking to avoid echo
            this.capture.StopCapture();
            SetVoiceState(VoiceState.Speaking);

            var sampleRate = this.tts.OutputFormat.SampleRate;

            // Use streaming synthesis: play each segment as it's synthesized
            // rather than waiting for the entire text to finish.
            // The default interface implementation falls back to single-chunk
            // synthesis for engines that don't support streaming (e.g. WindowsTextToSpeech).
            await foreach (var chunk in this.tts.SynthesizeStreamingAsync(text, cancellationToken: cts.Token).ConfigureAwait(false))
            {
                cts.Token.ThrowIfCancellationRequested();
                await this.playback.PlayAsync(chunk, sampleRate, cts.Token).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types — voice pipeline must remain resilient
        catch (OperationCanceledException)
        {
            // Cancelled via CancelSpeaking, caller ct, or shutdown — expected, nothing to log.
        }
        catch (Exception ex)
        {
            this.LogSpeakError(ChannelId, ex.Message);
        }
#pragma warning restore CA1031
        finally
        {
            if (gateAcquired)
            {
                Interlocked.CompareExchange(ref this.activePlaybackCts, null, cts);

                if (this.status == ChannelStatus.Connected && !this.capture.IsCapturing)
                {
                    this.capture.Start();
                }
                SetVoiceState(GetPostActionState());

                this.playbackGate.Release();
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Manually trigger listening (for push-to-talk mode or web UI button).
    /// </summary>
    public void StartListening()
    {
        if (this.status != ChannelStatus.Connected)
        {
            return;
        }

        if (this.voiceState == VoiceState.Speaking)
        {
            this.playback.StopPlayback();
        }

        ResetAccumulator();
        SetVoiceState(VoiceState.Listening);
        this.lastSpeechTime = DateTimeOffset.UtcNow;
        this.LogPushToTalkActivated(ChannelId);
    }

    /// <summary>
    /// Manually stop listening and process accumulated audio (for push-to-talk mode or web UI button).
    /// </summary>
    public Task StopListeningAsync(CancellationToken ct = default)
    {
        if (this.voiceState != VoiceState.Listening)
        {
            return Task.CompletedTask;
        }

        return ProcessAccumulatedAudioAsync(ct);
    }

    /// <summary>
    /// Cancels active TTS playback (interrupts the agent speaking).
    /// Safe to call from any state — no-op if not currently speaking or paused.
    /// The <see cref="SendMessageAsync"/> method will catch the resulting
    /// <see cref="OperationCanceledException"/> and resume capture automatically.
    /// </summary>
    public void CancelSpeaking()
    {
        if (this.voiceState is not (VoiceState.Speaking or VoiceState.Paused))
        {
            return;
        }

        this.LogSpeakingCancelled(ChannelId);

        try
        {
            this.activePlaybackCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Cts was already disposed — playback had just finished; nothing to cancel.
        }

        this.playback.StopPlayback();
    }

    /// <summary>
    /// Pauses TTS playback at the current position.
    /// No-op if not currently speaking.
    /// </summary>
    public void PauseSpeaking()
    {
        if (this.voiceState != VoiceState.Speaking)
        {
            return;
        }

        this.playback.PausePlayback();
        SetVoiceState(VoiceState.Paused);
        this.LogSpeakingPaused(ChannelId);
    }

    /// <summary>
    /// Resumes TTS playback from the paused position.
    /// No-op if not currently paused.
    /// </summary>
    public void ResumeSpeaking()
    {
        if (this.voiceState != VoiceState.Paused)
        {
            return;
        }

        this.playback.ResumePlayback();
        SetVoiceState(VoiceState.Speaking);
        this.LogSpeakingResumed(ChannelId);
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.status != ChannelStatus.Disconnected)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }

        // Unwire overlay
        if (this.overlay is not null)
        {
            VoiceStateChanged -= this.overlay.OnVoiceStateChanged;
            AudioLevelChanged -= this.overlay.OnAudioLevelChanged;
            this.overlay.Dispose();
        }

        this.hotkeyListener?.Dispose();
        this.audioAccumulator.Dispose();
        this.playbackGate.Dispose();
    }

    // ── Hotkey handler (toggle mode) ────────────────────────────────

    private async void OnHotkeyToggle()
    {
        try
        {
            if (this.voiceState == VoiceState.Listening)
            {
                // Currently listening — stop and process
                await StopListeningAsync().ConfigureAwait(false);
            }
            else
            {
                // Not listening — start
                StartListening();
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            this.LogAudioPipelineError(ChannelId, ex.Message);
        }
#pragma warning restore CA1031
    }

    // ── Audio pipeline ───────────────────────────────────────────────

    private void OnAudioBufferReady(object? sender, AudioBufferEventArgs e)
    {
        if (this.status != ChannelStatus.Connected)
        {
            return;
        }

        try
        {
            switch (this.voiceState)
            {
                case VoiceState.Listening:
                    HandleListeningBuffer(e.Buffer, e.BytesRecorded);
                    break;

                case VoiceState.Processing:
                case VoiceState.Speaking:
                case VoiceState.Paused:
                case VoiceState.Idle:
                    // Ignore audio while processing/speaking/idle
                    break;
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types — event handler must not throw
        catch (Exception ex)
        {
            this.LogAudioPipelineError(ChannelId, ex.Message);
        }
#pragma warning restore CA1031
    }

    private void HandleListeningBuffer(byte[] buffer, int bytesRecorded)
    {
        var rms = AudioHelpers.ComputeRms(buffer.AsSpan(0, bytesRecorded));
        var isSpeech = rms >= this.options.VoiceActivityThreshold;

        if (isSpeech)
        {
            this.lastSpeechTime = DateTimeOffset.UtcNow;
        }

        // Report audio level for overlay equalizer (~10 Hz throttle)
        ReportAudioLevel(rms);

        this.audioAccumulator.Write(buffer, 0, bytesRecorded);

        // In push-to-talk mode, don't auto-stop on silence — wait for key release
        if (this.options.PushToTalk)
        {
            return;
        }

        // Check for end of utterance (silence timeout)
        if (!isSpeech && this.audioAccumulator.Length > 0)
        {
            var silenceDuration = DateTimeOffset.UtcNow - this.lastSpeechTime;
            if (silenceDuration.TotalMilliseconds >= this.options.SilenceTimeoutMs)
            {
                // Fire-and-forget processing — we're in an event handler
                _ = ProcessAccumulatedAudioAsync(CancellationToken.None);
            }
        }
    }

    private async Task ProcessAccumulatedAudioAsync(CancellationToken ct)
    {
        byte[] audioData;
        lock (this.stateLock)
        {
            if (this.audioAccumulator.Length == 0)
            {
                return;
            }

            audioData = this.audioAccumulator.ToArray();
            ResetAccumulator();
        }

        SetVoiceState(VoiceState.Processing);

        // Check minimum energy — skip if entire clip is too quiet
        var overallRms = AudioHelpers.ComputeRms(audioData);
        if (overallRms < this.options.VoiceActivityThreshold)
        {
            this.LogAudioTooQuiet(ChannelId, overallRms);
            SetVoiceState(GetPostActionState());
            return;
        }

        // Kick off speaker verification in parallel with STT so the gate adds
        // no latency on the critical path. See spec section "Verification pipeline".
        var pcm16Shorts = Cortex.Contained.Speech.AudioConverter.BytesToShorts(audioData);
        var verifyTask = VoiceChannelGate.EvaluateAsync(
            this.options.SpeakerVerifier,
            this.options.TenantId,
            pcm16Shorts,
            ct,
            this.options.VerificationMetrics);

        try
        {
            var text = await this.stt.TranscribeAsync(audioData, ct).ConfigureAwait(false);

            // Tap to runtime recorder if a host session is active. Best-effort
            // observer — never affects latency or correctness. The controller
            // pre-pads inter-utterance silence so session.wav stays aligned
            // with the session timeline.
            if (this.options.Recorder is { } rec)
            {
                try
                {
                    rec.RecordCommittedUtterance(
                        Cortex.Contained.Contracts.Recording.ChannelKey.Host,
                        audioData,
                        utteranceId: Guid.NewGuid().ToString("N"),
                        text: text ?? string.Empty,
                        reason: "host-commit");
                }
                catch (Exception capEx)
                {
                    this.LogHostRecorderTapFailed(ChannelId, capEx.Message);
                }
            }

            // Always observe verifyTask so any future cancellation/exception
            // surfaces deterministically.
            var gateDecision = await verifyTask.ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text))
            {
                this.LogNoSpeechTranscribed(ChannelId);
            }
            else if (!gateDecision.PassesTranscript)
            {
                this.LogTranscriptionRejected(ChannelId, this.options.TenantId);
            }
            else
            {
                this.LogTranscribed(ChannelId, text);
                await RaiseMessageReceivedAsync(text).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types — pipeline resilience
        catch (Exception ex)
        {
            this.LogTranscriptionError(ChannelId, ex.Message);
        }
#pragma warning restore CA1031

        // Return to appropriate state
        SetVoiceState(GetPostActionState());
    }

    private async Task RaiseMessageReceivedAsync(string text)
    {
        var messageId = string.Create(CultureInfo.InvariantCulture, $"voice-{Interlocked.Increment(ref this.messageCounter)}");

        var message = new InboundMessage
        {
            MessageId = messageId,
            ConversationId = this.conversationId,
            ChannelId = ChannelId,
            ChannelType = ChannelType.Voice,
            Sender = new SenderInfo
            {
                Id = "local-user",
                DisplayName = "Voice User",
                IsVerified = true,
            },
            Content = new MessageContent { Text = text },
            Timestamp = DateTimeOffset.UtcNow,
        };

        if (MessageReceived is { } handler)
        {
            await handler(message).ConfigureAwait(false);
        }
    }

    // ── State helpers ────────────────────────────────────────────────

    /// <summary>
    /// Determines the correct state to return to after processing/speaking completes.
    /// Push-to-talk → Idle (wait for next key press); open-mic → Listening.
    /// </summary>
    private VoiceState GetPostActionState()
    {
        return this.options.PushToTalk ? VoiceState.Idle : VoiceState.Listening;
    }

    private void SetStatus(ChannelStatus newStatus, string? reason = null)
    {
        var previous = this.status;
        this.status = newStatus;
        StatusChanged?.Invoke(new ChannelStatusChange(previous, newStatus, reason));
    }

    private void SetVoiceState(VoiceState newState)
    {
        var previous = this.voiceState;
        this.voiceState = newState;

        if (previous != newState)
        {
            this.LogVoiceStateChanged(ChannelId, previous, newState);
            VoiceStateChanged?.Invoke(new VoiceStateChange(previous, newState));
        }
    }

    private void ResetAccumulator()
    {
        this.audioAccumulator.SetLength(0);
    }

    /// <summary>
    /// Reports audio level to subscribers at a throttled rate (~10 Hz) to avoid
    /// overwhelming the UI thread with updates from the 100ms audio buffer callbacks.
    /// </summary>
    private void ReportAudioLevel(float rms)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - this.lastAudioLevelReport).TotalMilliseconds < 100)
        {
            return;
        }

        this.lastAudioLevelReport = now;
        AudioLevelChanged?.Invoke(rms);
    }

    // ── LoggerMessage ────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-capture (host): tap failed on {ChannelId}: {Error}")]
    private partial void LogHostRecorderTapFailed(string channelId, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice channel {ChannelId} connected")]
    private partial void LogConnected(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice channel {ChannelId} disconnected")]
    private partial void LogDisconnected(string channelId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Voice channel {ChannelId} speaking {TextLength} chars")]
    private partial void LogSpeaking(string channelId, int textLength);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice channel {ChannelId} TTS/playback error: {ErrorMessage}")]
    private partial void LogSpeakError(string channelId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Voice channel {ChannelId} audio too quiet (RMS={Rms})")]
    private partial void LogAudioTooQuiet(string channelId, float rms);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Voice channel {ChannelId} no speech transcribed")]
    private partial void LogNoSpeechTranscribed(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice channel {ChannelId} transcribed: \"{Text}\"")]
    private partial void LogTranscribed(string channelId, string text);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice channel {ChannelId} transcription error: {ErrorMessage}")]
    private partial void LogTranscriptionError(string channelId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice channel {ChannelId} speaker-id rejected utterance for tenant {TenantId}")]
    private partial void LogTranscriptionRejected(string channelId, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice channel {ChannelId} audio pipeline error: {ErrorMessage}")]
    private partial void LogAudioPipelineError(string channelId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Voice channel {ChannelId} state: {PreviousState} -> {NewState}")]
    private partial void LogVoiceStateChanged(string channelId, VoiceState previousState, VoiceState newState);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Voice channel {ChannelId} push-to-talk activated")]
    private partial void LogPushToTalkActivated(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice channel {ChannelId} speaking cancelled by user")]
    private partial void LogSpeakingCancelled(string channelId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Voice channel {ChannelId} speaking paused by user")]
    private partial void LogSpeakingPaused(string channelId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Voice channel {ChannelId} speaking resumed by user")]
    private partial void LogSpeakingResumed(string channelId);
}

/// <summary>Voice pipeline state machine.</summary>
public enum VoiceState
{
    /// <summary>Channel is not connected or waiting for push-to-talk key press.</summary>
    Idle = 0,

    /// <summary>Actively capturing speech audio.</summary>
    Listening = 2,

    /// <summary>Processing captured audio (STT transcription).</summary>
    Processing = 3,

    /// <summary>Playing TTS audio output.</summary>
    Speaking = 4,

    /// <summary>TTS playback is paused by the user.</summary>
    Paused = 5,
}
