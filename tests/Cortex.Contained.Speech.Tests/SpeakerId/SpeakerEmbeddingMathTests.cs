namespace Cortex.Contained.Speech.Tests.SpeakerId;

using Cortex.Contained.Speech.SpeakerId;

public sealed class SpeakerEmbeddingMathTests
{
    [Fact]
    public void L2Normalise_UnitLengthOutput()
    {
        float[] input = [3.0f, 4.0f];
        var output = SpeakerEmbeddingMath.L2Normalise(input);
        Assert.Equal(0.6f, output[0], precision: 5);
        Assert.Equal(0.8f, output[1], precision: 5);
        var norm = MathF.Sqrt(output[0] * output[0] + output[1] * output[1]);
        Assert.Equal(1.0f, norm, precision: 5);
    }

    [Fact]
    public void L2Normalise_ZeroVector_ReturnsZeroVector()
    {
        float[] input = [0.0f, 0.0f, 0.0f];
        var output = SpeakerEmbeddingMath.L2Normalise(input);
        Assert.All(output, v => Assert.Equal(0.0f, v));
    }

    [Fact]
    public void L2Normalise_DoesNotMutateInput()
    {
        float[] input = [3.0f, 4.0f];
        SpeakerEmbeddingMath.L2Normalise(input);
        Assert.Equal(3.0f, input[0]);
        Assert.Equal(4.0f, input[1]);
    }

    [Fact]
    public void CosineSimilarity_IdenticalUnitVectors_ReturnsOne()
    {
        float[] a = [0.6f, 0.8f];
        float[] b = [0.6f, 0.8f];
        Assert.Equal(1.0f, SpeakerEmbeddingMath.CosineSimilarity(a, b), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalUnitVectors_ReturnsZero()
    {
        float[] a = [1.0f, 0.0f];
        float[] b = [0.0f, 1.0f];
        Assert.Equal(0.0f, SpeakerEmbeddingMath.CosineSimilarity(a, b), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_OppositeUnitVectors_ReturnsMinusOne()
    {
        float[] a = [1.0f, 0.0f];
        float[] b = [-1.0f, 0.0f];
        Assert.Equal(-1.0f, SpeakerEmbeddingMath.CosineSimilarity(a, b), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_DifferentLengths_Throws()
    {
        float[] a = [1.0f, 0.0f];
        float[] b = [1.0f, 0.0f, 0.0f];
        Assert.Throws<ArgumentException>(() => SpeakerEmbeddingMath.CosineSimilarity(a, b));
    }

    [Fact]
    public void CosineSimilarity_OneZeroVector_ReturnsZero()
    {
        float[] a = [1.0f, 0.0f];
        float[] b = [0.0f, 0.0f];
        Assert.Equal(0.0f, SpeakerEmbeddingMath.CosineSimilarity(a, b));
    }

    [Fact]
    public void MeanOfNormalised_NormalisesEachInputThenAveragesThenNormalises()
    {
        float[][] inputs =
        [
            [10.0f, 0.0f],
            [1.0f, 0.0f],
        ];

        var mean = SpeakerEmbeddingMath.MeanOfNormalised(inputs);
        Assert.Equal(2, mean.Length);
        Assert.Equal(1.0f, mean[0], precision: 5);
        Assert.Equal(0.0f, mean[1], precision: 5);
    }

    [Fact]
    public void MeanOfNormalised_EmptyInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => SpeakerEmbeddingMath.MeanOfNormalised(Array.Empty<float[]>()));
    }

    [Fact]
    public void MeanOfNormalised_DifferentDimensions_Throws()
    {
        float[][] inputs =
        [
            [1.0f, 0.0f],
            [1.0f, 0.0f, 0.0f],
        ];
        Assert.Throws<ArgumentException>(() => SpeakerEmbeddingMath.MeanOfNormalised(inputs));
    }
}
