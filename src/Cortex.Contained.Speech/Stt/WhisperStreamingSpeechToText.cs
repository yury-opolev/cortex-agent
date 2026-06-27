using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Speech.Stt;

/// <summary>
/// Streaming speech-to-text wrapper that stabilizes partial transcriptions
/// using the LocalAgreement-2 policy on top of any batch <see cref="ISpeechToText"/>
/// implementation (typically <see cref="WhisperSpeechToText"/>).
/// </summary>
/// <remarks>
/// <para>
/// LocalAgreement-2: each time we re-transcribe the audio buffer we get a new
/// output string. Tokens that appear (at the same word position) in two
/// consecutive outputs are considered <em>committed</em> — they won't change.
/// Tokens beyond the common prefix are <em>provisional</em> and may still shift
/// in later passes.
/// </para>
/// <para>
/// The wrapper owns no Whisper state of its own — it delegates all inference
/// to an injected <see cref="ISpeechToText"/>. This keeps the class testable
/// with a scripted fake and avoids duplicating model loading.
/// </para>
/// </remarks>
public sealed partial class WhisperStreamingSpeechToText : IStreamingSpeechToText
{
    /// <summary>
    /// Default auto-transcription threshold: 0 (disabled). With no consumer of
    /// partial results, background passes only add CPU load — each pass re-runs
    /// Whisper on the full audio buffer from the start of the utterance, which
    /// is very expensive on CPU. Only enable (by passing a positive threshold)
    /// when something (e.g. the turn detector) actually polls
    /// <see cref="GetPartialResult"/> during speech.
    /// </summary>
    public const int DefaultAutoTranscribeThresholdBytes = 0;

    private readonly ISpeechToText batchStt;
    private readonly ILogger<WhisperStreamingSpeechToText> logger;
    private readonly int autoTranscribeThresholdBytes;
    private bool disposed;

    // State protected by stateLock
    private readonly Lock stateLock = new();
    private string committedText = string.Empty;
    private string provisionalText = string.Empty;
    private string lastFullOutput = string.Empty;
    private int bytesAtLastTrigger;

    // Trim-by-committed-token state. Tracks how far into audioBuffer we've
    // already permanently committed text for, so subsequent partial passes
    // only re-transcribe the unstable tail. The full audioBuffer is preserved
    // unchanged so GetFinalResultAsync still does an authoritative pass on
    // the complete utterance.
    private int bytesCommittedThrough;
    private string committedTextAccumulator = string.Empty;

    /// <summary>
    /// Bumped on every <see cref="Reset"/>. Background passes snapshot it on
    /// entry and abort their write-back if it has changed — guards against a
    /// Reset that lands between snapshot and commit corrupting the freshly-
    /// cleared state with stale results.
    /// </summary>
    private int resetEpoch;

    // Audio buffer (protected by stateLock). Holds all PCM since last Reset.
    private readonly List<byte> audioBuffer = new();

    // Serializes calls into the underlying batch Whisper to match its internal gate.
    private readonly SemaphoreSlim transcribeGate = new(1, 1);

    // Most recently kicked-off background transcription task (exposed to tests
    // for deterministic awaiting; production code uses GetFinalResultAsync).
    private Task backgroundTranscription = Task.CompletedTask;

    public WhisperStreamingSpeechToText(
        ISpeechToText batchStt,
        ILogger<WhisperStreamingSpeechToText> logger,
        int autoTranscribeThresholdBytes = DefaultAutoTranscribeThresholdBytes)
    {
        this.batchStt = batchStt ?? throw new ArgumentNullException(nameof(batchStt));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (autoTranscribeThresholdBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(autoTranscribeThresholdBytes), "Must be 0 (disabled) or positive.");
        }
        this.autoTranscribeThresholdBytes = autoTranscribeThresholdBytes;
    }

    /// <inheritdoc />
    public bool IsReady => !this.disposed && this.batchStt.IsReady;

    /// <inheritdoc />
    public void AcceptAudio(ReadOnlySpan<byte> pcm16kMono)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (pcm16kMono.IsEmpty)
        {
            return;
        }

        var copy = pcm16kMono.ToArray();

        lock (this.stateLock)
        {
            this.audioBuffer.AddRange(copy);

            if (this.autoTranscribeThresholdBytes <= 0)
            {
                return;
            }

            var bufferedBytes = this.audioBuffer.Count;
            var shouldTrigger = (bufferedBytes - this.bytesAtLastTrigger) >= this.autoTranscribeThresholdBytes
                && this.backgroundTranscription.IsCompleted;

            if (!shouldTrigger)
            {
                return;
            }

            this.bytesAtLastTrigger = bufferedBytes;

            // Fire-and-forget background transcription. Assignment stays inside the
            // lock so the next AcceptAudio can't double-schedule on a stale
            // IsCompleted read. Task.Run only queues the lambda; it won't run
            // synchronously on this thread, so no deadlock on stateLock.
            this.backgroundTranscription = Task.Run(async () =>
            {
                try
                {
                    await TranscribePendingAsync(CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Do not catch general exception types — fire-and-forget background path.
                catch (Exception ex)
                {
                    this.LogBackgroundTranscriptionFailed(ex.Message);
                }
#pragma warning restore CA1031
            });
        }
    }

    /// <inheritdoc />
    public string GetPartialResult()
    {
        lock (this.stateLock)
        {
            // When we have cross-pass commits (token-path), use the accumulator.
            // Otherwise (text-only path), fall back to the per-pass committedText
            // — preserves backward-compat for engines that don't expose token timing.
            var basis = this.committedTextAccumulator.Length > 0
                ? this.committedTextAccumulator
                : this.committedText;

            if (basis.Length == 0)
            {
                return this.provisionalText;
            }
            if (this.provisionalText.Length == 0)
            {
                return basis;
            }
            return basis + " " + this.provisionalText;
        }
    }

    /// <inheritdoc />
    public async Task<string> GetFinalResultAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        // Wait for any background transcription from AcceptAudio to finish so it
        // doesn't race the final pass, then run one authoritative pass on the
        // FULL audio buffer (ignoring the trim cursor — the final transcription
        // dispatched to the agent must always come from the highest-quality
        // single-shot pass over the complete utterance).
        await WaitForPendingTranscriptionAsync().ConfigureAwait(false);

        byte[] fullBuffer;
        lock (this.stateLock)
        {
            if (this.audioBuffer.Count == 0)
            {
                return string.Empty;
            }
            fullBuffer = this.audioBuffer.ToArray();
        }

        await this.transcribeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // No prompt for the final pass — we want Whisper to decode the
            // utterance from scratch with full audio context.
            var detailed = await this.batchStt.TranscribeDetailedAsync(fullBuffer, prompt: null, cancellationToken).ConfigureAwait(false);
            return detailed?.Text ?? string.Empty;
        }
        finally
        {
            this.transcribeGate.Release();
        }
    }

    /// <summary>
    /// Runs one transcription pass on the current audio buffer and applies
    /// LocalAgreement-2 to refresh <see cref="committedText"/> and
    /// <see cref="provisionalText"/>. Safe to call concurrently — the
    /// <see cref="transcribeGate"/> ensures only one pass runs at a time.
    /// Exposed to tests via InternalsVisibleTo.
    /// </summary>
    internal async Task TranscribePendingAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        byte[] slice;
        string? prompt;
        int snapshotEpoch;
        lock (this.stateLock)
        {
            if (this.audioBuffer.Count == 0 || this.audioBuffer.Count <= this.bytesCommittedThrough)
            {
                return;
            }
            // Slice from the trim cursor: we never re-transcribe audio that has
            // already been committed (text wise) by an earlier pass.
            var sliceLength = this.audioBuffer.Count - this.bytesCommittedThrough;
            slice = new byte[sliceLength];
            this.audioBuffer.CopyTo(this.bytesCommittedThrough, slice, 0, sliceLength);
            prompt = this.committedTextAccumulator.Length == 0 ? null : this.committedTextAccumulator;
            snapshotEpoch = this.resetEpoch;
        }

        await this.transcribeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var detailed = await this.batchStt.TranscribeDetailedAsync(slice, prompt, cancellationToken).ConfigureAwait(false);
            var rawOutput = detailed?.Text ?? string.Empty;
            var rawTokens = detailed?.Tokens ?? [];

            // Whisper sometimes echoes the prompt as if it were transcribed
            // audio. Strip it from BOTH text and tokens before LA-2 so the
            // commit logic doesn't double-count or get confused by spurious
            // timestamps for words that aren't in the slice audio.
            var newOutput = StripEchoedPrompt(rawOutput, prompt);
            var stripped = !ReferenceEquals(newOutput, rawOutput);
            var tokens = stripped
                ? StripEchoedTokens(rawTokens, prompt)
                : rawTokens;

            lock (this.stateLock)
            {
                // Reset-epoch race guard: if Reset() ran between our snapshot
                // and now, abort the write-back so we don't corrupt the freshly-
                // cleared state with stale results.
                if (this.resetEpoch != snapshotEpoch)
                {
                    return;
                }

                var newCommitted = LongestCommonWordPrefix(this.lastFullOutput, newOutput);
                this.committedText = newCommitted;
                this.provisionalText = SuffixAfterWordPrefix(newOutput, newCommitted);

                if (newCommitted.Length > 0 && tokens.Count > 0)
                {
                    // Token path: advance trim cursor and accumulate committed text.
                    // The next pass will see only audio AFTER the committed prefix
                    // and will get the accumulator as the Whisper prompt.
                    var advanceMs = ResolveCommittedAdvanceMs(newCommitted, tokens);
                    if (advanceMs > 0)
                    {
                        var advanceBytes = MsToBytes(advanceMs);
                        // Safety clamp: never advance past the slice we transcribed.
                        if (advanceBytes > slice.Length)
                        {
                            advanceBytes = slice.Length;
                        }
                        this.bytesCommittedThrough += advanceBytes;
                        this.committedTextAccumulator = this.committedTextAccumulator.Length == 0
                            ? newCommitted
                            : this.committedTextAccumulator + " " + newCommitted;

                        // lastFullOutput becomes the un-committed suffix so the
                        // next pass's LA-2 compares like-with-like (both pass
                        // outputs are now slice-relative starting after the
                        // newly-committed prefix).
                        this.lastFullOutput = this.provisionalText;
                    }
                    else
                    {
                        // Tokens present but couldn't resolve the end timestamp —
                        // fall back to text-only behaviour.
                        this.lastFullOutput = newOutput;
                    }
                }
                else
                {
                    // Text-only path (no tokens — engine doesn't expose them):
                    // preserve original behaviour. Cursor stays put.
                    this.lastFullOutput = newOutput;
                }
            }
        }
        finally
        {
            this.transcribeGate.Release();
        }
    }

    /// <summary>
    /// Given the words committed this pass and the per-token timestamps for
    /// the pass's (post-strip) output, return the millisecond duration to
    /// advance the trim cursor by — i.e. the END time of the last token whose
    /// joined text covers the committed prefix, MINUS the START time of the
    /// first token (the slice's audio origin). Returns 0 if no mapping can
    /// be made.
    /// </summary>
    /// <remarks>
    /// Walks tokens accumulating their text and matches CHARACTERS (not word
    /// counts) against the committed prefix, so Whisper.cpp's sub-word BPE
    /// splits ("I'll" → " I" + "'ll") are handled correctly. A token boundary
    /// where the joined text fully covers the committed prefix is the cut.
    /// </remarks>
    internal static int ResolveCommittedAdvanceMs(string committed, IReadOnlyList<TranscribedToken> tokens)
    {
        if (string.IsNullOrEmpty(committed) || tokens.Count == 0)
        {
            return 0;
        }

        var origin = tokens[0].StartMs;
        var matchIndex = FindTokenCoveringPrefix(committed, tokens);
        if (matchIndex < 0)
        {
            // Defensive: not enough token text to cover the committed prefix.
            return tokens[^1].EndMs - origin;
        }
        return tokens[matchIndex].EndMs - origin;
    }

    /// <summary>
    /// Walks tokens accumulating <see cref="TranscribedToken.Text"/>, returns
    /// the smallest index <c>i</c> such that the joined text (left-trimmed)
    /// fully contains <paramref name="prefix"/> at the start. Returns -1 if
    /// no such index exists.
    /// </summary>
    private static int FindTokenCoveringPrefix(string prefix, IReadOnlyList<TranscribedToken> tokens)
    {
        var joined = new System.Text.StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            joined.Append(tokens[i].Text);
            var normalized = joined.ToString().TrimStart();
            // Match if normalized == prefix (exact) or starts with prefix
            // followed by a word boundary (space or punctuation Whisper might
            // append). Avoid matching when prefix is just a sub-word fragment
            // — e.g. don't match "hel" of "hello" when prefix is "hello".
            if (normalized.Length >= prefix.Length
                && normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (normalized.Length == prefix.Length || !char.IsLetterOrDigit(normalized[prefix.Length])))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Converts a millisecond duration to bytes for 16kHz mono 16-bit PCM.</summary>
    /// <remarks>16000 Hz × 2 bytes/sample / 1000 ms/s = 32 bytes/ms.</remarks>
    internal static int MsToBytes(int milliseconds) => milliseconds * 32;

    /// <summary>
    /// Drop the leading tokens that correspond to the echoed prompt. Uses
    /// character-level prefix matching (via <see cref="FindTokenCoveringPrefix"/>)
    /// so sub-word BPE splits are handled correctly: prompt "hello" matched
    /// against tokens [(" hel"), ("lo"), (" world")] consumes the first two
    /// tokens and leaves " world".
    /// </summary>
    internal static IReadOnlyList<TranscribedToken> StripEchoedTokens(
        IReadOnlyList<TranscribedToken> tokens, string? prompt)
    {
        if (string.IsNullOrEmpty(prompt) || tokens.Count == 0)
        {
            return tokens;
        }

        var matchIndex = FindTokenCoveringPrefix(prompt, tokens);
        if (matchIndex < 0)
        {
            // Defensive: tokens don't fully cover the prompt — strip nothing.
            return tokens;
        }

        var keepStart = matchIndex + 1;
        if (keepStart >= tokens.Count)
        {
            return [];
        }
        var trimmed = new TranscribedToken[tokens.Count - keepStart];
        for (var j = 0; j < trimmed.Length; j++)
        {
            trimmed[j] = tokens[keepStart + j];
        }
        return trimmed;
    }

    /// <summary>
    /// Whisper sometimes echoes the prompt back as if it were transcribed audio.
    /// If <paramref name="output"/> begins with all the words of <paramref name="prompt"/>
    /// (case- and trailing-punctuation-insensitive), strip those words. The
    /// returned string starts with the first non-prompt word.
    /// Returns <paramref name="output"/> unchanged when the prompt is null/empty
    /// or doesn't appear as a leading prefix.
    /// </summary>
    internal static string StripEchoedPrompt(string output, string? prompt)
    {
        if (string.IsNullOrEmpty(prompt) || string.IsNullOrEmpty(output))
        {
            return output;
        }

        var promptWords = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var outputWords = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (promptWords.Length == 0 || outputWords.Length < promptWords.Length)
        {
            return output;
        }

        for (var i = 0; i < promptWords.Length; i++)
        {
            if (!WordEqualsIgnoringCaseAndPunctuation(outputWords[i], promptWords[i]))
            {
                return output;
            }
        }

        return string.Join(' ', outputWords, promptWords.Length, outputWords.Length - promptWords.Length);
    }

    /// <summary>
    /// Longest common prefix of two transcripts at word granularity with
    /// case-insensitive + trailing-punctuation-insensitive comparison.
    /// Returns the prefix using the tokens from <paramref name="b"/> (so the
    /// caller sees the newer capitalization/punctuation).
    /// </summary>
    internal static string LongestCommonWordPrefix(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return string.Empty;
        }

        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var limit = Math.Min(wordsA.Length, wordsB.Length);

        var commonCount = 0;
        for (var i = 0; i < limit; i++)
        {
            if (!WordEqualsIgnoringCaseAndPunctuation(wordsA[i], wordsB[i]))
            {
                break;
            }
            commonCount++;
        }

        return commonCount == 0
            ? string.Empty
            : string.Join(' ', wordsB, 0, commonCount);
    }

    /// <summary>
    /// Given the full newer output and its committed-word prefix, returns the
    /// remaining provisional suffix (trimmed of leading whitespace).
    /// </summary>
    internal static string SuffixAfterWordPrefix(string full, string wordPrefix)
    {
        if (string.IsNullOrEmpty(wordPrefix))
        {
            return full;
        }

        var prefixWordCount = wordPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var words = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (prefixWordCount >= words.Length)
        {
            return string.Empty;
        }
        return string.Join(' ', words, prefixWordCount, words.Length - prefixWordCount);
    }

    private static bool WordEqualsIgnoringCaseAndPunctuation(string a, string b)
    {
        return string.Equals(
            TrimPunctuation(a),
            TrimPunctuation(b),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimPunctuation(string word) =>
        word.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '(', ')');

    // --- Test-only state observers (InternalsVisibleTo speech.Tests) ---
    // These exist so tests can assert on the committed/provisional split that
    // GetPartialResult joins together. Production callers should use
    // GetPartialResult / GetFinalResultAsync.

    internal string GetCommittedTextForTesting()
    {
        lock (this.stateLock)
        {
            return this.committedText;
        }
    }

    internal string GetProvisionalTextForTesting()
    {
        lock (this.stateLock)
        {
            return this.provisionalText;
        }
    }

    internal int GetBytesCommittedThroughForTesting()
    {
        lock (this.stateLock)
        {
            return this.bytesCommittedThrough;
        }
    }

    internal string GetCommittedAccumulatorForTesting()
    {
        lock (this.stateLock)
        {
            return this.committedTextAccumulator;
        }
    }

    /// <summary>
    /// Awaits completion of the most-recently-scheduled background transcription
    /// (if any). Safe to call even when no transcription is pending.
    /// </summary>
    internal Task WaitForPendingTranscriptionAsync()
    {
        Task pending;
        lock (this.stateLock)
        {
            pending = this.backgroundTranscription;
        }
        return pending;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Background streaming transcription failed: {ErrorMessage}")]
    private partial void LogBackgroundTranscriptionFailed(string errorMessage);

    /// <inheritdoc />
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        lock (this.stateLock)
        {
            this.audioBuffer.Clear();
            this.committedText = string.Empty;
            this.provisionalText = string.Empty;
            this.lastFullOutput = string.Empty;
            this.bytesAtLastTrigger = 0;
            this.bytesCommittedThrough = 0;
            this.committedTextAccumulator = string.Empty;
            this.resetEpoch++;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.transcribeGate.Dispose();

        // We do NOT dispose the injected batchStt — ownership belongs to the caller.
        GC.SuppressFinalize(this);
    }
}
