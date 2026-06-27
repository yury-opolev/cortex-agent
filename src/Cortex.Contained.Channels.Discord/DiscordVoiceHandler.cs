using System.Collections.Concurrent;
using System.Net.Http;

using Discord;
using Discord.Audio;
using Discord.Net;
using Discord.WebSocket;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Messages;
using Cortex.Contained.Speech;
using Cortex.Contained.Speech.SpeakerId;
using Cortex.Contained.Speech.Tts;
using Microsoft.Extensions.Logging;

using ChannelType = Cortex.Contained.Contracts.Channels.ChannelType;

namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Full-duplex Discord voice channel handler. Always listens and can speak
/// simultaneously. Receive/STT runs on a per-user loop; voice-out is handled by
/// a <see cref="VoiceOutPipeline"/> instance created on join and disposed on leave.
/// </summary>
/// <remarks>
/// Architecture:
/// <list type="bullet">
///   <item><b>Receive loop</b> — always running, reads 20ms PCM frames from Discord,
///     performs VAD, accumulates audio, detects silence timeout → transcription.</item>
///   <item><b>VoiceOutPipeline</b> — owns the sentence queue, audio queue,
///     producer/playback loops, and lifetime CTS. Created on join, disposed on leave.</item>
/// </list>
/// </remarks>
internal sealed partial class DiscordVoiceHandler : IAsyncDisposable
{
    // ── Constants ─────────────────────────────────────────────────────

    /// <summary>Opus frame size at 48kHz (20ms frames = 960 samples per channel).</summary>
    private const int OpusFrameSize = 960;

    /// <summary>
    /// Bytes per Opus frame at 48kHz stereo 16-bit (960 samples × 2 channels × 2 bytes).
    /// This is Discord.Net's native frame size — both <see cref="Discord.Audio.AudioInStream"/>
    /// and the stream returned by <see cref="Discord.Audio.IAudioClient.CreatePCMStream"/>
    /// expect/produce 3840-byte stereo frames. Mono PCM from our TTS engines must be upmixed
    /// before writing, and stereo frames from Discord must be downmixed before STT.
    /// </summary>
    private const int OpusFrameBytes = OpusFrameSize * 2 * 2;

    /// <summary>Minimum RMS energy threshold for voice activity detection.</summary>
    private const float VoiceActivityThreshold = 0.01f;

    // ── Substates ────────────────────────────────────────────────────

    private enum ListeningState { Idle, Hearing, Processing }

    // Suppress CS0414: this.listeningState is assigned-but-not-read in Stage A;
    // it will be read by streaming STT (EchoSharp) in Stage B for barge-in classification.
#pragma warning disable CS0414
    private volatile ListeningState listeningState = ListeningState.Idle;
#pragma warning restore CS0414

    // ── Dependencies ─────────────────────────────────────────────────

    private readonly ILogger logger;
    private readonly VoiceHandlerConfig config;
    private readonly ISpeechToText stt;
    private readonly IStreamingSpeechToText? streamingStt;
    private readonly Cortex.Contained.Speech.Stt.StreamingSttCoordinator sttCoordinator;
    private readonly ITurnDetector? turnDetector;
    private readonly bool turnDetectorActive;
    private readonly ITextToSpeech tts;
    private readonly DiscordSocketClient client;
    private readonly Func<InboundMessage, Task> onTranscription;

    /// <summary>
    /// Bridge → Agent barge-in commit. Invoked with the truncated assistant
    /// turn (exactly what the user heard) so the Agent Host records that
    /// instead of the full generated answer. Wired by the Bridge the same way
    /// as <see cref="onTranscription"/> (constructor injection from the
    /// <c>DiscordChannel</c> construction site, backed by the per-tenant
    /// <c>HubClient.OnTurnInterruptedAsync</c>). No-op default keeps the
    /// parameterless / non-Bridge construction paths working.
    /// </summary>
    private readonly Func<TurnInterruptedNotification, Task> onTurnInterrupted;

    /// <summary>
    /// Bridge → Agent cross-process generation abort for hold-back (phases
    /// <see cref="ConversationPhase.Committed"/> / <see cref="ConversationPhase.Thinking"/>):
    /// cancels the in-flight LLM turn in the Agent Host so the re-absorbed
    /// audio is not racing a wasted generation. Wired the same way as
    /// <see cref="onTranscription"/>, backed by <c>HubClient.AbortGenerationAsync</c>.
    /// </summary>
    private readonly Func<string, Task> onAbortGeneration;

    private readonly DaveEventStats? daveStats;
    private readonly ISpeakerVerifier? speakerVerifier;
    private readonly VerificationMetrics? verificationMetrics;

    // Minimum gap between turn-detector calls during silence — cheap debounce so
    // we don't run a ~20ms inference on every 20ms audio frame.
    private const int TurnDetectorDebounceMs = 100;

    /// <summary>
    /// Below this pEou the turn detector is treated as actively saying "not done",
    /// extending the wait past the base silence timeout (up to <see cref="MaxSilenceTimeoutMs"/>).
    /// </summary>
    private const float LowConfidenceEouThreshold = 0.005f;

    /// <summary>
    /// Hard ceiling on the silence wait. <see cref="VoiceHandlerConfig.SilenceTimeoutMs"/>
    /// is the soft commit point; this is the unconditional ceiling so a quiet user
    /// never strands the agent.
    /// </summary>
    private const int MaxSilenceTimeoutMs = 4000;

    /// <summary>
    /// Hold-back grace window (ms). On a <see cref="CommitConfidence.Tentative"/>
    /// commit the receive loop waits this long for the user to resume the same
    /// thought before dispatching. Confident commits ignore this (zero added
    /// latency). 0 disables hold-back entirely.
    /// </summary>
    private const int HoldBackGraceMs = 350;

    /// <summary>
    /// Absolute ceiling on a single accumulated utterance (ms). Independent of
    /// silence/grace timing — guarantees an utterance is dispatched even if the
    /// user keeps re-triggering the grace window. ~20 s of mono 48 kHz audio.
    /// </summary>
    private const int MaxUtteranceMs = 20000;

    /// <summary>
    /// Maximum time <see cref="ReceiveUserAudioAsync"/> will block on a single
    /// <c>ReadAsync</c> before falling through to the silence check. Bounds
    /// end-of-utterance detection latency even when Discord drops all incoming
    /// frames (e.g. sustained DAVE decryption failures), which would otherwise
    /// leave <c>ReadAsync</c> blocked indefinitely and strand the silence timer.
    /// </summary>
    private const int ReceivePollTimeoutMs = 100;

    // ── Discord audio ────────────────────────────────────────────────

    private IAudioClient? audioClient;
    private AudioOutStream? audioOutStream;
    private bool disposed;

    /// <summary>
    /// Serializes every connect/teardown path — user-join recovery, the
    /// dropped-connection event, gateway-reconnect recovery, and the proactive
    /// "ring" — so no two can call <see cref="JoinVoiceChannelAsync"/> /
    /// <see cref="LeaveVoiceChannelAsync"/> concurrently and double-connect or
    /// leak a pipeline. All external callers go through
    /// <see cref="EnsureConnectedAsync"/> or <see cref="LeaveGatedAsync"/>;
    /// the raw Join/Leave methods are only invoked while this gate is held.
    /// </summary>
    private readonly SemaphoreSlim connectionGate = new(1, 1);

    // ── Per-user audio state ─────────────────────────────────────────

    private readonly ConcurrentDictionary<ulong, UserAudioState> userAudioStates = new();

    // ── Voice-out pipeline (one per voice session) ───────────────────

    /// <summary>
    /// Owns the sentence queue, audio queue, producer/playback loops, and lifetime CTS.
    /// Created on join, disposed on leave. Null while the bot is not in voice.
    /// </summary>
    private volatile VoiceOutPipeline? voiceOut;

    // ── Per-session cancellation ─────────────────────────────────────

    /// <summary>Cancelled when the bot leaves the voice channel. Cancels all per-user receive loops.</summary>
    private CancellationTokenSource? sessionCts;

    // ── Turn arbitration (barge-in / hold-back state machine) ────────

    /// <summary>
    /// Owns the per-session conversation phase and serializes every transition
    /// (single owner — the connectionGate lesson). Created on join, disposed on
    /// leave. Null while the bot is not in voice. Replaces the dead
    /// <c>bargeInActive</c>/<c>ResolveBargeIn</c> stub.
    /// </summary>
    private volatile VoiceTurnArbiter? turnArbiter;

    /// <summary>
    /// True once <see cref="OnAgentFirstAudio"/> has been signalled for the
    /// current agent answer. Reset at each user commit so the
    /// <see cref="ConversationPhase.Speaking"/> transition fires exactly once
    /// per turn (on the first enqueued sentence), not on every text chunk.
    /// </summary>
    private volatile bool agentFirstAudioSignalled;

    // ── Sentence accumulator (for streaming LLM text → sentences) ────

    private readonly SentenceAccumulator sentenceAccumulator = new();
    private readonly object accumulatorLock = new();

    /// <summary>
    /// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> of the previous
    /// sentence enqueued for the current answer. Used to log the inter-sentence
    /// arrival interval — a large gap here points at the LLM token stream
    /// stalling (not TTS) as the cause of audible pauses between sentences.
    /// Guarded by <see cref="accumulatorLock"/>; reset implicitly per answer via
    /// <see cref="agentFirstAudioSignalled"/>.
    /// </summary>
    private long lastSentenceEnqueueTs;

    /// <summary>
    /// Accumulates every sentence emitted between <see cref="FlushAccumulator"/>
    /// calls — i.e. one full agent message — so language detection can run
    /// against the complete message text once it has been fully streamed.
    /// Guarded by <see cref="accumulatorLock"/>.
    /// </summary>
    private readonly System.Text.StringBuilder agentMessageBuilder = new();

    // ── Proactive delivery ───────────────────────────────────────────

    /// <summary>Voice invite TTL for the proactive "ring" DM (1 minute).</summary>
    private static readonly TimeSpan ProactiveRingTtl = TimeSpan.FromMinutes(1);

    /// <summary>Per-handler cap on queued proactive messages while a ring is active.</summary>
    private const int ProactiveQueueCap = 5;

    private readonly ProactiveVoiceCoordinator proactiveCoordinator;

    // ── Enrollment wizard (channel lock) ─────────────────────────────
    // When non-null, this handler is running the deterministic enrollment
    // wizard: committed utterances feed capture only (agent bypassed), the
    // speaker-id gate and barge-in are suspended, and the wizard speaks
    // scripted prompts. Guarded by wizardLock.
    private readonly object wizardLock = new();
    private WizardEnrollmentSession? wizardSession;
    private Timer? wizardTimeoutTimer;
    private bool pendingEnrollment;

    // ── Constructor ──────────────────────────────────────────────────

    public DiscordVoiceHandler(
        ILogger logger,
        VoiceHandlerConfig config,
        ISpeechToText stt,
        ITextToSpeech tts,
        DiscordSocketClient client,
        Func<InboundMessage, Task> onTranscription,
        IStreamingSpeechToText? streamingStt = null,
        ITurnDetector? turnDetector = null,
        DaveEventStats? daveStats = null,
        TimeProvider? timeProvider = null,
        Func<TurnInterruptedNotification, Task>? onTurnInterrupted = null,
        Func<string, Task>? onAbortGeneration = null)
    {
        this.logger = logger;
        this.config = config;
        this.stt = stt;
        this.streamingStt = streamingStt;
        this.turnDetector = turnDetector;
        this.tts = tts;
        this.client = client;
        this.onTranscription = onTranscription;
        this.onTurnInterrupted = onTurnInterrupted ?? (_ => Task.CompletedTask);
        this.onAbortGeneration = onAbortGeneration ?? (_ => Task.CompletedTask);
        this.daveStats = daveStats;
        this.speakerVerifier = config.SpeakerVerifier;
        this.verificationMetrics = config.VerificationMetrics;

        // The barge-in interrupt classifier's optional LLM tier is unavailable
        // on the Discord voice path by architecture: the only LLM client
        // (DirectLlmClient) lives in the Agent Host container, and the Bridge —
        // which hosts this handler — has no LLM client and no IAgentHub call to
        // borrow one without a fragile, high-latency cross-process round-trip on
        // the barge-in hot path. HeuristicPlusLlm therefore behaves exactly like
        // HeuristicOnly here (the heuristic core resolves Unsure→Real, the safe
        // default — we never talk over the user on uncertainty). Emit one
        // Information note at construction so the configured-but-inert mode is
        // observable rather than silently surprising.
        if (config.BargeInClassifierMode == BargeInClassifierMode.HeuristicPlusLlm)
        {
            this.LogBargeInLlmTierUnavailable();
        }

        this.sttCoordinator = new Cortex.Contained.Speech.Stt.StreamingSttCoordinator(
            stt,
            streamingStt,
            useStreaming: config.UseStreamingStt);

        // Turn detection requires all three: the feature flag on, a detector
        // that's actually ready, and streaming STT providing partials. If any
        // is missing we silently fall back to pure silence-timeout semantics.
        this.turnDetectorActive = config.UseTurnDetector
            && turnDetector is not null
            && turnDetector.IsReady
            && this.sttCoordinator.IsStreaming;

        this.proactiveCoordinator = new ProactiveVoiceCoordinator(
            joinVoice: _ => EnsureConnectedAsync("proactive-ring"),
            createInvite: CreateVoiceInviteAsync,
            sendRingDm: SendRingDmAsync,
            sendVoiceMessageDm: SendVoiceMessageDmAsync,
            speak: SpeakProactiveAsync,
            leaveVoice: _ => LeaveGatedAsync(),
            ringTtl: ProactiveRingTtl,
            queueCap: ProactiveQueueCap,
            logger: logger,
            timeProvider: timeProvider ?? TimeProvider.System);
    }

    /// <summary>The tenant ID this handler is associated with.</summary>
    public string TenantId => this.config.TenantId;

    /// <summary>
    /// Channel key used to address this handler in
    /// <see cref="ChannelLanguageStore"/>. Identical in shape to
    /// <see cref="VoiceConversationId"/> by design so logs/state are easy to
    /// correlate, but the key is intentionally distinct from the agent
    /// conversation id — it identifies the TTS routing channel, not the LLM
    /// session.
    /// </summary>
    internal string LanguageChannelKey => $"discord-voice-{this.config.TenantId}";

    // ── Public API ───────────────────────────────────────────────────

    /// <summary>
    /// Whether the bot has a <em>live</em> voice connection. Reflects the real
    /// audio-transport state, not merely that an <see cref="IAudioClient"/>
    /// reference exists: a Discord gateway reconnect tears down the voice
    /// transport but leaves <see cref="audioClient"/> non-null. Treating that
    /// stale reference as "connected" stranded Discord voice on 2026-05-15
    /// (bot never rejoined, never received audio, never replied).
    /// </summary>
    public bool IsConnected => VoiceConnectionState.IsAlive(this.audioClient?.ConnectionState);

    /// <summary>
    /// True when the linked user is currently a member of the configured voice channel,
    /// queried authoritatively via Discord REST (bypassing the gateway voice-state cache,
    /// which can drift across gateway reconnects). Falls back to cache on transport errors.
    /// </summary>
    private async Task<bool> IsUserInTargetChannelAsync(CancellationToken ct)
    {
        var guild = this.client.GetGuild(this.config.GuildId);
        if (guild is null)
        {
            return false;
        }

        try
        {
            var voiceState = await guild.GetUserVoiceStateAsync(
                this.config.LinkedUserId,
                new RequestOptions { CancelToken = ct }).ConfigureAwait(false);

            return voiceState?.VoiceChannelId == this.config.VoiceChannelId;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TimeoutException
            or TaskCanceledException
            or HttpException
            or RateLimitedException)
        {
            this.LogVoiceStateRestFallback(ex.Message);
            return this.IsUserInTargetChannelFromCache();
        }
    }

    /// <summary>
    /// Cache-only voice-presence check. Used as a fallback when the REST query fails.
    /// </summary>
    private bool IsUserInTargetChannelFromCache()
    {
        var guild = this.client.GetGuild(this.config.GuildId);
        var channel = guild?.GetVoiceChannel(this.config.VoiceChannelId);
        return channel?.Users.Any(u => u.Id == this.config.LinkedUserId) ?? false;
    }

    /// <summary>
    /// Deliver a proactive message to the voice channel. When the user is already
    /// in the voice channel, the message is spoken immediately via TTS (existing
    /// fast path). When the user is absent, a "ring" is started: bot joins the
    /// voice channel, creates a short-lived invite, DMs the invite to the linked
    /// user, and queues the message. If the user joins within the TTL, the queue
    /// is drained via TTS. Otherwise each queued message is delivered as an
    /// OGG/Opus attachment DM and the bot leaves.
    /// </summary>
    public async Task<ProactiveOutcome> EnqueueProactiveAsync(string text, string? languageHint = null, CancellationToken ct = default)
    {
        var userInVoice = await this.IsUserInTargetChannelAsync(ct).ConfigureAwait(false);
        return await this.proactiveCoordinator.EnqueueAsync(text, userInVoice, ct, languageHint).ConfigureAwait(false);
    }

    /// <summary>
    /// Begin the deterministic enrollment wizard on this voice channel. Locks the
    /// channel (committed utterances feed capture only), speaks the intro + first
    /// phrase, and arms an inactivity timeout. No-op (logs) when the embedder or
    /// submit delegate is not configured. Safe to call when the user is connected.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task StartEnrollmentWizardAsync(CancellationToken cancellationToken = default)
    {
        if (this.config.SpeakerEmbedder is null || this.config.SubmitVoiceprintAsync is null)
        {
            this.LogWizardUnavailable(this.config.TenantId);
            return;
        }

        lock (this.wizardLock)
        {
            if (this.wizardSession is not null)
            {
                return; // already running
            }

            this.wizardSession = new WizardEnrollmentSession(
                this.config.SpeakerEmbedder,
                this.config.EnrollSamplesRequired,
                this.config.EnrollMatchesRequired,
                this.config.EnrollConfirmThreshold);
            this.ArmWizardTimeout();
        }

        this.LogWizardStarted(this.config.TenantId);
        var line = WizardScriptMapper.LineFor(WizardPhase.Enrolling, 0, this.config.EnrollSamplesRequired, this.config.EnrollMatchesRequired);
        await this.EnqueueProactiveAsync(line.Text, languageHint: "en", ct: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Arm the wizard to start automatically the next time the linked user joins the
    /// configured voice channel. Used when <c>/voice-id enroll</c> is run while the
    /// user is not yet in voice — the bot rings them in, and the wizard begins on join.
    /// </summary>
    public void ArmPendingEnrollment()
    {
        lock (this.wizardLock)
        {
            this.pendingEnrollment = true;
        }
    }

    private async Task MaybeStartPendingEnrollmentAsync()
    {
        bool pending;
        lock (this.wizardLock)
        {
            pending = this.pendingEnrollment;
            this.pendingEnrollment = false;
        }

        if (pending)
        {
            await this.StartEnrollmentWizardAsync().ConfigureAwait(false);
        }
    }

    private void ArmWizardTimeout()
    {
        // caller holds wizardLock
        this.wizardTimeoutTimer?.Dispose();
        this.wizardTimeoutTimer = new Timer(_ => this.OnWizardTimeout(), null, this.config.EnrollTimeoutMs, Timeout.Infinite);
    }

    private void OnWizardTimeout()
    {
        if (!this.EndEnrollmentWizard())
        {
            return;
        }

        this.LogWizardTimedOut(this.config.TenantId);
        _ = this.EnqueueProactiveAsync("I didn't catch enough to enroll your voice — run /voice-id enroll to try again.", languageHint: "en", ct: CancellationToken.None);
    }

    /// <summary>Releases the wizard lock. Returns true if a wizard was active (so callers
    /// don't double-fire end logic). Always safe to call (idempotent).</summary>
    private bool EndEnrollmentWizard()
    {
        lock (this.wizardLock)
        {
            if (this.wizardSession is null)
            {
                return false;
            }

            this.wizardSession = null;
            this.wizardTimeoutTimer?.Dispose();
            this.wizardTimeoutTimer = null;
            this.pendingEnrollment = false;
            return true;
        }
    }

    private async Task HandleEnrollmentUtteranceAsync(ReadOnlyMemory<short> pcm16kShorts)
    {
        WizardEnrollmentSession? session;
        lock (this.wizardLock)
        {
            session = this.wizardSession;
            if (session is null)
            {
                return;
            }

            this.ArmWizardTimeout(); // reset inactivity timer on each repeat
        }

        try
        {
            var result = await WizardTurnAdvancer.AdvanceAsync(
                session,
                pcm16kShorts,
                async text => { await this.EnqueueProactiveAsync(text, languageHint: "en").ConfigureAwait(false); },
                (vp, modelId) => this.config.SubmitVoiceprintAsync!(this.config.TenantId, vp, modelId),
                this.config.EnrollSamplesRequired,
                this.config.EnrollMatchesRequired,
                CancellationToken.None).ConfigureAwait(false);

            if (result == WizardAdvanceResult.Completed)
            {
                this.EndEnrollmentWizard();
                this.LogWizardCompleted(this.config.TenantId);
            }
        }
#pragma warning disable CA1031 // wizard capture must never crash the audio loop
        catch (Exception ex)
        {
            this.LogWizardCaptureFailed(this.config.TenantId, ex.Message);
            this.EndEnrollmentWizard();
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Handle a user voice state update. When the user joins the configured voice channel,
    /// the bot joins too. When the user leaves, the bot leaves.
    /// </summary>
    public Task HandleVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (user.IsBot)
        {
            return Task.CompletedTask;
        }

        // Only respond to the tenant's linked Discord user
        if (user.Id != this.config.LinkedUserId)
        {
            return Task.CompletedTask;
        }

        var targetVoiceChannelId = this.config.VoiceChannelId;

        var joinedTarget = after.VoiceChannel?.Id == targetVoiceChannelId;
        var leftTarget = before.VoiceChannel?.Id == targetVoiceChannelId
                         && after.VoiceChannel?.Id != targetVoiceChannelId;

        var otherNonBotUsersPresent = before.VoiceChannel is SocketVoiceChannel leftChannel
            && leftChannel.ConnectedUsers.Any(u => !u.IsBot);

        var action = VoiceStateRouter.Route(joinedTarget, leftTarget, IsConnected, otherNonBotUsersPresent);

        // Fire-and-forget: the join path awaits ConnectAsync which needs gateway
        // events (VOICE_SERVER_UPDATE) to complete. Awaiting here would deadlock
        // because this handler runs on the gateway dispatch thread.
        switch (action)
        {
            case VoiceStateAction.JoinAndDrainProactive:
                this.LogUserJoinedVoice(user.Username, targetVoiceChannelId);
                _ = Task.Run(async () =>
                {
                    await EnsureConnectedAsync("user-joined").ConfigureAwait(false);
                    await this.proactiveCoordinator.OnUserJoinedAsync(CancellationToken.None).ConfigureAwait(false);
                    await this.MaybeStartPendingEnrollmentAsync().ConfigureAwait(false);
                });
                break;

            case VoiceStateAction.DrainProactive:
                // Bot already connected (proactive ring in progress). User accepted
                // the invite — drain any queued proactive messages via TTS.
                this.LogUserJoinedVoice(user.Username, targetVoiceChannelId);
                _ = Task.Run(async () =>
                {
                    await this.proactiveCoordinator.OnUserJoinedAsync(CancellationToken.None).ConfigureAwait(false);
                    await this.MaybeStartPendingEnrollmentAsync().ConfigureAwait(false);
                });
                break;

            case VoiceStateAction.Leave:
                this.LogUserLeftVoice(user.Username, targetVoiceChannelId);
                _ = Task.Run(LeaveGatedAsync);
                break;

            case VoiceStateAction.None:
            default:
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Ensures a live voice connection exists, recovering from a stale audio
    /// client left behind by a gateway reconnect. No-op when already connected.
    /// Serialized via <see cref="connectionGate"/> so concurrent triggers
    /// (user-join, dropped-connection event, gateway reconnect) cannot
    /// double-connect.
    /// </summary>
    private async Task EnsureConnectedAsync(string trigger)
    {
        if (this.disposed || IsConnected)
        {
            return;
        }

        await this.connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (this.disposed || IsConnected)
            {
                return;
            }

            this.LogVoiceRecovering(trigger);

            // Drop a stale/dead client (non-null but not Connected) before rejoining.
            if (this.audioClient is not null)
            {
                await LeaveVoiceChannelAsync().ConfigureAwait(false);
            }

            await JoinVoiceChannelAsync(this.config.VoiceChannelId).ConfigureAwait(false);
        }
        finally
        {
            this.connectionGate.Release();
        }
    }

    /// <summary>
    /// Leave the voice channel under <see cref="connectionGate"/> so a leave
    /// (user left, or proactive-ring fallback) cannot race a concurrent
    /// connect/recovery. Safe when not connected — the teardown no-ops.
    /// </summary>
    private async Task LeaveGatedAsync()
    {
        // The user is leaving the channel — release any active wizard lock so it
        // cannot get stuck holding the channel.
        this.EndEnrollmentWizard();

        await this.connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await LeaveVoiceChannelAsync().ConfigureAwait(false);
        }
        finally
        {
            this.connectionGate.Release();
        }
    }

    /// <summary>
    /// Discord raises this when the voice transport drops (commonly as a
    /// side-effect of a gateway reconnect). Tear down the dead client and
    /// rejoin if the linked user is still in the channel — otherwise voice
    /// stays silently dead until a manual rejoin (the 2026-05-15 outage).
    /// </summary>
    private Task OnAudioClientDisconnected(Exception ex)
    {
        this.LogVoiceConnectionLost(ex?.Message ?? "unknown");

        _ = Task.Run(async () =>
        {
            try
            {
                if (await IsUserInTargetChannelAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    await EnsureConnectedAsync("connection-lost").ConfigureAwait(false);
                    await this.proactiveCoordinator.OnUserJoinedAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    // User absent — drop the dead client so a later join reconnects cleanly.
                    await LeaveGatedAsync().ConfigureAwait(false);
                }
            }
            catch (Exception recoveryEx)
            {
                this.LogVoiceRecoveryFailed("connection-lost", recoveryEx.Message);
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called by <see cref="DiscordChannel"/> after the gateway reconnects. A
    /// reconnect silently kills the voice transport; if the linked user is
    /// still in the channel, rejoin so audio receive resumes without the user
    /// having to leave and come back.
    /// </summary>
    public Task OnGatewayReconnectedAsync()
    {
        if (this.disposed)
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (await IsUserInTargetChannelAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    await EnsureConnectedAsync("gateway-reconnect").ConfigureAwait(false);
                    await this.proactiveCoordinator.OnUserJoinedAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception recoveryEx)
            {
                this.LogVoiceRecoveryFailed("gateway-reconnect", recoveryEx.Message);
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Accept a text chunk from the LLM streaming response.
    /// Accumulates text and enqueues complete sentences for TTS synthesis.
    /// Thread-safe — called from the streaming dispatcher.
    /// </summary>
    public void AcceptTextChunk(string chunk)
    {
        if (this.voiceOut is not { } pipeline)
        {
            return;
        }

        lock (this.accumulatorLock)
        {
            this.sentenceAccumulator.Append(chunk);
            this.LogAcceptTextChunk(chunk.Length);

            while (this.sentenceAccumulator.TryGetNextSentence(out var sentence))
            {
                var nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
                var gapMs = this.agentFirstAudioSignalled
                    ? (int)System.Diagnostics.Stopwatch.GetElapsedTime(this.lastSentenceEnqueueTs, nowTs).TotalMilliseconds
                    : 0;
                this.lastSentenceEnqueueTs = nowTs;
                this.LogSentenceEnqueued(sentence.Length, gapMs);
                pipeline.TryEnqueue(sentence);
                this.agentMessageBuilder.Append(sentence).Append(' ');

                // Phase transition: first audio for this answer is now on its
                // way to the user — the agent owns the turn (Speaking). Fire
                // exactly once per turn, not on every chunk.
                if (!this.agentFirstAudioSignalled)
                {
                    this.agentFirstAudioSignalled = true;
                    this.turnArbiter?.OnAgentFirstAudio();
                }
            }
        }
    }

    /// <summary>
    /// Flush any remaining partial sentence from the accumulator.
    /// Called when the LLM streaming response is finalized.
    /// </summary>
    public void FlushAccumulator()
    {
        if (this.voiceOut is not { } pipeline)
        {
            return;
        }

        string fullText;
        lock (this.accumulatorLock)
        {
            var drainedCount = 0;

            // First drain any complete sentences
            while (this.sentenceAccumulator.TryGetNextSentence(out var sentence))
            {
                pipeline.TryEnqueue(sentence);
                this.agentMessageBuilder.Append(sentence).Append(' ');
                drainedCount++;
            }

            // Then flush the remaining partial
            var remaining = this.sentenceAccumulator.Flush();
            if (remaining is not null)
            {
                var nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
                var gapMs = this.agentFirstAudioSignalled
                    ? (int)System.Diagnostics.Stopwatch.GetElapsedTime(this.lastSentenceEnqueueTs, nowTs).TotalMilliseconds
                    : 0;
                this.lastSentenceEnqueueTs = nowTs;
                this.LogSentenceEnqueued(remaining.Length, gapMs);
                pipeline.TryEnqueue(remaining);
                this.agentMessageBuilder.Append(remaining).Append(' ');
                drainedCount++;
            }

            // Phase transition: a short answer with no sentence terminator
            // reaches the user only via the flush — cover that path too so the
            // Speaking transition fires exactly once per turn.
            if (drainedCount > 0 && !this.agentFirstAudioSignalled)
            {
                this.agentFirstAudioSignalled = true;
                this.turnArbiter?.OnAgentFirstAudio();
            }

            // The response stream is finalized: drop an in-band end-of-response
            // marker after the last sentence. When playback reaches it,
            // OnAgentFinished fires (Phase → Listening) so the user's next
            // answer is a normal turn, not a spurious barge-in.
            pipeline.MarkEndOfResponse();

            this.LogFlushAccumulator(drainedCount);

            // Snapshot the message text and reset the builder under the same
            // lock that guards every accumulator mutation, so a subsequent
            // turn's AcceptTextChunk does not race the detection read.
            fullText = this.agentMessageBuilder.ToString().Trim();
            this.agentMessageBuilder.Clear();
        }

        // Run language detection outside the accumulator lock — the detector
        // is pure CPU work but takes microseconds-to-milliseconds and we do
        // not want to block AcceptTextChunk on the next agent answer.
        if (!string.IsNullOrEmpty(fullText)
            && this.config.LanguageDetector is { } det
            && this.config.LanguageStore is { } store)
        {
            var result = store.UpdateFromDetection(this.LanguageChannelKey, fullText, det, this.config.LanguageSwitchThresholds);
            this.LogLangDetect("agent-out", this.LanguageChannelKey, result);
        }
    }

    /// <summary>
    /// Hand a fully-formed text message to the voice-out pipeline.
    /// Returns as soon as the text is enqueued — synthesis and playback happen on
    /// the pipeline's background loops. Cancellation is bound to the voice session
    /// lifetime (the pipeline's own CTS). The <paramref name="ct"/> parameter is
    /// accepted for API compatibility only.
    /// </summary>
    /// <param name="text">The text to speak.</param>
    /// <param name="languageHint">Optional ISO 639-1 language code forwarded
    /// directly to TTS synthesis for this message, overriding the channel's
    /// sticky current language. Used for wizard prompts that must remain in
    /// English regardless of recent conversation language.</param>
    /// <param name="ct">Cancellation token (API-compat only — see remarks).</param>
    public Task SendVoiceAsync(string text, string? languageHint = null, CancellationToken ct = default)
    {
        if (this.voiceOut is not { } pipeline)
        {
            throw new VoiceNotConnectedException("Bot is not connected to a voice channel");
        }

        if (!pipeline.TryEnqueue(text, languageHint))
        {
            throw new VoiceNotConnectedException("Voice session is disposing");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Speak a proactive message through the same Speaking-phase lifecycle as a
    /// normal agent reply, so the user can barge in on it. The proactive path
    /// does not flow through <see cref="AcceptTextChunk"/>/<see cref="FlushAccumulator"/>,
    /// so without this the turn arbiter never enters
    /// <see cref="ConversationPhase.Speaking"/> and the barge-in detector never
    /// commits an interrupt during proactive delivery. We move the arbiter into
    /// Speaking before enqueuing, then drop an end-of-response marker so playback
    /// completion fires <c>OnAgentFinished</c> (Phase → Listening) exactly as a
    /// streamed reply does. A barge-in bumps the pipeline epoch and drops the
    /// marker, so completion does not fire on an interrupt.
    /// </summary>
    private async Task SpeakProactiveAsync(string text, string? languageHint, CancellationToken ct)
    {
        this.turnArbiter?.OnAgentFirstAudio();
        await this.SendVoiceAsync(text, languageHint, ct).ConfigureAwait(false);
        this.voiceOut?.MarkEndOfResponse();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        this.EndEnrollmentWizard();
        await LeaveVoiceChannelAsync().ConfigureAwait(false);
        this.connectionGate.Dispose();
    }

    // ── Proactive ring callbacks ─────────────────────────────────────

    private async Task<string> CreateVoiceInviteAsync(CancellationToken ct)
    {
        var guild = this.client.GetGuild(this.config.GuildId)
            ?? throw new InvalidOperationException($"Guild {this.config.GuildId} not found");
        var channel = guild.GetVoiceChannel(this.config.VoiceChannelId)
            ?? throw new InvalidOperationException($"Voice channel {this.config.VoiceChannelId} not found");

        var invite = await channel.CreateInviteAsync(
            maxAge: (int)ProactiveRingTtl.TotalSeconds,
            maxUses: 1,
            isTemporary: false,
            isUnique: true).ConfigureAwait(false);
        return invite.Url;
    }

    private async Task SendRingDmAsync(string inviteUrl, CancellationToken ct)
    {
        var user = await this.client.GetUserAsync(this.config.LinkedUserId).ConfigureAwait(false);
        if (user is null)
        {
            this.LogProactiveDmUserMissing(this.config.LinkedUserId);
            return;
        }

        var dm = await user.CreateDMChannelAsync().ConfigureAwait(false);
        await dm.SendMessageAsync(inviteUrl).ConfigureAwait(false);
    }

    private async Task SendVoiceMessageDmAsync(string text, CancellationToken ct)
    {
        var user = await this.client.GetUserAsync(this.config.LinkedUserId).ConfigureAwait(false);
        if (user is null)
        {
            this.LogProactiveDmUserMissing(this.config.LinkedUserId);
            return;
        }

        var dm = await user.CreateDMChannelAsync().ConfigureAwait(false);

        byte[] oggData;
        try
        {
            var pcm = await this.tts.SynthesizeAsync(
                text,
                languageHint: this.config.LanguageStore?.GetCurrent(this.LanguageChannelKey),
                cancellationToken: ct).ConfigureAwait(false);
            oggData = Cortex.Contained.Speech.AudioConverter.EncodeOggOpus(pcm, this.tts.OutputFormat.SampleRate);
        }
        catch (Exception ex)
        {
            this.LogProactiveVoiceDmSynthesisFailed(ex.Message);
            // Fall back to plain-text DM so the message is still delivered.
            await dm.SendMessageAsync(text).ConfigureAwait(false);
            return;
        }

        using var stream = new MemoryStream(oggData);
        await dm.SendFileAsync(stream, "proactive.ogg", text: text).ConfigureAwait(false);
    }

    // ── Voice channel lifecycle ──────────────────────────────────────

    private async Task JoinVoiceChannelAsync(ulong voiceChannelId)
    {
        try
        {
            var guild = this.client.GetGuild(this.config.GuildId);
            if (guild is null)
            {
                this.LogVoiceJoinFailed($"Guild {this.config.GuildId} not found");
                return;
            }

            var voiceChannel = guild.GetVoiceChannel(voiceChannelId);
            if (voiceChannel is null)
            {
                this.LogVoiceJoinFailed($"Voice channel {voiceChannelId} not found in guild {guild.Name}");
                return;
            }

            // Log bot permissions for diagnostics
            var botUser = guild.CurrentUser;
            if (botUser is not null)
            {
                var perms = botUser.GetPermissions(voiceChannel);
                this.LogVoicePermissions(voiceChannel.Name, perms.Connect, perms.Speak, perms.UseVAD);
            }

            this.LogVoiceJoining(voiceChannel.Name, voiceChannelId);

            this.audioClient = await voiceChannel.ConnectAsync(selfDeaf: false).ConfigureAwait(false);
            this.audioOutStream = this.audioClient.CreatePCMStream(AudioApplication.Mixed, 48000);

            // Subscribe to per-user audio streams
            this.audioClient.StreamCreated += OnAudioStreamCreated;
            this.audioClient.StreamDestroyed += OnAudioStreamDestroyed;
            this.audioClient.Disconnected += OnAudioClientDisconnected;

            // Initialize session CTS — cancelled in LeaveVoiceChannelAsync to stop all receive loops.
            this.sessionCts?.Dispose();
            this.sessionCts = new CancellationTokenSource();

            // One pipeline per voice session. Disposed in LeaveVoiceChannelAsync.
            var sink = new DiscordAudioOutSink(this.audioOutStream);
            var voiceOutOptions = new VoiceOutOptions
            {
                OutputGain = this.config.OutputGain,
                SourceSampleRate = this.tts.OutputFormat.SampleRate,
            };
            // onResponsePlaybackComplete fires when the in-band end-of-response
            // marker reaches playback (i.e. the agent's whole answer has been
            // spoken) and no barge-in superseded it → back to Listening. This
            // is the per-answer "agent finished speaking" signal; without it
            // Phase stayed Speaking and every normal answer was mis-read as a
            // barge-in. turnArbiter is assigned just below; the callback only
            // runs later, on the playback thread, by which time it is set.
            this.voiceOut = new VoiceOutPipeline(
                this.tts,
                sink,
                voiceOutOptions,
                this.logger,
                onResponsePlaybackComplete: () => this.turnArbiter?.OnAgentFinished(),
                defaultLanguageHintProvider: () => this.config.LanguageStore?.GetCurrent(this.LanguageChannelKey),
                noticeGender: this.config.VoiceGender);

            // One turn-arbiter per voice session — owns the conversation phase
            // and drives barge-in / hold-back via the pipeline + handler seams.
            //
            // The interrupt classifier's optional LLM tier is intentionally left
            // unwired (llm: null) on the Discord voice path — including when
            // BargeInClassifierMode == HeuristicPlusLlm.
            //
            // Rationale (architectural, not a TODO): the only LLM client in the
            // system (DirectLlmClient) runs inside the Agent Host container. This
            // handler runs Bridge-side, and the Bridge has neither an LLM client
            // nor an IAgentHub call that could borrow one. The only way to reach
            // the LLM from here would be a bespoke cross-process SignalR
            // round-trip on the barge-in hot path — exactly the fragile,
            // high-latency coupling this design avoids (the classifier only ever
            // delays *resume*, never the stop, and resolving Unsure→Real here is
            // already the correct, safe behaviour: never talk over the user on
            // uncertainty). HeuristicPlusLlm therefore behaves identically to
            // HeuristicOnly on this path; the difference is surfaced once at
            // handler construction via LogBargeInLlmTierUnavailable() so the
            // configured-but-inert mode is observable. The
            // HeuristicInterruptClassifier still accepts an LLM delegate, so a
            // future in-process voice path (e.g. local voice with a co-located
            // model) can light up the tier without touching the core.
            var classifier = new HeuristicInterruptClassifier(llm: null);
            var pipelineForArbiter = this.voiceOut;
            var voiceConversationId = VoiceConversationId(this.config.TenantId);
            this.turnArbiter = new VoiceTurnArbiter(
                BuildArbiterCallbacks(
                    conversationId: voiceConversationId,
                    stopPlayback: () => pipelineForArbiter is { } p
                        ? p.StopForBargeInAsync()
                        : Task.FromResult(new PlaybackProgress([], null, 0.0)),
                    reenqueueSentence: sentence => pipelineForArbiter?.ReenqueueSentence(sentence),
                    classify: classifier.ClassifyAsync,
                    onTurnInterrupted: this.onTurnInterrupted,
                    onAbortGeneration: this.onAbortGeneration,
                    logger: this.logger),
                bargeInEnabled: this.config.EnableBargeIn,
                logger: this.logger);

            lock (this.accumulatorLock)
            {
                this.sentenceAccumulator.Reset();
                this.agentMessageBuilder.Clear();
            }

            this.LogVoiceJoined(voiceChannel.Name, voiceChannelId);

            // Speak greeting if configured. Best-effort — a greeting failure must not abort the join.
            if (!string.IsNullOrEmpty(this.config.VoiceGreeting))
            {
                try
                {
                    await SendVoiceAsync(this.config.VoiceGreeting, ct: CancellationToken.None).ConfigureAwait(false);
                }
                catch (VoiceNotConnectedException ex)
                {
                    this.LogVoiceGreetingSkipped(ex.Message);
                }
            }

            // Defense-in-depth: if a "ring" was started because the cache said the user
            // wasn't in voice, but the user is actually already in the channel, no
            // VOICE_STATE_UPDATE will fire and OnUserJoinedAsync would never run.
            // Drain unconditionally — OnUserJoinedAsync is a no-op when no ring is active.
            if (await IsUserInTargetChannelAsync(CancellationToken.None).ConfigureAwait(false))
            {
                _ = Task.Run(() => this.proactiveCoordinator.OnUserJoinedAsync(CancellationToken.None));
            }
        }
        catch (TimeoutException ex)
        {
            this.LogVoiceJoinTimeout(voiceChannelId, ex);
            await CleanupAfterJoinFailureAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogVoiceJoinException(voiceChannelId, ex);
            await CleanupAfterJoinFailureAsync().ConfigureAwait(false);
        }
    }

    private async Task LeaveVoiceChannelAsync()
    {
        // Dispose the pipeline first — it cancels its own CTS, awaits its loops,
        // disposes the sink (which owns audioOutStream), and drops queued sentences/audio.
        if (this.voiceOut is { } pipeline)
        {
            this.voiceOut = null;
            await pipeline.DisposeAsync().ConfigureAwait(false);
        }

        // Phase transition: playback has drained (the pipeline was disposed
        // above, which cancels and discards all queued/in-flight audio), so the
        // agent no longer owns the turn — back to Listening before teardown.
        // (The pipeline exposes no per-turn drain-complete hook; the per-answer
        // drain signal is therefore covered at session teardown here.)
        this.turnArbiter?.OnAgentFinished();

        // Dispose the per-session turn-arbiter (owns a SemaphoreSlim).
        if (this.turnArbiter is { } arbiter)
        {
            this.turnArbiter = null;
            arbiter.Dispose();
        }

        // The pipeline's sink already disposed the audio stream — just null the reference.
        this.audioOutStream = null;

        // Cleanup per-user audio states.
        foreach (var kvp in this.userAudioStates)
        {
            kvp.Value.Dispose();
        }
        this.userAudioStates.Clear();

        if (this.audioClient is not null)
        {
            this.audioClient.StreamCreated -= OnAudioStreamCreated;
            this.audioClient.StreamDestroyed -= OnAudioStreamDestroyed;
            this.audioClient.Disconnected -= OnAudioClientDisconnected;

            try
            {
                await this.audioClient.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogVoiceLeaveFailed(ex.Message);
            }

            this.audioClient.Dispose();
            this.audioClient = null;

            this.LogVoiceLeft();
        }

        if (this.sessionCts is not null)
        {
            try
            {
                this.sessionCts.Cancel();
            }
            catch (ObjectDisposedException) { }

            this.sessionCts.Dispose();
            this.sessionCts = null;
        }
    }

    private async Task CleanupAfterJoinFailureAsync()
    {
        if (this.voiceOut is { } pipeline)
        {
            this.voiceOut = null;
            try
            {
                await pipeline.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.LogVoiceLeaveFailed(ex.Message);
            }
        }

        if (this.turnArbiter is { } arbiter)
        {
            this.turnArbiter = null;
            arbiter.Dispose();
        }

        this.audioOutStream = null;
        if (this.audioClient is not null)
        {
            this.audioClient.Disconnected -= OnAudioClientDisconnected;
            this.audioClient = null;
        }

        if (this.sessionCts is not null)
        {
            try
            {
                this.sessionCts.Cancel();
            }
            catch (ObjectDisposedException) { }

            this.sessionCts.Dispose();
            this.sessionCts = null;
        }
    }

    // ── Receive loop (per-user audio streams) ────────────────────────

    private Task OnAudioStreamCreated(ulong userId, AudioInStream audioStream)
    {
        this.LogAudioStreamCreated(userId);

        var state = new UserAudioState(userId);
        this.userAudioStates[userId] = state;

        var ct = this.sessionCts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => ReceiveUserAudioAsync(userId, audioStream, state, ct), ct);

        return Task.CompletedTask;
    }

    private Task OnAudioStreamDestroyed(ulong userId)
    {
        this.LogAudioStreamDestroyed(userId);

        if (this.userAudioStates.TryRemove(userId, out var state))
        {
            _ = Task.Run(() => ProcessUserAudioAsync(state));
            state.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Receive loop for a single user. Always running while the user's audio stream exists.
    /// Manages <see cref="ListeningState"/> transitions and detects barge-in conditions.
    /// </summary>
    private async Task ReceiveUserAudioAsync(ulong userId, AudioInStream audioStream, UserAudioState state, CancellationToken ct)
    {
        // Discord.Net delivers 3840-byte stereo PCM frames (48kHz × 2 ch × 2 bytes × 20 ms).
        // We downmix to mono immediately so the rest of the pipeline (VAD, accumulator, STT
        // resample to 16 kHz) can stay mono.
        var stereoBuffer = new byte[OpusFrameBytes];
        var silenceTimeoutMs = this.config.SilenceTimeoutMs;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Poll the audio stream with a short wall-clock budget. If Discord
                // drops incoming frames (e.g. DAVE decryption failures — the
                // "Malformed Frame" pattern), ReadAsync would otherwise block
                // indefinitely waiting for a valid frame, and our silence timer
                // would only fire whenever one happens to slip through. With a
                // poll timeout we fall through to the silence check on every
                // tick so end-of-utterance detection is bounded by wall clock.
                int bytesRead = 0;
                using (var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    pollCts.CancelAfter(ReceivePollTimeoutMs);
                    try
                    {
                        bytesRead = await audioStream.ReadAsync(
                            stereoBuffer.AsMemory(0, OpusFrameBytes), pollCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Poll budget elapsed without a frame; fall through so the
                        // silence check below still runs.
                    }
                }

                var isSpeech = false;

                if (bytesRead > 0)
                {
                    var monoFrame = AudioConverter.StereoToMono(stereoBuffer.AsSpan(0, bytesRead));
                    var frameData = monoFrame.AsSpan();
                    var rms = AudioConverter.ComputeRms(frameData);
                    isSpeech = rms >= VoiceActivityThreshold;

                    if (isSpeech)
                    {
                        state.LastSpeechTime = DateTimeOffset.UtcNow;

                        if (!state.HasSpeech)
                        {
                            state.HasSpeech = true;
                            state.CurrentUtteranceId = NewUtteranceId();
                            state.SpeechStartTime = state.LastSpeechTime;
                            state.DaveStatsAtOnset = this.daveStats?.Take() ?? default;
                            this.listeningState = ListeningState.Hearing;
                            this.LogSpeechOnset(state.CurrentUtteranceId, state.UserId, rms);
                        }

                        // Hold-back: speech resumed while a tentative commit was
                        // parked in its grace window. Cancel the pending dispatch
                        // and continue the SAME utterance — the accumulator below
                        // keeps appending, so the resumed thought is re-absorbed.
                        // One grace per turn (GraceUsed): a later pause dispatches.
                        if (state.GracePending)
                        {
                            state.GracePending = false;
                            state.GraceUsed = true;
                            this.LogHoldBackReabsorbed(state.CurrentUtteranceId);
                        }

                        // Turn-arbiter speech-onset trigger. Only after the
                        // onset guard — sustained speech ≥ BargeInOnsetGuardMs —
                        // so single-frame coughs/claps don't fire a barge-in.
                        // Fired once per utterance, fire-and-forget so the
                        // receive loop is never blocked on the arbiter gate /
                        // classifier (the arbiter serializes phase transitions
                        // itself and gates on EnableBargeIn internally).
                        if (!state.OnsetSignalledToArbiter
                            && this.wizardSession is null
                            && this.turnArbiter is { } arbiter
                            && state.SpeechStartTime is { } onsetStart
                            && (state.LastSpeechTime - onsetStart).TotalMilliseconds
                                >= this.config.BargeInOnsetGuardMs)
                        {
                            state.OnsetSignalledToArbiter = true;

                            // Best available partial transcript: the live
                            // streaming-STT partial when streaming is active,
                            // otherwise "" (the heuristic treats an empty
                            // partial as Unsure→Real, the safe default — and
                            // there is no LLM tier on this path to escalate to;
                            // see the classifier construction in EnsureConnected).
                            var partial = this.sttCoordinator.IsStreaming
                                ? (this.streamingStt?.GetPartialResult() ?? string.Empty)
                                : string.Empty;
                            var pEou = state.LastPEou;

                            _ = Task.Run(
                                () => arbiter.OnUserSpeechOnsetAsync(partial, pEou, ct),
                                ct);
                        }
                    }

                    // Accumulate audio when speech has been detected. In streaming
                    // mode we also feed each frame to the streaming STT (resampled
                    // 48kHz → 16kHz) so partials can stabilize before silence hits.
                    if (state.HasSpeech)
                    {
                        state.AudioAccumulator.Write(frameData);

                        if (this.sttCoordinator.IsStreaming)
                        {
                            var frame16k = AudioConverter.Resample(
                                monoFrame,
                                AudioFormat.Discord.SampleRate,
                                AudioFormat.Whisper.SampleRate);
                            this.sttCoordinator.FeedFrame16k(frame16k);
                        }
                    }
                }

                // End-of-utterance check — runs on EVERY tick (frame arrived or
                // poll timed out), so silence is detected by wall clock, not by
                // the arrival cadence of valid frames.
                if (state.HasSpeech && !isSpeech)
                {
                    var now = DateTimeOffset.UtcNow;
                    var silenceElapsedMs = (int)(now - state.LastSpeechTime).TotalMilliseconds;

                    if (this.turnDetectorActive)
                    {
                        await RefreshTurnDetectorAsync(state, now, ct).ConfigureAwait(false);
                    }

                    var threshold = this.turnDetectorActive && this.turnDetector is not null
                        ? this.turnDetector.GetThreshold("en")
                        : 0f;
                    var decision = EndOfTurnDecision.Decide(
                        silenceElapsedMs,
                        silenceTimeoutMs,
                        this.turnDetectorActive,
                        state.LastPEou,
                        threshold,
                        lowConfidenceThreshold: LowConfidenceEouThreshold,
                        maxSilenceTimeoutMs: MaxSilenceTimeoutMs);

                    if (decision.Commit)
                    {
                        var accumulatedMs = (int)(state.AudioAccumulator.Length * 1000L
                            / AudioFormat.Discord.BytesPerSecond);

                        HoldBackOutcome holdBack;
                        if (state.GracePending)
                        {
                            // A tentative commit is parked. Speech resuming is
                            // handled in the isSpeech path (clears GracePending,
                            // sets GraceUsed) — so here the user is still silent.
                            // Pass the live decision confidence: when the hard
                            // silence ceiling fires, EndOfTurnDecision returns
                            // Confident → HoldBackDecision dispatches structurally
                            // (the "quiet user never strands" invariant), not by
                            // relying on elapsed grace having grown large enough.
                            var graceElapsedMs =
                                (int)(now - state.GraceOpenedUtc).TotalMilliseconds;
                            holdBack = HoldBackDecision.Decide(
                                decision.Confidence,
                                state.GraceUsed,
                                accumulatedMs,
                                MaxUtteranceMs,
                                graceElapsedMs,
                                HoldBackGraceMs);
                        }
                        else
                        {
                            holdBack = HoldBackDecision.Decide(
                                decision.Confidence,
                                state.GraceUsed,
                                accumulatedMs,
                                MaxUtteranceMs,
                                graceElapsedMs: 0,
                                HoldBackGraceMs);

                            if (holdBack == HoldBackOutcome.Hold)
                            {
                                state.GracePending = true;
                                state.GraceOpenedUtc = now;
                                this.LogHoldBackStarted(
                                    state.CurrentUtteranceId, accumulatedMs);
                            }
                        }

                        if (holdBack == HoldBackOutcome.Hold)
                        {
                            // Keep the receive loop running; do NOT dispatch.
                            continue;
                        }

                        // ---- finalize: dispatch this utterance ----
                        state.GracePending = false;

                        var speechDurationMs = state.SpeechStartTime is { } start
                            ? (int)(state.LastSpeechTime - start).TotalMilliseconds
                            : 0;
                        this.LogCommitDecision(
                            state.CurrentUtteranceId,
                            decision.Reason,
                            silenceElapsedMs,
                            state.LastPEou,
                            threshold,
                            speechDurationMs);

                        // Per-utterance DAVE drop report: how many decrypt/etc.
                        // events fired on this specific turn. Correlates with
                        // the global DAVE stats summary by wall-clock time.
                        if (this.daveStats is not null)
                        {
                            var daveDelta = this.daveStats.Take().Delta(state.DaveStatsAtOnset);
                            if (daveDelta.Total > 0)
                            {
                                this.LogDaveDropsForTurn(
                                    state.CurrentUtteranceId,
                                    daveDelta.DecryptFailure,
                                    daveDelta.MissingKeyRatchet,
                                    daveDelta.InvalidNonce,
                                    daveDelta.MalformedFrame,
                                    daveDelta.UnknownSsrc,
                                    daveDelta.UnknownUser,
                                    daveDelta.MlsFailure);
                            }
                        }

                        // Phase transition fires only on a real dispatch — not on
                        // a held tentative commit (which may be re-absorbed).
                        this.agentFirstAudioSignalled = false;
                        this.turnArbiter?.OnUserCommit();

                        this.listeningState = ListeningState.Processing;
                        await ProcessUserAudioAsync(state).ConfigureAwait(false);
                        this.listeningState = ListeningState.Idle;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            this.LogAudioReceiveFailed(userId, ex.Message);
        }
    }

    /// <summary>
    /// Update <see cref="UserAudioState.LastPEou"/> from the turn detector,
    /// at most once per <see cref="TurnDetectorDebounceMs"/> ms and only when
    /// the streaming STT partial transcript has actually changed. Skipped if
    /// the streaming STT isn't producing anything yet or the detector isn't
    /// loaded.
    /// </summary>
    private async Task RefreshTurnDetectorAsync(UserAudioState state, DateTimeOffset now, CancellationToken ct)
    {
        if (this.streamingStt is null || this.turnDetector is null)
        {
            return;
        }

        var sinceLastRun = (now - state.LastDetectorRun).TotalMilliseconds;
        if (sinceLastRun < TurnDetectorDebounceMs)
        {
            return;
        }

        var partial = this.streamingStt.GetPartialResult();
        if (string.IsNullOrWhiteSpace(partial))
        {
            return;
        }

        // Skip re-inference when the partial hasn't changed — cheap win since
        // the streaming STT emits the same text between transcription passes.
        if (string.Equals(partial, state.LastDetectorInput, StringComparison.Ordinal))
        {
            state.LastDetectorRun = now;
            return;
        }

        try
        {
            var detectorStart = DateTimeOffset.UtcNow;
            var p = await this.turnDetector.PredictEndOfTurnAsync(
                [new TurnDetectorMessage("user", partial)],
                language: "en",
                ct).ConfigureAwait(false);
            var detectorMs = (int)(DateTimeOffset.UtcNow - detectorStart).TotalMilliseconds;
            state.LastPEou = p;
            state.LastDetectorInput = partial;
            this.LogTurnDetectorRefreshed(state.CurrentUtteranceId, p, detectorMs, partial.Length);
        }
#pragma warning disable CA1031 // Broad catch — detector errors must never stop audio reception.
        catch (Exception ex)
        {
            this.LogTurnDetectorFailed(ex.Message);
        }
#pragma warning restore CA1031
        finally
        {
            state.LastDetectorRun = now;
        }
    }

    /// <summary>
    /// Process accumulated user audio: resample → STT → dispatch to agent.
    /// </summary>
    private async Task ProcessUserAudioAsync(UserAudioState state)
    {
        if (!state.HasSpeech || state.AudioAccumulator.Length == 0)
        {
            return;
        }

        // Snapshot the utterance id before Reset() clears it — we want it on
        // the transcription logs for end-to-end correlation.
        var utteranceId = state.CurrentUtteranceId;
        var audioData = state.AudioAccumulator.ToArray();
        state.Reset();

        // Check overall energy
        var overallRms = AudioConverter.ComputeRms(audioData);
        if (overallRms < VoiceActivityThreshold)
        {
            this.LogAudioTooQuiet(state.UserId, overallRms);
            return;
        }

        // Resample 48kHz → 16kHz for Whisper STT. In streaming mode the full
        // PCM is still passed to the coordinator as a fallback argument even
        // though the streaming path won't use it — the coordinator needs it
        // only in batch mode.
        var pcm16k = AudioConverter.Resample(audioData, AudioFormat.Discord.SampleRate, AudioFormat.Whisper.SampleRate);

        // Kick off speaker verification in parallel with STT so the gate adds
        // no latency on the critical path. See spec section "Verification pipeline".
        var pcm16kShorts = AudioConverter.BytesToShorts(pcm16k);

        // Channel wizard lock: while the enrollment wizard owns this channel,
        // committed utterances feed capture only — the verify gate, STT,
        // recorder tap, and agent dispatch are all bypassed.
        if (WizardLockDecision.Route(this.wizardSession is not null) == UtteranceRoute.EnrollmentCapture)
        {
            await this.HandleEnrollmentUtteranceAsync(pcm16kShorts).ConfigureAwait(false);
            return;
        }

        var verifyTask = DiscordVoiceGate.EvaluateAsync(
            this.speakerVerifier,
            this.config.TenantId,
            pcm16kShorts,
            CancellationToken.None,
            this.verificationMetrics);

        try
        {
            this.LogWhisperStart(utteranceId, state.UserId, pcm16k.Length, this.sttCoordinator.IsStreaming);
            var whisperStart = DateTimeOffset.UtcNow;
            var transcription = await this.sttCoordinator.FinalizeAsync(pcm16k).ConfigureAwait(false);
            var whisperMs = (int)(DateTimeOffset.UtcNow - whisperStart).TotalMilliseconds;

            // Tap to the runtime recorder if a session is active for this
            // Discord voice channel. Best-effort observer — never affects
            // voice-path latency or correctness; one ConcurrentDictionary
            // lookup-miss per committed utterance when no session is active.
            // The controller pre-pads inter-utterance silence so session.wav
            // stays aligned with the session timeline.
            if (this.config.Recorder is { } rec)
            {
                try
                {
                    rec.RecordCommittedUtterance(
                        Cortex.Contained.Contracts.Recording.ChannelKey.ForDiscord(this.config.VoiceChannelId),
                        pcm16k,
                        utteranceId,
                        transcription ?? string.Empty,
                        reason: "discord-commit");
                }
                catch (Exception captureEx)
                {
                    this.LogVoiceCaptureFailed(utteranceId, captureEx.Message);
                }
            }

            // Always observe verifyTask so any future cancellation/exception
            // surfaces deterministically. The gate helper is fail-open, so
            // observing here is cheap.
            var gateDecision = await verifyTask.ConfigureAwait(false);

            var displayName = this.client.GetUser(state.UserId) is { } u
                ? (u.GlobalName ?? u.Username)
                : state.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Run language detection BEFORE dispatch so the agent's reply
            // (synthesized later via the TTS pipeline) reads the freshly
            // updated sticky current language from the store.
            if (!string.IsNullOrWhiteSpace(transcription)
                && this.config.LanguageDetector is { } userInDetector
                && this.config.LanguageStore is { } userInStore)
            {
                var userInResult = userInStore.UpdateFromDetection(
                    this.LanguageChannelKey,
                    transcription,
                    userInDetector,
                    this.config.LanguageSwitchThresholds);
                this.LogLangDetect("user-in", this.LanguageChannelKey, userInResult);
            }

            var dispatchOutcome = await TryDispatchUtteranceAsync(
                this.logger,
                utteranceId,
                state.UserId,
                whisperMs,
                transcription,
                gateDecision,
                this.config.TenantId,
                displayName,
                this.onTranscription).ConfigureAwait(false);

            // Phase transition: the user turn was actually sent to the LLM.
            // Only Dispatched means the agent now owns the turn (Thinking).
            if (dispatchOutcome == UtteranceDispatchOutcome.Dispatched)
            {
                this.turnArbiter?.OnAgentSentToLlm();
            }
        }
        catch (Exception ex)
        {
            this.LogTranscriptionFailed(state.UserId, ex.Message);
        }
    }

    // ── Turn-arbiter actuator callbacks ──────────────────────────────

    /// <summary>
    /// The conversation id this voice session uses with the Agent Host. Must be
    /// identical to the id stamped on inbound voice transcripts in
    /// <see cref="TryDispatchUtteranceAsync"/> so the Agent Host's barge-in
    /// history edit lands on the same session the user is talking in.
    /// </summary>
    internal static string VoiceConversationId(string tenantId) => $"discord-voice-{tenantId}";

    /// <summary>
    /// Builds the <see cref="VoiceTurnArbiter"/> actuator callbacks. Static so
    /// tests can drive the cross-process commit/abort wiring without a full
    /// Discord/audio handler (mirrors <see cref="TryDispatchUtteranceAsync"/>).
    /// </summary>
    /// <param name="conversationId">The voice session conversation id (see
    /// <see cref="VoiceConversationId"/>) — threaded into every cross-process
    /// call so the Agent Host edits the correct session.</param>
    /// <param name="onTurnInterrupted">Bridge → Agent barge-in commit (records
    /// the truncated assistant turn). Best-effort: one retry, then a warning;
    /// never throws out of the callback (a lost notification leaves that single
    /// turn's history optimistic — recoverable, not catastrophic).</param>
    /// <param name="onAbortGeneration">Bridge → Agent cross-process abort of the
    /// in-flight LLM turn for hold-back. Best-effort + warning; never throws.</param>
    internal static VoiceTurnArbiterCallbacks BuildArbiterCallbacks(
        string conversationId,
        Func<Task<PlaybackProgress>> stopPlayback,
        Action<string> reenqueueSentence,
        Func<string, float, CancellationToken, Task<InterruptClass>> classify,
        Func<TurnInterruptedNotification, Task> onTurnInterrupted,
        Func<string, Task> onAbortGeneration,
        ILogger logger)
    {
        return new VoiceTurnArbiterCallbacks
        {
            StopPlayback = stopPlayback,
            ReenqueueSentence = reenqueueSentence,
            Classify = classify,

            // Hold-back actuator (phases Committed/Thinking): the user kept
            // talking after the end-of-turn detector committed, so the
            // premature LLM turn must be cancelled in the Agent Host (a separate
            // process — no local per-turn CTS exists Bridge-side; sessionCts is
            // session-wide and must not be touched here) before the re-absorbed
            // audio re-runs end-of-turn detection as one continuous turn.
            CancelGenerationAndReabsorb = async reason =>
            {
                LogStaticHoldBackCancel(logger, reason);
                try
                {
                    await onAbortGeneration(conversationId).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // best-effort cross-process abort; never throw out of the actuator
                catch (Exception ex)
                {
                    try
                    {
                        await onAbortGeneration(conversationId).ConfigureAwait(false);
                    }
                    catch (Exception retryEx)
                    {
                        LogStaticHoldBackAbortFailed(logger, conversationId, retryEx.Message);
                        return;
                    }

                    LogStaticHoldBackAbortRetried(logger, conversationId, ex.Message);
                }
#pragma warning restore CA1031
            },

            // Barge-in commit actuator (phase Speaking, classifier said Real):
            // playback was already stopped; record what the user actually heard
            // (playedText, already ending with "…") as the truncated assistant
            // turn Agent-Host-side, then the interrupting utterance is taken as
            // the next user turn.
            CommitInterrupt = async progress =>
            {
                var playedText = PlaybackTruncation.BuildPlayedText(progress);
                var notification = new TurnInterruptedNotification
                {
                    ConversationId = conversationId,
                    PlayedText = playedText,
                };
                LogStaticBargeInCommitted(logger, playedText.Length);
                try
                {
                    await onTurnInterrupted(notification).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // best-effort; a lost notification only makes one turn's history optimistic
                catch (Exception ex)
                {
                    try
                    {
                        await onTurnInterrupted(notification).ConfigureAwait(false);
                    }
                    catch (Exception retryEx)
                    {
                        LogStaticBargeInSendFailed(logger, conversationId, retryEx.Message);
                        return;
                    }

                    LogStaticBargeInSendRetried(logger, conversationId, ex.Message);
                }
#pragma warning restore CA1031
            },
        };
    }

    // ── Per-user audio state ─────────────────────────────────────────

    private sealed class UserAudioState : IDisposable
    {
        public ulong UserId { get; }
        public MemoryStream AudioAccumulator { get; } = new();
        public DateTimeOffset LastSpeechTime { get; set; }
        public bool HasSpeech { get; set; }

        /// <summary>
        /// Short random id (8 hex chars) stamped on every log line between
        /// speech onset and commit so all telemetry for a single user-turn can
        /// be grepped out of the log with one filter. Empty between utterances.
        /// </summary>
        public string CurrentUtteranceId { get; set; } = string.Empty;

        /// <summary>
        /// When VAD first detected speech for the current utterance — used to
        /// compute total speech duration at commit time.
        /// </summary>
        public DateTimeOffset? SpeechStartTime { get; set; }

        /// <summary>
        /// Snapshot of DAVE counters at speech onset. At commit we diff against
        /// the current snapshot to see how many decrypt / malformed / etc.
        /// events happened specifically during this user turn.
        /// </summary>
        public DaveEventStats.Snapshot DaveStatsAtOnset { get; set; }

        // Turn-detector cache: last P(EOU) observed for this user during the
        // current silence window, plus when we last ran inference. Reset on
        // each new utterance so stale values from a prior turn don't trigger
        // a premature commit.
        public float LastPEou { get; set; }
        public DateTimeOffset LastDetectorRun { get; set; } = DateTimeOffset.MinValue;
        public string LastDetectorInput { get; set; } = string.Empty;

        /// <summary>
        /// True once the turn-arbiter speech-onset event has been fired for the
        /// current utterance (after the sustained-speech onset guard). Ensures
        /// the arbiter is signalled exactly once per utterance, not per frame.
        /// </summary>
        public bool OnsetSignalledToArbiter { get; set; }

        /// <summary>
        /// Hold-back grace state. On a <see cref="CommitConfidence.Tentative"/>
        /// commit the receive loop opens a short grace window instead of
        /// dispatching; resumed speech is re-absorbed into this same utterance.
        /// </summary>
        public bool GracePending { get; set; }

        /// <summary>UTC instant the grace window was opened. Elapsed grace is
        /// measured directly from this — independent of the window length, so it
        /// stays correct if the window duration ever becomes configurable.</summary>
        public DateTimeOffset GraceOpenedUtc { get; set; }

        /// <summary>True once this turn already consumed its single grace
        /// window (a resumed thought was re-absorbed). Blocks a second hold so
        /// a later pause dispatches immediately.</summary>
        public bool GraceUsed { get; set; }

        public UserAudioState(ulong userId) => UserId = userId;

        public void Reset()
        {
            AudioAccumulator.SetLength(0);
            HasSpeech = false;
            CurrentUtteranceId = string.Empty;
            SpeechStartTime = null;
            DaveStatsAtOnset = default;
            LastPEou = 0f;
            LastDetectorRun = DateTimeOffset.MinValue;
            LastDetectorInput = string.Empty;
            OnsetSignalledToArbiter = false;
            GracePending = false;
            GraceOpenedUtc = DateTimeOffset.MinValue;
            GraceUsed = false;
        }

        public void Dispose() => AudioAccumulator.Dispose();
    }

    /// <summary>
    /// Generate a terse 8-hex-char correlation id for an utterance. Short
    /// enough to keep log lines readable; entropy is plenty for a single
    /// voice channel session.
    /// </summary>
    private static string NewUtteranceId()
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── LoggerMessage source-generated methods ───────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-capture: tap failed for utt={UtteranceId}: {Error}")]
    private partial void LogVoiceCaptureFailed(string utteranceId, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {Username} joined voice channel {VoiceChannelId}")]
    private partial void LogUserJoinedVoice(string username, ulong voiceChannelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {Username} left voice channel {VoiceChannelId}")]
    private partial void LogUserLeftVoice(string username, ulong voiceChannelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Joining voice channel {ChannelName} ({VoiceChannelId})")]
    private partial void LogVoiceJoining(string channelName, ulong voiceChannelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Joined voice channel {ChannelName} ({VoiceChannelId})")]
    private partial void LogVoiceJoined(string channelName, ulong voiceChannelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice channel {ChannelName} bot permissions: Connect={CanConnect}, Speak={CanSpeak}, UseVAD={CanUseVAD}")]
    private partial void LogVoicePermissions(string channelName, bool canConnect, bool canSpeak, bool canUseVAD);

    [LoggerMessage(Level = LogLevel.Error, Message = "Voice channel {VoiceChannelId} join timed out — check that libsodium native library is present and network allows UDP to Discord voice servers")]
    private partial void LogVoiceJoinTimeout(ulong voiceChannelId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to join voice channel {VoiceChannelId}")]
    private partial void LogVoiceJoinException(ulong voiceChannelId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to join voice channel: {ErrorMessage}")]
    private partial void LogVoiceJoinFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Proactive DM skipped — Discord user {LinkedUserId} not found (not in a shared guild or blocked the bot)")]
    private partial void LogProactiveDmUserMissing(ulong linkedUserId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Proactive voice-message TTS synthesis failed — falling back to plain-text DM: {ErrorMessage}")]
    private partial void LogProactiveVoiceDmSynthesisFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Left voice channel")]
    private partial void LogVoiceLeft();

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-in: connection lost ({Reason}) — recovering if user still present")]
    private partial void LogVoiceConnectionLost(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: connection recovering trigger={Trigger}")]
    private partial void LogVoiceRecovering(string trigger);

    [LoggerMessage(Level = LogLevel.Error, Message = "voice-in: connection recovery failed trigger={Trigger}: {Error}")]
    private partial void LogVoiceRecoveryFailed(string trigger, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to leave voice channel: {ErrorMessage}")]
    private partial void LogVoiceLeaveFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Audio stream created for user {UserId}")]
    private partial void LogAudioStreamCreated(ulong userId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Audio stream destroyed for user {UserId}")]
    private partial void LogAudioStreamDestroyed(ulong userId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audio receive failed for user {UserId}: {ErrorMessage}")]
    private partial void LogAudioReceiveFailed(ulong userId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Audio from user {UserId} too quiet (RMS={Rms:F4}), discarding")]
    private partial void LogAudioTooQuiet(ulong userId, float rms);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Transcription empty for user {UserId}")]
    private partial void LogTranscriptionEmpty(ulong userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcribed voice from user {UserId} ({CharCount} chars)")]
    private partial void LogTranscriptionDone(ulong userId, int charCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice state REST query failed; falling back to gateway cache: {Reason}")]
    private partial void LogVoiceStateRestFallback(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Transcription failed for user {UserId}: {ErrorMessage}")]
    private partial void LogTranscriptionFailed(ulong userId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice greeting skipped: {Reason}")]
    private partial void LogVoiceGreetingSkipped(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sentence enqueued for TTS ({CharCount} chars, {GapMs} ms since previous sentence)")]
    private partial void LogSentenceEnqueued(int charCount, int gapMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Turn detector inference failed: {ErrorMessage}")]
    private partial void LogTurnDetectorFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: barge-in classifier mode HeuristicPlusLlm is configured but the LLM tier is unavailable on the Discord/Bridge path by architecture (the LLM client lives in the Agent Host container); running heuristic-only — Unsure resolves to Real (the safe default)")]
    private partial void LogBargeInLlmTierUnavailable();

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-enroll-wizard: started tenant={TenantId}")]
    private partial void LogWizardStarted(string tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-enroll-wizard: completed tenant={TenantId}")]
    private partial void LogWizardCompleted(string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-enroll-wizard: timed out tenant={TenantId}")]
    private partial void LogWizardTimedOut(string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-enroll-wizard: unavailable (no embedder/submit) tenant={TenantId}")]
    private partial void LogWizardUnavailable(string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-enroll-wizard: capture failed tenant={TenantId} error={Error}")]
    private partial void LogWizardCaptureFailed(string tenantId, string error);

    // ── Turn-arbitration actuator diagnostics ────────────────────────
    //
    // Static (take ILogger) so BuildArbiterCallbacks — itself static for
    // testability, mirroring TryDispatchUtteranceAsync — can emit them.

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: hold-back cancel ({Reason}) — aborting in-flight Agent generation")]
    private static partial void LogStaticHoldBackCancel(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-in: hold-back abort retried after error conv={ConversationId}: {ErrorMessage}")]
    private static partial void LogStaticHoldBackAbortRetried(ILogger logger, string conversationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-in: hold-back abort failed (both attempts) conv={ConversationId}: {ErrorMessage}")]
    private static partial void LogStaticHoldBackAbortFailed(ILogger logger, string conversationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: barge-in committed, playedText={PlayedTextChars} chars — sending OnTurnInterrupted")]
    private static partial void LogStaticBargeInCommitted(ILogger logger, int playedTextChars);

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-in: OnTurnInterrupted retried after error conv={ConversationId}: {ErrorMessage}")]
    private static partial void LogStaticBargeInSendRetried(ILogger logger, string conversationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-in: OnTurnInterrupted failed (both attempts) conv={ConversationId}: {ErrorMessage} — that turn's Agent history stays optimistic")]
    private static partial void LogStaticBargeInSendFailed(ILogger logger, string conversationId, string errorMessage);

    // ── Voice output diagnostics (temporary — tracking a silent-after-first-message bug) ──
    [LoggerMessage(Level = LogLevel.Information, Message = "voice-out: AcceptTextChunk received {Chars} chars")]
    private partial void LogAcceptTextChunk(int chars);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-out: FlushAccumulator drained {DrainedCount} sentences")]
    private partial void LogFlushAccumulator(int drainedCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "lang-detect src={Source} channel={Channel} text_len={TextLength} top={Candidate} conf={ConfTop:F2} current_before={CurrentBefore} margin={Margin:F2} decision={Decision} current_after={CurrentAfter}")]
    private partial void LogLangDetectMessage(string source, string channel, int textLength, string candidate, double confTop, string currentBefore, double margin, string decision, string currentAfter);

    private void LogLangDetect(string source, string channel, LanguageUpdateResult r)
        => this.LogLangDetectMessage(source, channel, r.TextLength, r.Candidate, r.ConfTop, r.CurrentBefore, r.ConfTop - r.ConfCurrentBefore, r.Switched ? "switch" : "keep", r.CurrentAfter);

    // ── Voice input telemetry (utterance-scoped correlation id) ────────────
    //
    // Each user turn is tagged with an 8-hex-char utterance id from the moment
    // VAD detects speech onset through the end of Whisper transcription. All
    // lines below carry `utt={id}` so a full turn can be pulled out of the log
    // with `grep 'utt=<id>'`. After transcription finishes, the agent's reply
    // has no natural id to thread through — correlate by timestamp.

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: speech onset utt={UtteranceId} user={UserId} rms={Rms:F4}")]
    private partial void LogSpeechOnset(string utteranceId, ulong userId, float rms);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: committed utt={UtteranceId} reason={Reason} silenceMs={SilenceMs} pEou={PEou:F3} threshold={Threshold:F3} speechMs={SpeechMs}")]
    private partial void LogCommitDecision(string utteranceId, CommitReason reason, int silenceMs, float pEou, float threshold, int speechMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: hold-back grace opened utt={UtteranceId} accumulatedMs={AccumulatedMs}")]
    private partial void LogHoldBackStarted(string utteranceId, int accumulatedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: hold-back re-absorbed resumed speech utt={UtteranceId}")]
    private partial void LogHoldBackReabsorbed(string utteranceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: whisper start utt={UtteranceId} user={UserId} bytes16k={Bytes16k} streaming={Streaming}")]
    private partial void LogWhisperStart(string utteranceId, ulong userId, int bytes16k, bool streaming);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: whisper done utt={UtteranceId} user={UserId} ms={Ms} chars={Chars}")]
    private partial void LogWhisperDone(string utteranceId, ulong userId, int ms, int chars);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: whisper done (empty) utt={UtteranceId} user={UserId} ms={Ms}")]
    private partial void LogWhisperDoneEmpty(string utteranceId, ulong userId, int ms);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: turn detector refreshed utt={UtteranceId} pEou={PEou:F3} ms={Ms} partialChars={PartialChars}")]
    private partial void LogTurnDetectorRefreshed(string utteranceId, float pEou, int ms, int partialChars);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: DAVE drops for turn utt={UtteranceId} decryptFail={DecryptFail} missingKeyRatchet={MissingKeyRatchet} invalidNonce={InvalidNonce} malformed={Malformed} unknownSsrc={UnknownSsrc} unknownUser={UnknownUser} mlsFail={MlsFail}")]
    private partial void LogDaveDropsForTurn(string utteranceId, long decryptFail, long missingKeyRatchet, long invalidNonce, long malformed, long unknownSsrc, long unknownUser, long mlsFail);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: speaker-id rejected utt={UtteranceId} tenant={TenantId} score={Score:F3}")]
    private partial void LogSpeakerIdRejected(string utteranceId, string tenantId, float score);

    // ── Post-STT dispatch (extracted as internal static for direct testing) ──

    /// <summary>
    /// Outcome of <see cref="TryDispatchUtteranceAsync"/>. Visible to tests via
    /// the existing <c>InternalsVisibleTo</c> entry.
    /// </summary>
    internal enum UtteranceDispatchOutcome
    {
        Dispatched,
        SkippedEmpty,
        SkippedRejected,
    }

    /// <summary>
    /// Decides whether to dispatch the transcript to the agent and, if so,
    /// builds the inbound message and calls <paramref name="onTranscription"/>.
    /// Static so tests can drive it without constructing the full handler.
    /// </summary>
    internal static async Task<UtteranceDispatchOutcome> TryDispatchUtteranceAsync(
        ILogger logger,
        string utteranceId,
        ulong userId,
        int whisperMs,
        string? transcription,
        VoiceGateDecision gateDecision,
        string tenantId,
        string displayName,
        Func<InboundMessage, Task> onTranscription)
    {
        if (string.IsNullOrWhiteSpace(transcription))
        {
            LogStaticWhisperDoneEmpty(logger, utteranceId, userId, whisperMs);
            LogStaticTranscriptionEmpty(logger, userId);
            return UtteranceDispatchOutcome.SkippedEmpty;
        }

        if (!gateDecision.PassesTranscript)
        {
            var rejectScore = gateDecision.Result is VerificationResult.Reject reject ? reject.Score : 0f;
            LogStaticSpeakerIdRejected(logger, utteranceId, tenantId, rejectScore);
            return UtteranceDispatchOutcome.SkippedRejected;
        }

        LogStaticWhisperDone(logger, utteranceId, userId, whisperMs, transcription.Length);
        LogStaticTranscriptionDone(logger, userId, transcription.Length);

        var userIdStr = userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var inbound = new InboundMessage
        {
            MessageId = $"voice-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            ConversationId = VoiceConversationId(tenantId),
            ChannelId = DiscordChannel.VoiceChannelId,
            ChannelType = ChannelType.Discord,
            Sender = new SenderInfo
            {
                Id = userIdStr,
                DisplayName = displayName,
            },
            Content = new MessageContent
            {
                Text = transcription,
                IsMarkdown = false,
            },
            Timestamp = DateTimeOffset.UtcNow,
            IsGroup = true,
            Properties = new Dictionary<string, string>
            {
                ["voice"] = "true",
                ["tenantId"] = tenantId,
            },
        };

        await onTranscription(inbound).ConfigureAwait(false);
        return UtteranceDispatchOutcome.Dispatched;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: whisper done (empty) utt={UtteranceId} user={UserId} ms={Ms}")]
    private static partial void LogStaticWhisperDoneEmpty(ILogger logger, string utteranceId, ulong userId, int ms);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: transcription empty user={UserId}")]
    private static partial void LogStaticTranscriptionEmpty(ILogger logger, ulong userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: speaker-id rejected utt={UtteranceId} tenant={TenantId} score={Score:F3}")]
    private static partial void LogStaticSpeakerIdRejected(ILogger logger, string utteranceId, string tenantId, float score);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: whisper done utt={UtteranceId} user={UserId} ms={Ms} chars={Chars}")]
    private static partial void LogStaticWhisperDone(ILogger logger, string utteranceId, ulong userId, int ms, int chars);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-in: transcription done user={UserId} chars={Chars}")]
    private static partial void LogStaticTranscriptionDone(ILogger logger, ulong userId, int chars);
}
