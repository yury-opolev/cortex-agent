using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Vector helpers for exact dense search: L2 normalisation, SIMD dot products, little-endian
/// float32 BLOBs and the int8 copy (<c>[float32 scale][sbyte × dims]</c>) used above the size threshold.
/// </summary>
public static class VectorMath
{
    /// <summary>Returns <paramref name="vector"/> scaled to unit length (a zero vector stays zero).</summary>
    public static float[] Normalize(ReadOnlySpan<float> vector)
    {
        var norm = MathF.Sqrt(Dot(vector, vector));
        var result = new float[vector.Length];
        if (norm <= 0 || float.IsNaN(norm))
        {
            return result;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            result[i] = vector[i] / norm;
        }

        return result;
    }

    /// <summary>Dot product (cosine similarity for unit vectors).</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vector lengths differ ({a.Length} vs {b.Length}).", nameof(b));
        }

        var sum = 0f;
        var i = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            var va = MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
            for (var v = 0; v < va.Length; v++)
            {
                acc += va[v] * vb[v];
            }

            sum = Vector.Dot(acc, Vector<float>.One);
            i = va.Length * Vector<float>.Count;
        }

        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>Encodes a vector as little-endian float32 bytes.</summary>
    public static byte[] ToBlob(ReadOnlySpan<float> vector)
    {
        var blob = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(i * sizeof(float)), vector[i]);
        }

        return blob;
    }

    /// <summary>Decodes a little-endian float32 BLOB.</summary>
    public static float[] FromBlob(ReadOnlySpan<byte> blob)
    {
        if (blob.Length % sizeof(float) != 0)
        {
            throw new ArgumentException("A float32 vector BLOB must be a multiple of 4 bytes.", nameof(blob));
        }

        var vector = new float[blob.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(i * sizeof(float))..]);
        }

        return vector;
    }

    /// <summary>Symmetric per-vector int8 quantisation: <c>[scale][round(v/scale)]</c>, scale = max|v|/127.</summary>
    public static byte[] QuantizeInt8(ReadOnlySpan<float> vector)
    {
        var max = 0f;
        foreach (var value in vector)
        {
            max = MathF.Max(max, MathF.Abs(value));
        }

        var scale = max > 0 ? max / 127f : 1f;
        var blob = new byte[sizeof(float) + vector.Length];
        BinaryPrimitives.WriteSingleLittleEndian(blob, scale);
        for (var i = 0; i < vector.Length; i++)
        {
            blob[sizeof(float) + i] = unchecked((byte)(sbyte)Math.Clamp(MathF.Round(vector[i] / scale), -127, 127));
        }

        return blob;
    }

    /// <summary>Approximate dot product of a float query with an int8 copy from <see cref="QuantizeInt8"/>.</summary>
    public static float DotInt8(ReadOnlySpan<float> query, ReadOnlySpan<byte> quantized)
    {
        var codes = MemoryMarshal.Cast<byte, sbyte>(quantized[sizeof(float)..]);
        if (codes.Length != query.Length)
        {
            throw new ArgumentException($"Vector lengths differ ({query.Length} vs {codes.Length}).", nameof(quantized));
        }

        var sum = 0f;
        for (var i = 0; i < codes.Length; i++)
        {
            sum += query[i] * codes[i];
        }

        return sum * BinaryPrimitives.ReadSingleLittleEndian(quantized);
    }
}
