using System.Buffers.Binary;

namespace Cortex.Contained.Channels.Discord.Tests.TurnDetection;

/// <summary>
/// Minimal RIFF/WAVE reader for 16-bit PCM mono fixture files.
/// Throws on any unexpected format — these are deterministic test inputs,
/// not arbitrary user audio.
/// </summary>
internal static class WavReader
{
    public static (short[] Samples, int SampleRate) ReadPcm16Mono(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)) != 0x52494646u // "RIFF"
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 0x57415645u) // "WAVE"
        {
            throw new InvalidDataException($"Not a RIFF/WAVE file: {path}");
        }

        var sampleRate = 0;
        short channels = 0;
        short bitsPerSample = 0;
        var dataOffset = -1;
        var dataLength = 0;
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var chunkId = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos, 4));
            var chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
            pos += 8;
            if (chunkId == 0x666d7420u) // "fmt "
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(pos + 2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(pos + 14, 2));
            }
            else if (chunkId == 0x64617461u) // "data"
            {
                dataOffset = pos;
                dataLength = chunkSize;
            }
            pos += chunkSize + (chunkSize & 1);
        }

        if (channels != 1 || bitsPerSample != 16 || dataOffset < 0)
        {
            throw new InvalidDataException(
                $"Expected 16-bit PCM mono. Got channels={channels}, bps={bitsPerSample}, hasData={dataOffset >= 0}.");
        }

        var samples = new short[dataLength / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataOffset + (i * 2), 2));
        }
        return (samples, sampleRate);
    }
}
