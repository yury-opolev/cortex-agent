using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Cortex.Contained.Speech.Stt;

/// <summary>
/// <see cref="ITurnDetector"/> backed by the LiveKit multilingual turn-detector
/// ONNX model (pruned Qwen2.5-0.5B fine-tune, revision v0.4.1-intl).
/// </summary>
/// <remarks>
/// <para>
/// Loads three artifacts at construction:
/// <list type="bullet">
///   <item><c>model_q8.onnx</c> — the INT8-quantized inference graph.</item>
///   <item><c>tokenizer.json</c> — the Qwen2.5 BPE vocab + merges (loaded via
///         <see cref="Qwen25Tokenizer"/>).</item>
///   <item><c>languages.json</c> — per-language calibrated thresholds.</item>
/// </list>
/// </para>
/// <para>
/// Each <see cref="PredictEndOfTurnAsync"/> call builds a prompt string via
/// <see cref="LiveKitPromptBuilder"/>, tokenizes it, left-truncates to 128
/// tokens, runs one forward pass through the ONNX session, and reads the
/// probability at the final position (the model's output is pre-sliced at
/// <c>&lt;|im_end|&gt;</c> — no softmax needed).
/// </para>
/// <para>
/// Thread-safe: <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>
/// supports concurrent calls, and the tokenizer is immutable after construction.
/// A single detector is shared across all voice sessions.
/// </para>
/// </remarks>
public sealed partial class LiveKitTurnDetector : ITurnDetector
{
    private const int MaxInputTokens = 128;
    private const int ImEndTokenId = 151645;
    private const float DefaultFallbackThreshold = 0.011f;   // English threshold per languages.json

    private readonly InferenceSession session;
    private readonly Qwen25Tokenizer tokenizer;
    private readonly Dictionary<string, float> languageThresholds;
    private readonly ILogger<LiveKitTurnDetector> logger;
    private bool disposed;

    /// <inheritdoc />
    public bool IsReady => !this.disposed;

    /// <summary>
    /// Creates a new detector. Prefer the static factory <see cref="Load"/>
    /// unless wiring the pieces manually (tests / DI with overrides).
    /// </summary>
    public LiveKitTurnDetector(
        InferenceSession session,
        Qwen25Tokenizer tokenizer,
        IReadOnlyDictionary<string, float> languageThresholds,
        ILogger<LiveKitTurnDetector> logger)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ArgumentNullException.ThrowIfNull(languageThresholds);
        this.languageThresholds = new Dictionary<string, float>(languageThresholds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Load the detector from a directory containing <c>model_q8.onnx</c>,
    /// <c>tokenizer.json</c>, and <c>languages.json</c> (as produced by
    /// downloading from the <c>livekit/turn-detector</c> HuggingFace repo at
    /// revision <c>v0.4.1-intl</c>).
    /// </summary>
    public static LiveKitTurnDetector Load(
        string modelDirectory,
        ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var modelPath = Path.Combine(modelDirectory, "model_q8.onnx");
        var tokenizerPath = Path.Combine(modelDirectory, "tokenizer.json");
        var languagesPath = Path.Combine(modelDirectory, "languages.json");

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Turn-detector ONNX model not found at {modelPath}.", modelPath);
        }
        if (!File.Exists(tokenizerPath))
        {
            throw new FileNotFoundException($"Turn-detector tokenizer.json not found at {tokenizerPath}.", tokenizerPath);
        }
        if (!File.Exists(languagesPath))
        {
            throw new FileNotFoundException($"Turn-detector languages.json not found at {languagesPath}.", languagesPath);
        }

        var sessionOptions = new SessionOptions
        {
            // Matches LiveKit's plugin defaults exactly; don't tune without benchmarking.
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1,
        };
        sessionOptions.AddSessionConfigEntry("session.dynamic_block_base", "4");

        var session = new InferenceSession(modelPath, sessionOptions);
        var tokenizer = Qwen25Tokenizer.LoadFromHuggingFaceTokenizerJson(tokenizerPath);
        var thresholds = LoadLanguageThresholds(languagesPath);

        var logger = loggerFactory.CreateLogger<LiveKitTurnDetector>();
        return new LiveKitTurnDetector(session, tokenizer, thresholds, logger);
    }

    /// <inheritdoc />
    public Task<float> PredictEndOfTurnAsync(
        IReadOnlyList<TurnDetectorMessage> turns,
        string language = "en",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ObjectDisposedException.ThrowIf(this.disposed, this);

        if (turns.Count == 0)
        {
            return Task.FromResult(0f);
        }

        var prompt = LiveKitPromptBuilder.BuildPrompt(turns);
        if (prompt.Length == 0)
        {
            return Task.FromResult(0f);
        }

        var ids = this.tokenizer.Encode(prompt);
        if (ids.Count == 0)
        {
            return Task.FromResult(0f);
        }

        // Left-truncate to match LiveKit's MAX_HISTORY_TOKENS.
        if (ids.Count > MaxInputTokens)
        {
            ids = ids.GetRange(ids.Count - MaxInputTokens, MaxInputTokens);
        }

        var longIds = new long[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            longIds[i] = ids[i];
        }

        var inputTensor = new DenseTensor<long>(longIds, [1, longIds.Length]);
        cancellationToken.ThrowIfCancellationRequested();

        float probability;
        using (var results = this.session.Run([NamedOnnxValue.CreateFromTensor("input_ids", inputTensor)]))
        {
            probability = ReadLastProbability(results, ids.Count);
        }

        this.LogPredicted(language, ids.Count, probability);
        return Task.FromResult(probability);
    }

    /// <inheritdoc />
    public float GetThreshold(string language)
    {
        if (!string.IsNullOrEmpty(language)
            && this.languageThresholds.TryGetValue(language, out var t))
        {
            return t;
        }

        return this.languageThresholds.TryGetValue("en", out var fallback)
            ? fallback
            : DefaultFallbackThreshold;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.session.Dispose();
    }

    // Read P(EOU) at the last input position. ONNX exports with a pre-sliced
    // output shape [1, seq] return the probability directly; raw-LM exports
    // with shape [1, seq, vocab] require softmax + lookup at <|im_end|>. We
    // handle both shapes so this code survives a model repackaging.
    private static float ReadLastProbability(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, int seqLen)
    {
        DisposableNamedOnnxValue first;
        using (var enumerator = results.GetEnumerator())
        {
            if (!enumerator.MoveNext())
            {
                throw new InvalidOperationException("Turn-detector inference produced no output.");
            }
            first = enumerator.Current;
        }

        var tensor = first.AsTensor<float>();
        var dims = tensor.Dimensions.ToArray();

        if (dims.Length == 2)
        {
            return tensor[0, seqLen - 1];
        }

        if (dims.Length == 3)
        {
            var vocab = dims[2];
            var lastRow = new float[vocab];
            for (var v = 0; v < vocab; v++)
            {
                lastRow[v] = tensor[0, seqLen - 1, v];
            }
            var max = lastRow.Max();
            double sum = 0;
            for (var v = 0; v < vocab; v++)
            {
                sum += Math.Exp(lastRow[v] - max);
            }
            return (float)(Math.Exp(lastRow[ImEndTokenId] - max) / sum);
        }

        throw new InvalidOperationException(
            $"Unexpected turn-detector output shape [{string.Join(",", dims)}].");
    }

    private static Dictionary<string, float> LoadLanguageThresholds(string languagesPath)
    {
        using var stream = File.OpenRead(languagesPath);
        using var doc = JsonDocument.Parse(stream);

        var thresholds = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in doc.RootElement.EnumerateObject())
        {
            if (lang.Value.TryGetProperty("threshold", out var t) && t.TryGetSingle(out var f))
            {
                thresholds[lang.Name] = f;
            }
        }
        return thresholds;
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "turn-detector predict lang={Language} tokens={Tokens} P(EOU)={Probability}")]
    private partial void LogPredicted(string language, int tokens, float probability);
}
