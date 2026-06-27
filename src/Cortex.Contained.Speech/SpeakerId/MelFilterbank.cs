namespace Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Triangular mel-filterbank construction. Produces an array of per-filter
/// weight vectors indexed by FFT bin. Internal — consumed by
/// <see cref="FbankExtractor"/>.
/// </summary>
internal static class MelFilterbank
{
    /// <summary>
    /// O'Shaughnessy mel scale: <c>2595 * log10(1 + f/700)</c>.
    /// </summary>
    public static float HzToMel(float hz) => 2595.0f * MathF.Log10(1.0f + hz / 700.0f);

    /// <summary>
    /// Inverse of <see cref="HzToMel"/>: <c>700 * (10^(m/2595) - 1)</c>.
    /// </summary>
    public static float MelToHz(float mel) => 700.0f * (MathF.Pow(10.0f, mel / 2595.0f) - 1.0f);

    /// <summary>
    /// Builds <paramref name="numFilters"/> triangular filters spanning
    /// <paramref name="lowHz"/> to <paramref name="highHz"/> in mel scale.
    /// Returns an array of <paramref name="numFilters"/> rows; each row has
    /// <c>fftSize / 2 + 1</c> entries giving the filter weight per FFT
    /// magnitude bin.
    /// </summary>
    public static float[][] Create(int numFilters, int fftSize, int sampleRate, float lowHz, float highHz)
    {
        var numBins = (fftSize / 2) + 1;
        var lowMel = HzToMel(lowHz);
        var highMel = HzToMel(highHz);

        // numFilters + 2 mel points: low edge, N centres, high edge.
        var melPoints = new float[numFilters + 2];
        var step = (highMel - lowMel) / (numFilters + 1);
        for (var i = 0; i < melPoints.Length; i++)
        {
            melPoints[i] = lowMel + step * i;
        }

        // Convert mel points to fractional FFT bin indices.
        var binPoints = new float[melPoints.Length];
        for (var i = 0; i < melPoints.Length; i++)
        {
            var hz = MelToHz(melPoints[i]);
            binPoints[i] = (fftSize + 1) * hz / sampleRate;
        }

        var bank = new float[numFilters][];
        for (var f = 0; f < numFilters; f++)
        {
            var weights = new float[numBins];
            var left = binPoints[f];
            var centre = binPoints[f + 1];
            var right = binPoints[f + 2];

            for (var k = 0; k < numBins; k++)
            {
                if (k < left || k > right)
                {
                    continue;
                }
                weights[k] = k <= centre
                    ? (k - left) / (centre - left)
                    : (right - k) / (right - centre);
            }
            bank[f] = weights;
        }

        return bank;
    }
}
