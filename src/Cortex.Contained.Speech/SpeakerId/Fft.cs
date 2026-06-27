namespace Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// In-place radix-2 Cooley-Tukey FFT for real and imaginary float spans.
/// Input length must be a power of two. Internal — used by
/// <see cref="FbankExtractor"/>.
/// </summary>
internal static class Fft
{
    public static void Transform(Span<float> real, Span<float> imag)
    {
        if (real.Length != imag.Length)
        {
            throw new ArgumentException("Real and imaginary parts must have the same length.");
        }

        var n = real.Length;
        if (n <= 0 || (n & (n - 1)) != 0)
        {
            throw new ArgumentException("FFT length must be a positive power of two.");
        }

        // Bit-reversal permutation
        var j = 0;
        for (var i = 1; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // Cooley-Tukey butterflies
        for (var size = 2; size <= n; size <<= 1)
        {
            var half = size >> 1;
            var angleStep = -2.0 * Math.PI / size;
            for (var start = 0; start < n; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var angle = angleStep * k;
                    var wReal = (float)Math.Cos(angle);
                    var wImag = (float)Math.Sin(angle);

                    var even = start + k;
                    var odd = start + k + half;

                    var oddR = real[odd] * wReal - imag[odd] * wImag;
                    var oddI = real[odd] * wImag + imag[odd] * wReal;

                    real[odd] = real[even] - oddR;
                    imag[odd] = imag[even] - oddI;
                    real[even] += oddR;
                    imag[even] += oddI;
                }
            }
        }
    }
}
