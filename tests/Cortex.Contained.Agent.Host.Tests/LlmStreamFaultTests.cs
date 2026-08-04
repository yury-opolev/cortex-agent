using Cortex.Contained.Agent.Host.Llm;
using Cortex.Contained.Agent.Host.Llm.Providers;
using Cortex.Contained.Contracts.Llm;
using System.Net.Sockets;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests the streaming fault guard that stops a provider-side exception from escaping the
/// stream raw. Before this guard, an <see cref="IOException"/> thrown while reading SSE lines
/// propagated out of <c>DirectLlmClient</c> past BOTH same-provider retry and failover (the
/// non-streaming path already caught everything), so a dropped connection killed the turn.
/// </summary>
public class LlmStreamFaultTests
{
    private static async IAsyncEnumerable<LlmStreamChunk> Throwing(Exception ex, params string[] before)
    {
        foreach (var text in before)
        {
            yield return new LlmStreamChunk { ContentDelta = text };
        }

        await Task.Yield();
        throw ex;
    }

    private static async IAsyncEnumerable<LlmStreamChunk> Clean(params string[] deltas)
    {
        foreach (var text in deltas)
        {
            yield return new LlmStreamChunk { ContentDelta = text };
        }

        yield return new LlmStreamChunk { IsComplete = true, FinishReason = "stop" };
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async Task<List<LlmStreamChunk>> DrainAsync(
        IAsyncEnumerable<LlmStreamChunk> source, CancellationToken ct = default)
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in source.WithCancellation(ct).ConfigureAwait(false))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    // ── Classification ────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutException))]
    public void IsTransient_TransportExceptions_ReturnsTrue(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.True(LlmStreamFault.IsTransient(ex));
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(ArgumentException))]
    public void IsTransient_ProgrammingErrors_ReturnsFalse(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.False(LlmStreamFault.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_TransportExceptionWrappedInAggregate_ReturnsTrue()
    {
        var ex = new InvalidOperationException("outer", new IOException("connection reset"));

        Assert.True(LlmStreamFault.IsTransient(ex));
    }

    [Fact]
    public void Format_TransientException_UsesTransientPrefix()
    {
        var message = LlmStreamFault.Format(new IOException("Unable to read data from the transport connection"));

        Assert.StartsWith(LlmStreamFault.TransientPrefix, message, StringComparison.Ordinal);
        Assert.Contains("Unable to read data from the transport connection", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_NonTransientException_UsesTerminalPrefix()
    {
        var message = LlmStreamFault.Format(new InvalidOperationException("bad state"));

        Assert.StartsWith(LlmStreamFault.TerminalPrefix, message, StringComparison.Ordinal);
    }

    // ── Guard behaviour ───────────────────────────────────────────────

    [Fact]
    public async Task Guard_CleanStream_PassesEveryChunkThrough()
    {
        var chunks = await DrainAsync(LlmStreamFault.Guard(Clean("a", "b"), CancellationToken.None));

        Assert.Equal(3, chunks.Count);
        Assert.Equal("a", chunks[0].ContentDelta);
        Assert.Equal("b", chunks[1].ContentDelta);
        Assert.True(chunks[2].IsComplete);
        Assert.Null(chunks[2].ErrorMessage);
    }

    [Fact]
    public async Task Guard_ThrowsBeforeAnyContent_YieldsTerminalErrorChunkInsteadOfThrowing()
    {
        var source = Throwing(new IOException("Unable to read data from the transport connection"));

        var chunks = await DrainAsync(LlmStreamFault.Guard(source, CancellationToken.None));

        var chunk = Assert.Single(chunks);
        Assert.True(chunk.IsComplete);
        Assert.NotNull(chunk.ErrorMessage);
        Assert.StartsWith(LlmStreamFault.TransientPrefix, chunk.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guard_ThrowsAfterContent_YieldsContentThenTerminalErrorChunk()
    {
        var source = Throwing(new IOException("reset"), "hello", " world");

        var chunks = await DrainAsync(LlmStreamFault.Guard(source, CancellationToken.None));

        Assert.Equal(3, chunks.Count);
        Assert.Equal("hello", chunks[0].ContentDelta);
        Assert.Equal(" world", chunks[1].ContentDelta);
        Assert.True(chunks[2].IsComplete);
        Assert.StartsWith(LlmStreamFault.TransientPrefix, chunks[2].ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guard_NonTransientException_YieldsTerminalPrefixedErrorChunk()
    {
        var source = Throwing(new InvalidOperationException("bug"));

        var chunks = await DrainAsync(LlmStreamFault.Guard(source, CancellationToken.None));

        var chunk = Assert.Single(chunks);
        Assert.StartsWith(LlmStreamFault.TerminalPrefix, chunk.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guard_CallerCancellation_PropagatesCancellationRatherThanErrorChunk()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var source = Throwing(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DrainAsync(LlmStreamFault.Guard(source, cts.Token), cts.Token));
    }

    [Fact]
    public async Task Guard_ProviderCancellationWithoutCallerCancellation_IsTreatedAsTransientFault()
    {
        // A stream-idle guard cancels its own token, surfacing an OperationCanceledException that
        // the CALLER never asked for. That must become a retryable fault, not a silent stop.
        var source = Throwing(new OperationCanceledException("idle timeout"));

        var chunks = await DrainAsync(LlmStreamFault.Guard(source, CancellationToken.None));

        var chunk = Assert.Single(chunks);
        Assert.True(chunk.IsComplete);
        Assert.StartsWith(LlmStreamFault.TransientPrefix, chunk.ErrorMessage!, StringComparison.Ordinal);
    }

    // ── Facade classification of guarded faults ───────────────────────

    [Fact]
    public void TransientFaultMessage_IsRetriedOnSameProvider()
    {
        var message = LlmStreamFault.Format(new IOException("transport connection closed"));

        Assert.True(DirectLlmClient.IsErrorTransientRetryable(message));
    }

    [Fact]
    public void TransientFaultMessage_IsAlsoFailoverEligible()
    {
        var message = LlmStreamFault.Format(new IOException("transport connection closed"));

        Assert.True(DirectLlmClient.IsErrorFailoverEligible(message));
    }

    [Fact]
    public void TerminalFaultMessage_IsNotRetried()
    {
        var message = LlmStreamFault.Format(new InvalidOperationException("bug in the SSE mapper"));

        Assert.False(DirectLlmClient.IsErrorTransientRetryable(message));
    }

    [Fact]
    public void TerminalFaultMessage_IsNotFailoverEligible()
    {
        var message = LlmStreamFault.Format(new InvalidOperationException("bug in the SSE mapper"));

        Assert.False(DirectLlmClient.IsErrorFailoverEligible(message));
    }

    [Fact]
    public void ContextOverflowRaisedAsAFault_IsNeverRetried()
    {
        // A context-overflow surfaced as an exception must stay terminal: retrying sends the
        // same oversized prompt again, and compaction is the only thing that helps.
        var message = LlmStreamFault.Format(new IOException("context window exceeded for this model"));

        Assert.False(DirectLlmClient.IsErrorTransientRetryable(message));
    }
}
