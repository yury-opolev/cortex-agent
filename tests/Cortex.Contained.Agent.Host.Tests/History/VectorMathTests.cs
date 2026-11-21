using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class VectorMathTests
{
    private static float[] Random(int seed, int dims = 37)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, dims).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
    }

    [Fact]
    public void Normalize_UnitLength_ZeroStaysZero()
    {
        var unit = VectorMath.Normalize(Random(1));

        Assert.Equal(1f, VectorMath.Dot(unit, unit), 4);
        Assert.All(VectorMath.Normalize(new float[5]), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Dot_MatchesScalarReference()
    {
        var a = Random(2, 1024);
        var b = Random(3, 1024);
        var expected = a.Zip(b, (x, y) => (double)x * y).Sum();

        Assert.Equal(expected, VectorMath.Dot(a, b), 2);
    }

    [Fact]
    public void Blob_RoundTrips_LittleEndian()
    {
        var vector = new[] { 1f, -2.5f, 0.125f };

        var blob = VectorMath.ToBlob(vector);

        Assert.Equal(12, blob.Length);
        Assert.Equal([0x00, 0x00, 0x80, 0x3F], blob[..4]);
        Assert.Equal(vector, VectorMath.FromBlob(blob));
    }

    [Fact]
    public void Int8_ApproximatesFloatDot()
    {
        var query = VectorMath.Normalize(Random(4, 1024));
        var doc = VectorMath.Normalize(Random(5, 1024));

        var quantized = VectorMath.QuantizeInt8(doc);

        Assert.Equal(4 + 1024, quantized.Length);
        Assert.Equal(VectorMath.Dot(query, doc), VectorMath.DotInt8(query, quantized), 2);
    }
}
