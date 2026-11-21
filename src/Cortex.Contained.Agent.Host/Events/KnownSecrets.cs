using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>
/// The secret values the agent knows (the "vault" pushed by the Bridge: provider API keys and
/// tokens), replaced exactly by persistence redaction. Until the first push
/// <see cref="IsLoaded"/> is false and payloads are stored as <c>[REDACTION_UNAVAILABLE]</c>.
/// </summary>
public sealed class KnownSecrets
{
    private readonly Lock addLock = new();
    private volatile string[] values = [];
    private volatile bool loaded;

    /// <summary>True once the Bridge has pushed credentials.</summary>
    public bool IsLoaded => this.loaded;

    /// <summary>The current secret values.</summary>
    public IReadOnlyList<string> Values => this.values;

    /// <summary>Replaces the set with the secrets in <paramref name="credentials"/>.</summary>
    public void Load(LlmCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var loadedValues = credentials.Providers
            .SelectMany(p => new[] { p.ApiKey, p.AccessToken, p.RefreshToken })
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        lock (this.addLock)
        {
            this.values = loadedValues;
            this.loaded = true;
        }
    }

    /// <summary>
    /// Adds secrets learned outside a credentials push (tokens the Bridge returned from a refresh or
    /// reload), so persistence redaction replaces them too. Does not mark the set loaded.
    /// </summary>
    public void Add(params string?[] secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        lock (this.addLock)
        {
            this.values = this.values
                .Concat(secrets.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }
}
