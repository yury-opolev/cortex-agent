using System.Threading.Channels;
using Cortex.Contained.Speech.Tts;

namespace Cortex.Contained.Speech.Tests;

public class KokoroTextToSpeechTests
{
    [Fact]
    public void TryWriteOrDrop_OpenChannel_WritesChunkAndReturnsTrue()
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var chunk = new byte[] { 1, 2, 3 };

        var written = KokoroTextToSpeech.TryWriteOrDrop(channel.Writer, chunk);

        Assert.True(written);
        Assert.True(channel.Reader.TryRead(out var received));
        Assert.Equal(chunk, received);
    }

    [Fact]
    public void TryWriteOrDrop_ClosedChannel_ReturnsFalseWithoutThrowing()
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        channel.Writer.TryComplete();

        var written = KokoroTextToSpeech.TryWriteOrDrop(channel.Writer, new byte[] { 1, 2, 3 });

        Assert.False(written);
    }

    [Fact]
    public void TryWriteOrDrop_ChannelClosedWithError_ReturnsFalseWithoutThrowing()
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        channel.Writer.TryComplete(new InvalidOperationException("consumer bailed"));

        var written = KokoroTextToSpeech.TryWriteOrDrop(channel.Writer, new byte[] { 1, 2, 3 });

        Assert.False(written);
    }
}
