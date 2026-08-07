using System.Threading.Channels;
using Cortex.Contained.Bridge.Connectors;

namespace Cortex.Contained.Bridge.Tests.Connectors;

/// <summary>
/// In-memory <see cref="IConnectorTransport"/> test double.
/// Incoming frames are queued; outgoing frames are collected in <see cref="Sent"/>.
/// </summary>
internal sealed class FakeConnectorTransport : IConnectorTransport
{
    private readonly Channel<string?> incoming = Channel.CreateUnbounded<string?>();

    /// <summary>Frames sent via <see cref="SendAsync"/>.</summary>
    public List<string> Sent { get; } = new();

    /// <inheritdoc/>
    public bool IsOpen { get; set; } = true;

    /// <inheritdoc/>
    public string RemoteEndpoint { get; set; } = "127.0.0.1:9999";

    /// <summary>When true the next <see cref="ReceiveAsync"/> call will throw an exception set in <see cref="FaultException"/>.</summary>
    public bool Faulted { get; set; }

    /// <summary>Exception thrown when <see cref="Faulted"/> is true.</summary>
    public Exception FaultException { get; set; } = new InvalidOperationException("transport faulted");

    /// <summary>Queue a JSON frame that will be returned by the next <see cref="ReceiveAsync"/> call.</summary>
    public void QueueIncoming(string json) => this.incoming.Writer.TryWrite(json);

    /// <summary>Signals the end of the incoming stream; next <see cref="ReceiveAsync"/> returns null.</summary>
    public void CompleteIncoming() => this.incoming.Writer.TryWrite(null);

    /// <inheritdoc/>
    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        if (this.Faulted)
        {
            throw this.FaultException;
        }

        return await this.incoming.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task SendAsync(string json, CancellationToken ct)
    {
        this.Sent.Add(json);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task CloseAsync(string reason, CancellationToken ct)
    {
        this.incoming.Writer.TryWrite(null);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
