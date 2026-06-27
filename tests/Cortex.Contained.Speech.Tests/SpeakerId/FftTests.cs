namespace Cortex.Contained.Speech.Tests.SpeakerId;

using Cortex.Contained.Speech.SpeakerId;

public sealed class FftTests
{
    [Fact]
    public void Transform_OfImpulse_GivesAllOnes()
    {
        var real = new float[8] { 1, 0, 0, 0, 0, 0, 0, 0 };
        var imag = new float[8];

        Fft.Transform(real, imag);

        Assert.All(real, v => Assert.Equal(1.0f, v, precision: 5));
        Assert.All(imag, v => Assert.Equal(0.0f, v, precision: 5));
    }

    [Fact]
    public void Transform_OfDc_GivesSingleSpike()
    {
        var real = new float[8] { 1, 1, 1, 1, 1, 1, 1, 1 };
        var imag = new float[8];

        Fft.Transform(real, imag);

        Assert.Equal(8.0f, real[0], precision: 5);
        for (var i = 1; i < real.Length; i++)
        {
            Assert.Equal(0.0f, real[i], precision: 5);
            Assert.Equal(0.0f, imag[i], precision: 5);
        }
    }

    [Fact]
    public void Transform_OfSingleTone_HasPeakAtCorrectBin()
    {
        const int n = 16;
        var real = new float[n];
        var imag = new float[n];
        for (var k = 0; k < n; k++)
        {
            real[k] = MathF.Cos((2.0f * MathF.PI * 3 * k) / n);
        }

        Fft.Transform(real, imag);

        float Mag(int i) => MathF.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
        Assert.Equal(8.0f, Mag(3), precision: 3);
        Assert.Equal(8.0f, Mag(13), precision: 3);
        for (var i = 0; i < n; i++)
        {
            if (i == 3 || i == 13)
            {
                continue;
            }
            Assert.Equal(0.0f, Mag(i), precision: 3);
        }
    }

    [Fact]
    public void Transform_NonPowerOfTwo_Throws()
    {
        var real = new float[7];
        var imag = new float[7];
        Assert.Throws<ArgumentException>(() => Fft.Transform(real, imag));
    }

    [Fact]
    public void Transform_MismatchedLengths_Throws()
    {
        var real = new float[8];
        var imag = new float[16];
        Assert.Throws<ArgumentException>(() => Fft.Transform(real, imag));
    }
}
