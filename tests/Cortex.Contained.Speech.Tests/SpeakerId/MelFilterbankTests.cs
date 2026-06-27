namespace Cortex.Contained.Speech.Tests.SpeakerId;

using Cortex.Contained.Speech.SpeakerId;

public sealed class MelFilterbankTests
{
    [Fact]
    public void HzToMel_Zero_ReturnsZero()
    {
        Assert.Equal(0.0f, MelFilterbank.HzToMel(0.0f), precision: 5);
    }

    [Fact]
    public void HzToMel_1000Hz_AboutOneThousand()
    {
        // 2595 * log10(1 + 1000/700) ≈ 999.99
        Assert.Equal(1000.0f, MelFilterbank.HzToMel(1000.0f), precision: 1);
    }

    [Fact]
    public void HzToMel_RoundTrip()
    {
        Assert.Equal(440.0f, MelFilterbank.MelToHz(MelFilterbank.HzToMel(440.0f)), precision: 2);
        Assert.Equal(7600.0f, MelFilterbank.MelToHz(MelFilterbank.HzToMel(7600.0f)), precision: 2);
    }

    [Fact]
    public void Create_Returns80By257MatrixFor16kHzWith512Fft()
    {
        var bank = MelFilterbank.Create(numFilters: 80, fftSize: 512, sampleRate: 16000, lowHz: 20.0f, highHz: 7600.0f);
        Assert.Equal(80, bank.Length);
        for (var i = 0; i < 80; i++)
        {
            Assert.Equal(257, bank[i].Length);
        }
    }

    [Fact]
    public void Create_AllFiltersHaveAtLeastOnePositiveBin()
    {
        var bank = MelFilterbank.Create(numFilters: 80, fftSize: 512, sampleRate: 16000, lowHz: 20.0f, highHz: 7600.0f);
        foreach (var filter in bank)
        {
            Assert.Contains(filter, w => w > 0.0f);
        }
    }

    [Fact]
    public void Create_FilterPeaksAreNonDecreasingInBin()
    {
        var bank = MelFilterbank.Create(numFilters: 80, fftSize: 512, sampleRate: 16000, lowHz: 20.0f, highHz: 7600.0f);
        var lastPeak = -1;
        foreach (var filter in bank)
        {
            var peak = 0;
            for (var i = 1; i < filter.Length; i++)
            {
                if (filter[i] > filter[peak])
                {
                    peak = i;
                }
            }
            Assert.True(peak >= lastPeak, $"filter peak regressed: {peak} after {lastPeak}");
            lastPeak = peak;
        }
    }
}
