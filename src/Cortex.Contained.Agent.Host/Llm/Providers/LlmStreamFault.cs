using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm.Providers;

/// <summary>
/// Classifies and contains faults thrown *while a provider stream is being read*.
/// <para>
/// The non-streaming path (<c>CompleteWithRetryAsync</c>) wraps its provider call in a
/// <c>catch (Exception)</c>, so any fault becomes an error result that same-provider retry and
/// failover can act on. The streaming path could not do the same, because C# forbids
/// <c>yield return</c> inside a <c>try</c>/<c>catch</c> — so an <see cref="IOException"/> raised
/// by the SSE reader mid-stream escaped past BOTH retry and failover and killed the turn.
/// </para>
/// <para>
/// <see cref="Guard"/> restores the symmetry using a manual enumerator: every fault becomes a
/// terminal error chunk, tagged with a prefix that tells the facade's classifiers whether it is
/// worth retrying. Caller-requested cancellation is never swallowed.
/// </para>
/// </summary>
internal static class LlmStreamFault
{
    /// <summary>Marks a fault that is plausibly a transient transport blip — worth retrying.</summary>
    internal const string TransientPrefix = "Stream transport fault: ";

    /// <summary>Marks a fault that would recur identically on retry — surface it.</summary>
    internal const string TerminalPrefix = "Stream fault: ";

    /// <summary>
    /// True when the exception (or anything it wraps) is a transport-level blip: a dropped or
    /// reset connection, a socket error, or a timeout. A provider-side cancellation raised by an
    /// inactivity guard counts too — the caller did not ask to stop, so the turn is retryable.
    /// Programming errors (bad state, bad argument) are excluded: they fail identically on retry.
    /// </summary>
    internal static bool IsTransient(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is IOException or SocketException or HttpRequestException
                or TimeoutException or OperationCanceledException)
            {
                return true;
            }

            if (ex is AggregateException aggregate
                && aggregate.InnerExceptions.Any(inner => IsTransient(inner)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Formats an exception into a prefixed error-chunk message.</summary>
    internal static string Format(Exception exception)
        => (IsTransient(exception) ? TransientPrefix : TerminalPrefix) + exception.Message;

    /// <summary>
    /// Wraps a provider stream so a thrown fault becomes a terminal error chunk instead of
    /// escaping. Chunks already produced are passed through untouched, so a fault raised after
    /// content still surfaces (the caller is committed and cannot un-stream).
    /// </summary>
    internal static async IAsyncEnumerable<LlmStreamChunk> Guard(
        IAsyncEnumerable<LlmStreamChunk> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                // C# forbids `yield return` inside both `try` and `catch`, so the outcome is
                // captured here and yielded below, after the guarded region has closed.
                LlmStreamChunk? chunk = null;
                string? fault = null;
                try
                {
                    if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        chunk = enumerator.Current;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // the caller asked to stop — never convert this into an error
                }
                catch (Exception ex)
                {
                    fault = Format(ex);
                }

                if (fault is not null)
                {
                    yield return new LlmStreamChunk { IsComplete = true, ErrorMessage = fault };
                    yield break;
                }

                if (chunk is null)
                {
                    yield break; // stream ended cleanly
                }

                yield return chunk;
            }
        }
        finally
        {
            // A fault during disposal must not mask the outcome we already reported.
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }
}
