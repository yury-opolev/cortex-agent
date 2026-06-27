namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Reads and writes the context bootstrap file.
/// Extracted as an interface for testability.
/// </summary>
public interface IBootstrapContextStore
{
    /// <summary>Gets the current bootstrap context content.</summary>
    Task<string> GetBootstrapContextAsync(CancellationToken cancellationToken);

    /// <summary>Sets the bootstrap context content.</summary>
    Task SetBootstrapContextAsync(string content, CancellationToken cancellationToken);
}
