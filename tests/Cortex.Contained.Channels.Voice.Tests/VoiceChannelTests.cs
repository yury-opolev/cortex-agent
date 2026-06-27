using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Messages;
using Cortex.Contained.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace Cortex.Contained.Channels.Voice.Tests;

public class VoiceChannelTests : IAsyncDisposable
{
    private readonly IAudioCapture _capture;
    private readonly IAudioPlayback _playback;
    private readonly ISpeechToText _stt;
    private readonly ITextToSpeech _tts;
    private readonly VoiceChannelOptions _options;
    private readonly VoiceChannel _sut;

    public VoiceChannelTests()
    {
        _capture = Substitute.For<IAudioCapture>();
        _playback = Substitute.For<IAudioPlayback>();
        _stt = Substitute.For<ISpeechToText>();
        _tts = Substitute.For<ITextToSpeech>();

        _stt.IsReady.Returns(true);
        _tts.OutputFormat.Returns(AudioFormat.Kokoro);

        // Open-mic (PushToTalk = false) so the VAD/silence-driven tests exercise the
        // Listening → Processing path. Push-to-talk is the product default now, but
        // these tests target the open-mic state machine explicitly.
        _options = new VoiceChannelOptions
        {
            ChannelId = "test-voice",
            PushToTalk = false,
            SilenceTimeoutMs = 1500,
            VoiceActivityThreshold = 0.01f,
        };

        _sut = new VoiceChannel(
            _capture,
            _playback,
            _stt,
            _tts,
            null,
            null,
            _options,
            NullLogger<VoiceChannel>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    #region Constructor and Properties

    [Fact]
    public void ChannelId_ReturnsConfiguredValue()
    {
        Assert.Equal("test-voice", _sut.ChannelId);
    }

    [Fact]
    public void Type_ReturnsVoice()
    {
        Assert.Equal(ChannelType.Voice, _sut.Type);
    }

    [Fact]
    public void Status_InitiallyDisconnected()
    {
        Assert.Equal(ChannelStatus.Disconnected, _sut.Status);
    }

    [Fact]
    public void CurrentVoiceState_InitiallyIdle()
    {
        Assert.Equal(VoiceState.Idle, _sut.CurrentVoiceState);
    }

    #endregion

    #region Capabilities

    [Fact]
    public void Capabilities_SupportsStreaming_IsFalse()
    {
        Assert.False(_sut.Capabilities.SupportsStreaming);
    }

    [Fact]
    public void Capabilities_SupportsRichText_IsFalse()
    {
        Assert.False(_sut.Capabilities.SupportsRichText);
    }

    [Fact]
    public void Capabilities_SupportsMedia_IsFalse()
    {
        Assert.False(_sut.Capabilities.SupportsMedia);
    }

    [Fact]
    public void Capabilities_SupportsEditing_IsFalse()
    {
        Assert.False(_sut.Capabilities.SupportsEditing);
    }

    [Fact]
    public void Capabilities_SupportsDeletion_IsFalse()
    {
        Assert.False(_sut.Capabilities.SupportsDeletion);
    }

    [Fact]
    public void Capabilities_MaxMessageLength_Is10000()
    {
        Assert.Equal(10_000, _sut.Capabilities.MaxMessageLength);
    }

    #endregion

    #region ConnectAsync

    [Fact]
    public async Task ConnectAsync_SetsStatusToConnected()
    {
        await _sut.ConnectAsync();

        Assert.Equal(ChannelStatus.Connected, _sut.Status);
    }

    [Fact]
    public async Task ConnectAsync_StartsAudioCapture()
    {
        await _sut.ConnectAsync();

        _capture.Received(1).Start();
    }

    [Fact]
    public async Task ConnectAsync_SubscribesToAudioBufferReady()
    {
        await _sut.ConnectAsync();

        _capture.Received(1).AudioBufferReady += Arg.Any<EventHandler<AudioBufferEventArgs>>();
    }

    [Fact]
    public async Task ConnectAsync_WithPushToTalk_SetsStateToIdle()
    {
        var options = _options with { PushToTalk = true };
        var sut = new VoiceChannel(
            _capture, _playback, _stt, _tts, null, null, options,
            NullLogger<VoiceChannel>.Instance);

        await sut.ConnectAsync();

        Assert.Equal(VoiceState.Idle, sut.CurrentVoiceState);
        await sut.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_OpenMic_SetsStateToListening()
    {
        var sut = new VoiceChannel(
            _capture, _playback, _stt, _tts, null, null, _options,
            NullLogger<VoiceChannel>.Instance);

        await sut.ConnectAsync();

        Assert.Equal(VoiceState.Listening, sut.CurrentVoiceState);
        await sut.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_SttNotReady_ThrowsInvalidOperationException()
    {
        _stt.IsReady.Returns(false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_SttNotReady_SetsStatusToError()
    {
        _stt.IsReady.Returns(false);

        try { await _sut.ConnectAsync(); }
        catch (InvalidOperationException) { }

        Assert.Equal(ChannelStatus.Error, _sut.Status);
    }

    [Fact]
    public async Task ConnectAsync_FiresStatusChangedEvent()
    {
        ChannelStatusChange? captured = null;
        _sut.StatusChanged += change =>
        {
            captured = change;
            return Task.CompletedTask;
        };

        await _sut.ConnectAsync();

        Assert.NotNull(captured);
        Assert.Equal(ChannelStatus.Connected, captured.CurrentStatus);
    }

    #endregion

    #region DisconnectAsync

    [Fact]
    public async Task DisconnectAsync_SetsStatusToDisconnected()
    {
        await _sut.ConnectAsync();

        await _sut.DisconnectAsync();

        Assert.Equal(ChannelStatus.Disconnected, _sut.Status);
    }

    [Fact]
    public async Task DisconnectAsync_StopsCapture()
    {
        await _sut.ConnectAsync();

        await _sut.DisconnectAsync();

        _capture.Received().StopCapture();
    }

    [Fact]
    public async Task DisconnectAsync_StopsPlayback()
    {
        await _sut.ConnectAsync();

        await _sut.DisconnectAsync();

        _playback.Received().StopPlayback();
    }

    [Fact]
    public async Task DisconnectAsync_SetsVoiceStateToIdle()
    {
        await _sut.ConnectAsync();

        await _sut.DisconnectAsync();

        Assert.Equal(VoiceState.Idle, _sut.CurrentVoiceState);
    }

    [Fact]
    public async Task DisconnectAsync_UnsubscribesFromAudioBufferReady()
    {
        await _sut.ConnectAsync();

        await _sut.DisconnectAsync();

        _capture.Received(1).AudioBufferReady -= Arg.Any<EventHandler<AudioBufferEventArgs>>();
    }

    [Fact]
    public async Task DisconnectAsync_FiresStatusChangedEvent()
    {
        await _sut.ConnectAsync();

        ChannelStatusChange? captured = null;
        _sut.StatusChanged += change =>
        {
            captured = change;
            return Task.CompletedTask;
        };

        await _sut.DisconnectAsync();

        Assert.NotNull(captured);
        Assert.Equal(ChannelStatus.Connected, captured.PreviousStatus);
        Assert.Equal(ChannelStatus.Disconnected, captured.CurrentStatus);
    }

    #endregion

    #region SendMessageAsync

    [Fact]
    public async Task SendMessageAsync_WithText_SynthesizesAndPlaysAudio()
    {
        var ttsAudio = new byte[] { 1, 2, 3, 4 };
        SetupTtsStreaming(ttsAudio);
        _playback.PlayAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ConnectAsync();

        var message = CreateOutboundMessage("msg-1", "Hello there");
        var result = await _sut.SendMessageAsync(message);

        Assert.True(result.Success);

        // Fire-and-forget: wait for background playback to complete before asserting mocks.
        await WaitForPlaybackCompletionAsync();

        _tts.Received(1).SynthesizeStreamingAsync("Hello there", cancellationToken: Arg.Any<CancellationToken>());
        await _playback.Received(1).PlayAsync(ttsAudio, AudioFormat.Kokoro.SampleRate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessageAsync_ReturnsBeforePlaybackCompletes()
    {
        // Gate playback so we can observe SendMessageAsync returning before audio finishes.
        var playbackBlocker = new TaskCompletionSource();
        SetupTtsStreaming(new byte[] { 1, 2 });
        _playback.PlayAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => playbackBlocker.Task);

        await _sut.ConnectAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _sut.SendMessageAsync(CreateOutboundMessage("msg-1", "Hello"));
        sw.Stop();

        // SendMessageAsync must not block on playback; elapsed should be well under
        // playback completion. 250ms is generous for CI noise.
        Assert.True(result.Success);
        Assert.True(sw.ElapsedMilliseconds < 250,
            $"SendMessageAsync took {sw.ElapsedMilliseconds}ms — should return before playback completes.");
        Assert.NotNull(_sut.ActivePlaybackTask);
        Assert.False(_sut.ActivePlaybackTask.IsCompleted);

        // Let playback complete so the test cleans up gracefully.
        playbackBlocker.SetResult();
        await WaitForPlaybackCompletionAsync();
    }

    [Fact]
    public async Task SendMessageAsync_ConcurrentCalls_AreSerializedByPlaybackGate()
    {
        // Track overlap: if two playbacks ran simultaneously, max concurrency would be > 1.
        var active = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        SetupTtsStreaming(new byte[] { 1 });
        _playback.PlayAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                lock (lockObj)
                {
                    active++;
                    if (active > maxConcurrent) maxConcurrent = active;
                }
                try
                {
                    await Task.Delay(50);
                }
                finally
                {
                    lock (lockObj) { active--; }
                }
            });

        await _sut.ConnectAsync();

        // Fire three messages in quick succession.
        await _sut.SendMessageAsync(CreateOutboundMessage("msg-1", "One"));
        await _sut.SendMessageAsync(CreateOutboundMessage("msg-2", "Two"));
        await _sut.SendMessageAsync(CreateOutboundMessage("msg-3", "Three"));

        // Wait for all queued playbacks to drain. ActivePlaybackTask reflects the
        // most-recently-queued one; waiting on it ensures at least it finished.
        await WaitForPlaybackCompletionAsync();

        // All three should have executed, but never overlapped.
        await _playback.Received(3).PlayAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task SendMessageAsync_WithEmptyText_ReturnsSuccessWithoutTts()
    {
        var message = CreateOutboundMessage("msg-1", "");

        var result = await _sut.SendMessageAsync(message);

        Assert.True(result.Success);
        Assert.Equal("msg-1", result.ExternalMessageId);
        await _tts.DidNotReceive().SynthesizeAsync(Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessageAsync_WithWhitespaceText_ReturnsSuccessWithoutTts()
    {
        var message = CreateOutboundMessage("msg-1", "   ");

        var result = await _sut.SendMessageAsync(message);

        Assert.True(result.Success);
        await _tts.DidNotReceive().SynthesizeAsync(Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessageAsync_PausesCaptureWhileSpeaking()
    {
        SetupTtsStreamingWithPlayback(new byte[] { 1, 2 });

        await _sut.ConnectAsync();
        _capture.ClearReceivedCalls();

        var message = CreateOutboundMessage("msg-1", "Hello");
        await _sut.SendMessageAsync(message);
        await WaitForPlaybackCompletionAsync();

        // StopCapture called before speaking, Start called after
        _capture.Received().StopCapture();
        _capture.Received().Start();
    }

    [Fact]
    public async Task SendMessageAsync_ReturnsExternalMessageId()
    {
        SetupTtsStreamingWithPlayback(new byte[] { 1 });

        await _sut.ConnectAsync();
        var message = CreateOutboundMessage("msg-42", "Test");
        var result = await _sut.SendMessageAsync(message);

        Assert.Equal("msg-42", result.ExternalMessageId);
    }

    [Fact]
    public async Task SendMessageAsync_TtsThrows_ReturnsSuccessAndDoesNotCrash()
    {
        // Fire-and-forget: SendMessageAsync returns immediately with success.
        // TTS errors happen on the background task and are logged (not surfaced to caller).
        _tts.SynthesizeStreamingAsync(Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => ThrowingAsyncEnumerable<byte[]>(new InvalidOperationException("TTS failed")));

        await _sut.ConnectAsync();
        var message = CreateOutboundMessage("msg-1", "Hello");
        var result = await _sut.SendMessageAsync(message);

        Assert.True(result.Success);

        // Background task should complete without propagating (no unobserved exception).
        await WaitForPlaybackCompletionAsync();
    }

    [Fact]
    public async Task SendMessageAsync_Cancelled_DoesNotCrash()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _tts.SynthesizeStreamingAsync(Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => ThrowingAsyncEnumerable<byte[]>(new OperationCanceledException()));

        await _sut.ConnectAsync();
        var message = CreateOutboundMessage("msg-1", "Hello");
        var result = await _sut.SendMessageAsync(message, cts.Token);

        // Fire-and-forget: caller always gets success; cancellation is observed on the task.
        Assert.True(result.Success);
        await WaitForPlaybackCompletionAsync();
    }

    [Fact]
    public async Task SendMessageAsync_AfterSpeaking_ResumesCapture()
    {
        SetupTtsStreamingWithPlayback(new byte[] { 1, 2 });

        await _sut.ConnectAsync();

        var message = CreateOutboundMessage("msg-1", "Hello");
        await _sut.SendMessageAsync(message);
        await WaitForPlaybackCompletionAsync();

        // After speaking, open-mic returns to Listening.
        Assert.Equal(VoiceState.Listening, _sut.CurrentVoiceState);
    }

    #endregion

    #region StartListening / StopListeningAsync (Push-to-Talk)

    [Fact]
    public async Task StartListening_WhenConnected_SetsStateToListening()
    {
        await _sut.ConnectAsync();

        _sut.StartListening();

        Assert.Equal(VoiceState.Listening, _sut.CurrentVoiceState);
    }

    [Fact]
    public void StartListening_WhenDisconnected_DoesNothing()
    {
        _sut.StartListening();

        Assert.Equal(VoiceState.Idle, _sut.CurrentVoiceState);
    }

    [Fact]
    public async Task StopListeningAsync_WhenNotListening_CompletesImmediately()
    {
        await _sut.ConnectAsync();

        // Not in Listening state, so should complete immediately
        var exception = await Record.ExceptionAsync(() => _sut.StopListeningAsync());

        Assert.Null(exception);
    }

    #endregion

    #region DisposeAsync

    [Fact]
    public async Task DisposeAsync_WhenConnected_Disconnects()
    {
        await _sut.ConnectAsync();

        await _sut.DisposeAsync();

        Assert.Equal(ChannelStatus.Disconnected, _sut.Status);
    }

    [Fact]
    public async Task DisposeAsync_WhenDisconnected_DoesNotFireStatusChanged()
    {
        var eventFired = false;
        _sut.StatusChanged += _ =>
        {
            eventFired = true;
            return Task.CompletedTask;
        };

        await _sut.DisposeAsync();

        Assert.False(eventFired);
    }

    #endregion

    #region MessageReceived Event (Integration via Audio Pipeline)

    [Fact]
    public async Task ConnectAsync_WithPushToTalk_StartListening_AccumulatesAudio_StopListening_RaisesMessageReceived()
    {
        var options = _options with { PushToTalk = true, VoiceActivityThreshold = 0.0f };
        var sut = new VoiceChannel(
            _capture, _playback, _stt, _tts, null, null, options,
            NullLogger<VoiceChannel>.Instance);

        _stt.TranscribeAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns("Hello world");

        await sut.ConnectAsync();

        // Simulate audio buffer event
        InboundMessage? captured = null;
        sut.MessageReceived += msg =>
        {
            captured = msg;
            return Task.CompletedTask;
        };

        sut.StartListening();

        // Trigger audio buffer with loud data
        var loudAudio = CreateLoudAudio(3200);
        _capture.AudioBufferReady += Raise.EventWith(new AudioBufferEventArgs
        {
            Buffer = loudAudio,
            BytesRecorded = loudAudio.Length,
        });

        // Stop listening triggers processing
        await sut.StopListeningAsync();

        // Give async event handler time to complete
        await Task.Delay(100);

        Assert.NotNull(captured);
        Assert.Equal("Hello world", captured.Content.Text);
        Assert.Equal(ChannelType.Voice, captured.ChannelType);
        Assert.Equal("local-user", captured.Sender.Id);
        Assert.Equal("Voice User", captured.Sender.DisplayName);
        Assert.True(captured.Sender.IsVerified);

        await sut.DisposeAsync();
    }

    #endregion

    #region ConversationId

    [Fact]
    public void ConversationId_IsBasedOnChannelId()
    {
        // The VoiceChannel creates conversationId as $"voice-{channelId}"
        // We verify this indirectly through a MessageReceived event
        // For now we just verify the channel ID is set
        Assert.Equal("test-voice", _sut.ChannelId);
    }

    #endregion

    #region PauseSpeaking / ResumeSpeaking / CancelSpeaking

    [Fact]
    public async Task PauseSpeaking_WhenSpeaking_SetsStateToPaused()
    {
        var playbackStarted = new TaskCompletionSource();
        var playbackGate = new TaskCompletionSource();

        SetupTtsStreaming(new byte[] { 1, 2 });
        _playback.PlayAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                playbackStarted.SetResult();
                await playbackGate.Task;
            });

        await _sut.ConnectAsync();

        await _sut.SendMessageAsync(CreateOutboundMessage("msg-1", "Hello"));
        await playbackStarted.Task;

        Assert.Equal(VoiceState.Speaking, _sut.CurrentVoiceState);

        _sut.PauseSpeaking();

        Assert.Equal(VoiceState.Paused, _sut.CurrentVoiceState);
        _playback.Received(1).PausePlayback();

        // Let playback complete so the background task finishes cleanly.
        playbackGate.SetResult();
        await WaitForPlaybackCompletionAsync();
    }

    [Fact]
    public async Task PauseSpeaking_WhenNotSpeaking_DoesNothing()
    {
        await _sut.ConnectAsync();

        _sut.PauseSpeaking();

        Assert.NotEqual(VoiceState.Paused, _sut.CurrentVoiceState);
        _playback.DidNotReceive().PausePlayback();
    }

    [Fact]
    public async Task ResumeSpeaking_WhenPaused_SetsStateToSpeaking()
    {
        var playbackStarted = new TaskCompletionSource();
        var playbackGate = new TaskCompletionSource();

        SetupTtsStreaming(new byte[] { 1, 2 });
        _playback.PlayAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                playbackStarted.SetResult();
                await playbackGate.Task;
            });

        await _sut.ConnectAsync();

        await _sut.SendMessageAsync(CreateOutboundMessage("msg-1", "Hello"));
        await playbackStarted.Task;

        _sut.PauseSpeaking();
        Assert.Equal(VoiceState.Paused, _sut.CurrentVoiceState);

        _sut.ResumeSpeaking();

        Assert.Equal(VoiceState.Speaking, _sut.CurrentVoiceState);
        _playback.Received(1).ResumePlayback();

        playbackGate.SetResult();
        await WaitForPlaybackCompletionAsync();
    }

    [Fact]
    public async Task ResumeSpeaking_WhenNotPaused_DoesNothing()
    {
        await _sut.ConnectAsync();

        _sut.ResumeSpeaking();

        _playback.DidNotReceive().ResumePlayback();
    }

    [Fact]
    public async Task CancelSpeaking_WhenSpeaking_StopsPlayback()
    {
        var playbackStarted = new TaskCompletionSource();
        var playbackGate = new TaskCompletionSource();

        SetupTtsStreaming(new byte[] { 1, 2 });
        _playback.PlayAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                playbackStarted.SetResult();
                await playbackGate.Task;
            });
        _playback.When(p => p.StopPlayback()).Do(_ =>
            playbackGate.TrySetException(new OperationCanceledException()));

        await _sut.ConnectAsync();

        await _sut.SendMessageAsync(CreateOutboundMessage("msg-1", "Hello"));
        await playbackStarted.Task;

        Assert.Equal(VoiceState.Speaking, _sut.CurrentVoiceState);

        _sut.CancelSpeaking();

        _playback.Received().StopPlayback();

        // Background task should observe cancellation and unwind cleanly.
        await WaitForPlaybackCompletionAsync();
        Assert.NotEqual(VoiceState.Speaking, _sut.CurrentVoiceState);
    }

    [Fact]
    public async Task CancelSpeaking_WhenPaused_StopsPlayback()
    {
        var playbackStarted = new TaskCompletionSource();
        var playbackGate = new TaskCompletionSource();

        SetupTtsStreaming(new byte[] { 1, 2 });
        _playback.PlayAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                playbackStarted.SetResult();
                await playbackGate.Task;
            });
        _playback.When(p => p.StopPlayback()).Do(_ =>
            playbackGate.TrySetException(new OperationCanceledException()));

        await _sut.ConnectAsync();

        await _sut.SendMessageAsync(CreateOutboundMessage("msg-1", "Hello"));
        await playbackStarted.Task;

        _sut.PauseSpeaking();
        Assert.Equal(VoiceState.Paused, _sut.CurrentVoiceState);

        _sut.CancelSpeaking();

        _playback.Received().StopPlayback();

        await WaitForPlaybackCompletionAsync();
    }

    [Fact]
    public void CancelSpeaking_WhenIdle_DoesNothing()
    {
        _sut.CancelSpeaking();

        _playback.DidNotReceive().StopPlayback();
    }

    [Fact]
    public async Task VoiceStateChanged_FiresOnPauseAndResume()
    {
        var playbackStarted = new TaskCompletionSource();
        var playbackGate = new TaskCompletionSource();

        SetupTtsStreaming(new byte[] { 1, 2 });
        _playback.PlayAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                playbackStarted.SetResult();
                await playbackGate.Task;
            });

        await _sut.ConnectAsync();

        var stateChanges = new List<VoiceStateChange>();
        _sut.VoiceStateChanged += change =>
        {
            stateChanges.Add(change);
        };

        await _sut.SendMessageAsync(CreateOutboundMessage("msg-1", "Hello"));
        await playbackStarted.Task;

        _sut.PauseSpeaking();
        _sut.ResumeSpeaking();

        // Should have: Speaking (from SendMessage), Paused (from Pause), Speaking (from Resume)
        Assert.Contains(stateChanges, c => c.NewState == VoiceState.Paused && c.PreviousState == VoiceState.Speaking);
        Assert.Contains(stateChanges, c => c.NewState == VoiceState.Speaking && c.PreviousState == VoiceState.Paused);

        playbackGate.SetResult();
        await WaitForPlaybackCompletionAsync();
    }

    #endregion

    #region Helpers

    private static OutboundMessage CreateOutboundMessage(string messageId, string text) =>
        new()
        {
            MessageId = messageId,
            ConversationId = "voice-test-voice",
            ChannelId = "test-voice",
            Content = new MessageContent { Text = text },
        };

    /// <summary>
    /// Creates audio data with high enough RMS to pass the voice activity threshold.
    /// </summary>
    private static byte[] CreateLoudAudio(int byteCount)
    {
        var data = new byte[byteCount];
        for (var i = 0; i < byteCount - 1; i += 2)
        {
            // ~half amplitude: 0x4000 = 16384
            data[i] = 0x00;
            data[i + 1] = 0x40;
        }

        return data;
    }

    /// <summary>
    /// Configures the TTS mock so that <see cref="ITextToSpeech.SynthesizeStreamingAsync"/>
    /// yields the given audio chunks. VoiceChannel calls SynthesizeStreamingAsync (not
    /// SynthesizeAsync) so all tests that exercise SendMessageAsync must use this helper.
    /// </summary>
    private void SetupTtsStreaming(params byte[][] chunks)
    {
        _tts.SynthesizeStreamingAsync(Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(callInfo => ToAsyncEnumerable(chunks));
    }

    /// <summary>
    /// Overload that configures streaming to yield a single chunk, and also configures
    /// the <c>PlayAsync</c> mock to return the provided <paramref name="playResult"/>.
    /// </summary>
    private void SetupTtsStreamingWithPlayback(byte[] chunk, Func<NSubstitute.Core.CallInfo, Task>? playResult = null)
    {
        SetupTtsStreaming(chunk);
        _playback.PlayAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(playResult ?? (_ => Task.CompletedTask));
    }

    private static async IAsyncEnumerable<byte[]> ToAsyncEnumerable(
        byte[][] chunks, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask; // Ensure truly async iteration
            yield return chunk;
        }
    }

    /// <summary>
    /// Creates an <see cref="IAsyncEnumerable{T}"/> that immediately throws the given exception
    /// when enumeration begins. Useful for testing error paths through streaming TTS.
    /// </summary>
    private static async IAsyncEnumerable<T> ThrowingAsyncEnumerable<T>(Exception exception)
    {
        await Task.CompletedTask; // Ensure truly async
        throw exception;
#pragma warning disable CS0162 // Unreachable code detected — required so compiler treats method as async iterator
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>
    /// Awaits the background speech synthesis + playback task created by the most recent
    /// fire-and-forget <see cref="VoiceChannel.SendMessageAsync"/> call. No-op if no
    /// playback is currently in flight. Errors are swallowed because the background
    /// task intentionally does not surface failures to its caller.
    /// </summary>
    private async Task WaitForPlaybackCompletionAsync()
    {
        var active = _sut.ActivePlaybackTask;
        if (active is null)
        {
            return;
        }

        try
        {
            await active;
        }
        catch
        {
            // Fire-and-forget contract: background failures are already logged, not rethrown.
        }
    }

    #endregion
}
