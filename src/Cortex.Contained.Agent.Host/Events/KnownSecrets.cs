using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>
/// The secret values the agent knows (the "vault" pushed by the Bridge: provider API keys and
/// tokens), replaced exactly by persistence redaction. Until the first push
/// <see cref="IsLoaded"/> is false and payloads are stored as <c>[REDACTION_UNAVAILABLE]</c>.
/// </summary>
public sealed class KnownSecrets
{
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
        this.values = credentials.Providers
            .SelectMany(p => new[] { p.ApiKey, p.AccessToken, p.RefreshToken })
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        this.loaded = true;
    }
}
