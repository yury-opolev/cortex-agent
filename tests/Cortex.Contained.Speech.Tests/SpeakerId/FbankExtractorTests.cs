namespace Cortex.Contained.Speech.Tests.SpeakerId;

using Cortex.Contained.Speech.SpeakerId;

public sealed class FbankExtractorTests
{
    [Fact]
    public void Extract_TooShortAudio_ReturnsZeroFrames()
    {
        var extractor = new FbankExtractor();
        // 5 ms — shorter than the 25 ms window.
        var pcm = new short[80];

        var (frames, numFrames) = extractor.Extract(pcm);

        Assert.Equal(0, numFrames);
        Assert.True(frames.IsEmpty);
    }

    [Fact]
    public void Extract_OneSecondOfSilence_ReturnsExpectedFrameCount()
    {
        var extractor = new FbankExtractor();
        // 16000 samples - 400 window = 15600; 15600 / 160 = 97; + 1 = 98 frames.
        var pcm = new short[16000];

        var (frames, numFrames) = extractor.Extract(pcm);

        Assert.Equal(98, numFrames);
        Assert.Equal(98 * 80, frames.Length);
    }

    [Fact]
    public void Extract_OneSecondOfSilence_AllFramesAreFinite()
    {
        var extractor = new FbankExtractor();
        var pcm = new short[16000];

        var (frames, _) = extractor.Extract(pcm);

        foreach (var v in frames.Span)
        {
            Assert.False(float.IsNaN(v));
            Assert.False(float.IsInfinity(v));
        }
    }

    [Fact]
    public void Extract_AfterCmn_FramesHaveZeroMeanPerBin()
    {
        var extractor = new FbankExtractor();
        var pcm = SineTone(sampleRate: 16000, frequency: 440.0f, durationSeconds: 2.0f, amplitude: 0.1f);

        var (frames, numFrames) = extractor.Extract(pcm);
        Assert.True(numFrames > 10);

        var data = frames.Span;
        for (var bin = 0; bin < 80; bin++)
        {
            float sum = 0.0f;
            for (var i = 0; i < numFrames; i++)
            {
                sum += data[i * 80 + bin];
            }
            var mean = sum / numFrames;
            Assert.True(MathF.Abs(mean) < 1e-3f, $"bin {bin} mean {mean} not close to zero");
        }
    }

    [Fact]
    public void Extract_SineTone_HasStructureNotAllZero()
    {
        var extractor = new FbankExtractor();
        var pcm = SineTone(sampleRate: 16000, frequency: 440.0f, durationSeconds: 1.0f, amplitude: 0.3f);

        var (frames, _) = extractor.Extract(pcm);

        var totalAbs = 0.0f;
        foreach (var v in frames.Span)
        {
            totalAbs += MathF.Abs(v);
        }
        Assert.True(totalAbs > 1.0f);
    }

    private static short[] SineTone(int sampleRate, float frequency, float durationSeconds, float amplitude)
    {
        var n = (int)(sampleRate * durationSeconds);
        var pcm = new short[n];
        for (var i = 0; i < n; i++)
        {
            var v = amplitude * MathF.Sin(2.0f * MathF.PI * frequency * i / sampleRate);
            pcm[i] = (short)(v * short.MaxValue);
        }
        return pcm;
    }
}
