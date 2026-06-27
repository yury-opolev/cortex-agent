namespace Cortex.Contained.Launcher.Services;

/// <summary>
/// Holds a mutable <see cref="CancellationTokenSource"/> that can be cancelled and replaced atomically.
/// </summary>
internal sealed class TokenHolder
{
    public CancellationTokenSource Cts { get; private set; } = new();

    /// <summary>
    /// Cancels the current <see cref="CancellationTokenSource"/>, disposes it, and replaces it with a fresh one.
    /// </summary>
    public void Reset()
    {
        var old = this.Cts;
        this.Cts = new CancellationTokenSource();
        old.Cancel();
        old.Dispose();
    }
}
