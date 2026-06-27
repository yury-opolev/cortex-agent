using Cortex.Contained.Speech.Stt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Speech.Tests;

/// <summary>
/// Integration-flavored tests for <see cref="LiveKitTurnDetector"/>. Use the
/// real ONNX model cached by the spike so threshold lookups and inference are
/// exercised end-to-end. Tests are skipped-by-throw if the cache isn't there
/// — production deployments will instead place the model via
/// SpeechModels.targets.
/// </summary>
public class LiveKitTurnDetectorTests : IClassFixture<LiveKitTurnDetectorFixture>
{
    private readonly LiveKitTurnDetector detector;
    private readonly LiveKitTurnDetectorFixture fixture;

    public LiveKitTurnDetectorTests(LiveKitTurnDetectorFixture fixture)
    {
        this.fixture = fixture;
        this.detector = fixture.Detector;
    }

    [Fact]
    public void IsReady_AfterLoad_ReturnsTrue()
    {
        Assert.True(this.detector.IsReady);
    }

    [Fact]
    public void GetThreshold_English_ReturnsExpectedCalibration()
    {
        // languages.json from v0.4.1-intl publishes English threshold ≈ 0.011.
        var t = this.detector.GetThreshold("en");
        Assert.InRange(t, 0.005f, 0.05f);
    }

    [Fact]
    public void GetThreshold_Russian_IsCalibratedIndependently()
    {
        // Each language has its own calibration; they should not be identical.
        var en = this.detector.GetThreshold("en");
        var ru = this.detector.GetThreshold("ru");

        Assert.NotEqual(en, ru);
        Assert.InRange(ru, 0.0001f, 0.2f);
    }

    [Fact]
    public void GetThreshold_UnknownLanguage_FallsBackToEnglish()
    {
        var en = this.detector.GetThreshold("en");
        var unknown = this.detector.GetThreshold("xx");

        Assert.Equal(en, unknown);
    }

    [Fact]
    public async Task PredictEndOfTurnAsync_EmptyTurns_ReturnsZero()
    {
        var p = await this.detector.PredictEndOfTurnAsync([]);
        Assert.Equal(0f, p);
    }

    [Fact]
    public async Task PredictEndOfTurnAsync_OnlyNonUserAssistantRoles_ReturnsZero()
    {
        var p = await this.detector.PredictEndOfTurnAsync(
            [new TurnDetectorMessage("system", "you are helpful")]);
        Assert.Equal(0f, p);
    }

    [Fact]
    public async Task PredictEndOfTurnAsync_CompleteStatement_ScoresAboveThreshold()
    {
        var p = await this.detector.PredictEndOfTurnAsync(
            [new TurnDetectorMessage("user", "I need help fixing this bug")]);
        Assert.True(p > this.detector.GetThreshold("en"),
            $"Expected P(EOU) > English threshold, got {p}");
    }

    [Fact]
    public async Task PredictEndOfTurnAsync_CompleteQuestion_ScoresWellAboveThreshold()
    {
        // Fully-formed questions are the strongest end-of-turn signal.
        var p = await this.detector.PredictEndOfTurnAsync(
            [new TurnDetectorMessage("user", "What's the weather like today?")]);
        Assert.True(p > 0.1f, $"Expected confident END signal, got {p}");
    }

    [Fact]
    public async Task PredictEndOfTurnAsync_TrailingOff_ScoresBelowThreshold()
    {
        var p = await this.detector.PredictEndOfTurnAsync(
            [new TurnDetectorMessage("user", "I was thinking that maybe we should")]);
        Assert.True(p < this.detector.GetThreshold("en"),
            $"Expected P(EOU) < English threshold, got {p}");
    }

    // ── API hygiene ───────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSession_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LiveKitTurnDetector(
                null!,
                this.fixture.Tokenizer,
                new Dictionary<string, float>(),
                NullLogger<LiveKitTurnDetector>.Instance));
    }

    [Fact]
    public void Constructor_NullTokenizer_Throws()
    {
        // We can't cheaply build a session without a real model, but we can
        // construct the detector with the fixture's session and pass null
        // for tokenizer; we rely on ownership guarantees: ArgumentNullException
        // is thrown before the session is used.
        Assert.Throws<ArgumentNullException>(() =>
            new LiveKitTurnDetector(
                this.fixture.Session,
                null!,
                new Dictionary<string, float>(),
                NullLogger<LiveKitTurnDetector>.Instance));
    }

    [Fact]
    public void Constructor_NullThresholds_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LiveKitTurnDetector(
                this.fixture.Session,
                this.fixture.Tokenizer,
                null!,
                NullLogger<LiveKitTurnDetector>.Instance));
    }

    [Fact]
    public void Load_MissingModelFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            LiveKitTurnDetector.Load(
                Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid()}"),
                NullLoggerFactory.Instance));
    }
}

/// <summary>
/// Shared fixture loading the real detector once per test class. Uses the
/// spike's model cache; tests are skipped-by-throw if it's not present.
/// </summary>
public sealed class LiveKitTurnDetectorFixture : IDisposable
{
    public LiveKitTurnDetector Detector { get; }

    // Expose the underlying session + tokenizer so constructor null-check tests
    // can pass real non-null values for the arguments they don't want to null.
    public Microsoft.ML.OnnxRuntime.InferenceSession Session { get; }

    public Qwen25Tokenizer Tokenizer { get; }

    public LiveKitTurnDetectorFixture()
    {
        var cacheDir = TurnDetectorModelLocator.ResolveRequiredDirectory();

        this.Detector = LiveKitTurnDetector.Load(cacheDir, NullLoggerFactory.Instance);

        // We deliberately load a second session for the constructor null-check
        // tests so failing a null arg doesn't dispose the shared one.
        var opts = new Microsoft.ML.OnnxRuntime.SessionOptions();
        this.Session = new Microsoft.ML.OnnxRuntime.InferenceSession(Path.Combine(cacheDir, "model_q8.onnx"), opts);
        this.Tokenizer = Qwen25Tokenizer.LoadFromHuggingFaceTokenizerJson(Path.Combine(cacheDir, "tokenizer.json"));
    }

    public void Dispose()
    {
        this.Detector.Dispose();
        this.Session.Dispose();
    }
}
