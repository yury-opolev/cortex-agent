namespace Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Kaldi-style 80-dimensional log mel-filterbank features at 16 kHz.
/// Output layout: row-major <c>[numFrames, 80]</c> after per-utterance
/// cepstral mean normalisation.
/// </summary>
/// <remarks>
/// Parameters chosen to match the input expectation of WeSpeaker /
/// 3D-Speaker ERes2NetV2 ONNX exports:
/// <list type="bullet">
///   <item>16 kHz mono PCM16 input.</item>
///   <item>Pre-emphasis 0.97.</item>
///   <item>25 ms window (400 samples), 10 ms hop (160 samples), Hamming.</item>
///   <item>512-point FFT, power spectrum.</item>
///   <item>80 mel filters from 20 Hz to 7600 Hz.</item>
///   <item>Natural log of mel energies.</item>
///   <item>Cepstral mean normalisation per utterance.</item>
/// </list>
/// </remarks>
public sealed class FbankExtractor
{
    private const int SampleRate = 16000;
    private const int FftSize = 512;
    private const int WindowSize = 400;
    private const int HopSize = 160;
    private const int NumFilters = 80;
    private const float LowHz = 20.0f;
    private const float HighHz = 7600.0f;
    private const float PreEmphasis = 0.97f;
    private const float LogFloor = 1e-10f;

    private readonly float[] hamming;
    private readonly float[][] melBank;

    public FbankExtractor()
    {
        this.hamming = new float[WindowSize];
        for (var i = 0; i < WindowSize; i++)
        {
            this.hamming[i] = 0.54f - 0.46f * MathF.Cos((2.0f * MathF.PI * i) / (WindowSize - 1));
        }

        this.melBank = MelFilterbank.Create(NumFilters, FftSize, SampleRate, LowHz, HighHz);
    }

    /// <summary>Number of mel bins per frame (80).</summary>
    public static int FbankDim => NumFilters;

    /// <summary>
    /// Extracts log-mel fbank features and returns them as a flat row-major
    /// buffer plus the frame count. If the input is shorter than one window
    /// the result is empty.
    /// </summary>
    public (ReadOnlyMemory<float> Frames, int NumFrames) Extract(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length < WindowSize)
        {
            return (ReadOnlyMemory<float>.Empty, 0);
        }

        var numFrames = ((pcm.Length - WindowSize) / HopSize) + 1;
        var output = new float[numFrames * NumFilters];

        // Pre-emphasis: scale PCM16 to [-1, 1] and apply y[n] = x[n] - 0.97 * x[n-1].
        var preEmph = new float[pcm.Length];
        preEmph[0] = pcm[0] / (float)short.MaxValue;
        for (var i = 1; i < pcm.Length; i++)
        {
            preEmph[i] = (pcm[i] - PreEmphasis * pcm[i - 1]) / (float)short.MaxValue;
        }

        var fftReal = new float[FftSize];
        var fftImag = new float[FftSize];
        var power = new float[(FftSize / 2) + 1];

        for (var frame = 0; frame < numFrames; frame++)
        {
            var start = frame * HopSize;

            Array.Clear(fftReal);
            Array.Clear(fftImag);
            for (var n = 0; n < WindowSize; n++)
            {
                fftReal[n] = preEmph[start + n] * this.hamming[n];
            }

            Fft.Transform(fftReal, fftImag);

            for (var k = 0; k <= FftSize / 2; k++)
            {
                power[k] = fftReal[k] * fftReal[k] + fftImag[k] * fftImag[k];
            }

            for (var f = 0; f < NumFilters; f++)
            {
                var weights = this.melBank[f];
                float energy = 0.0f;
                for (var k = 0; k < weights.Length; k++)
                {
                    energy += weights[k] * power[k];
                }

                output[frame * NumFilters + f] = MathF.Log(MathF.Max(energy, LogFloor));
            }
        }

        // Per-utterance cepstral mean normalisation (subtract per-bin mean).
        for (var f = 0; f < NumFilters; f++)
        {
            float sum = 0.0f;
            for (var t = 0; t < numFrames; t++)
            {
                sum += output[t * NumFilters + f];
            }
            var mean = sum / numFrames;
            for (var t = 0; t < numFrames; t++)
            {
                output[t * NumFilters + f] -= mean;
            }
        }

        return (output, numFrames);
    }
}
