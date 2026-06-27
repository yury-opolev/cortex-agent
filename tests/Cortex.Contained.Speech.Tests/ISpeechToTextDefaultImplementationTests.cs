using Cortex.Contained.Speech;

namespace Cortex.Contained.Speech.Tests;

public class ISpeechToTextDefaultImplementationTests
{
    /// <summary>
    /// Minimal implementation that overrides only TranscribeAsync — exercises the
    /// default TranscribeDetailedAsync interface implementation that engines
    /// without token-level timestamps fall back to.
    /// </summary>
    private sealed class TextOnlyStt(string? response) : ISpeechToText
    {
        private readonly string? response = response;

        public Task<string?> TranscribeAsync(byte[] pcmData, CancellationToken cancellationToken = default)
            => Task.FromResult(this.response);

        public bool IsReady => true;
        public void Dispose() { }
    }

    [Fact]
    public async Task TranscribeDetailedAsync_DefaultImpl_WrapsTranscribeAsyncAndReturnsEmptyTokens()
    {
        ISpeechToText stt = new TextOnlyStt("hello world");

        var result = await stt.TranscribeDetailedAsync([1, 2, 3, 4], prompt: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("hello world", result.Text);
        Assert.Empty(result.Tokens);
    }

    [Fact]
    public async Task TranscribeDetailedAsync_DefaultImpl_ReturnsNullWhenUnderlyingReturnsNull()
    {
        ISpeechToText stt = new TextOnlyStt(null);

        var result = await stt.TranscribeDetailedAsync([1, 2, 3, 4], prompt: null, CancellationToken.None);

        Assert.Null(result);
    }
}
