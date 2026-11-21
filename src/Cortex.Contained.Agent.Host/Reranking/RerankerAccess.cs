using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// What the Bridge granted: whether reranking is enabled and the sidecar's API key. Held in memory
/// only, like every other credential; until the Bridge pushes a grant reranking is off.
/// </summary>
public sealed class RerankerAccess
{
    private volatile Snapshot current = new(false, null);

    /// <summary>True when the Bridge enabled reranking.</summary>
    public bool Enabled => this.current.Enabled;

    /// <summary>The sidecar API key, or null.</summary>
    public string? ApiKey => this.current.ApiKey;

    /// <summary>Replaces the grant (every Bridge connect pushes one).</summary>
    public void Apply(RerankerGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        this.current = new Snapshot(grant.Enabled, string.IsNullOrWhiteSpace(grant.ApiKey) ? null : grant.ApiKey);
    }

    private sealed record Snapshot(bool Enabled, string? ApiKey);
}
