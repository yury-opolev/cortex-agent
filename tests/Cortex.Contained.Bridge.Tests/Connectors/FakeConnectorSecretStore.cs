using Cortex.Contained.Bridge.Connectors.Security;

namespace Cortex.Contained.Bridge.Tests.Connectors;

/// <summary>
/// In-memory <see cref="IConnectorSecretStore"/> test double backing the connector registry blob.
/// </summary>
internal sealed class FakeConnectorSecretStore : IConnectorSecretStore
{
    private readonly Dictionary<string, string> backing;

    /// <summary>Initialises a new <see cref="FakeConnectorSecretStore"/> over the supplied dictionary.</summary>
    public FakeConnectorSecretStore(Dictionary<string, string> backing)
    {
        this.backing = backing;
    }

    /// <summary>Initialises a new <see cref="FakeConnectorSecretStore"/> with its own backing store.</summary>
    public FakeConnectorSecretStore()
        : this(new Dictionary<string, string>(StringComparer.Ordinal))
    {
    }

    /// <inheritdoc/>
    public string? GetSecret(string secretId) =>
        this.backing.TryGetValue(secretId, out var value) ? value : null;

    /// <inheritdoc/>
    public void SetSecret(string secretId, string value) =>
        this.backing[secretId] = value;

    /// <inheritdoc/>
    public void RemoveSecret(string secretId) =>
        this.backing.Remove(secretId);
}
