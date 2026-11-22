using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Hosting;

/// <summary>What the Bridge does with a tenant's storage key on connect.</summary>
internal enum StorageKeyPushDecision
{
    /// <summary>The Bridge holds a key: push it.</summary>
    Push,

    /// <summary>No key yet and the agent needs one for a fresh encrypted setup (or is too old to say): create it, then push.</summary>
    MintAndPush,

    /// <summary>No key and encryption is off: nothing to push; the key is created when the owner enables encryption.</summary>
    Withhold,

    /// <summary>No key but the agent holds encrypted data: never mint (that key could not open it); the owner restores the recovery kit.</summary>
    WithholdMissingKey,
}

/// <summary>
/// Decides whether the Bridge may create a tenant storage key. A new key is created only when no encrypted
/// data exists that it would orphan: a lost or rolled-back <c>secrets.json</c> must lead to a locked agent
/// and a recovery-kit restore, never to a silently minted key.
/// </summary>
internal static class StorageKeyPushPolicy
{
    /// <summary>The decision for a Bridge with or without a key and the agent's status (null: an agent without the storage hub).</summary>
    public static StorageKeyPushDecision Decide(bool bridgeHasKey, StorageEncryptionStatusDto? agent)
    {
        if (bridgeHasKey)
        {
            return StorageKeyPushDecision.Push;
        }

        if (agent is null)
        {
            return StorageKeyPushDecision.MintAndPush;
        }

        if (agent.HasEncryptedData)
        {
            return StorageKeyPushDecision.WithholdMissingKey;
        }

        return agent.EncryptAtRest ? StorageKeyPushDecision.MintAndPush : StorageKeyPushDecision.Withhold;
    }

    /// <summary>The key to push for <paramref name="tenantId"/> (null when none may be pushed) and the decision behind it.</summary>
    public static async Task<(string? Key, StorageKeyPushDecision Decision)> ResolveAsync(
        SecretManager secrets, string tenantId, Func<Task<StorageEncryptionStatusDto?>> agentStatus)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(agentStatus);
        var key = secrets.TryGetTenantStorageKey(tenantId);
        if (key is not null)
        {
            return (key, StorageKeyPushDecision.Push);
        }

        var decision = Decide(bridgeHasKey: false, await agentStatus().ConfigureAwait(false));
        return decision == StorageKeyPushDecision.MintAndPush
            ? (secrets.GetOrCreateTenantStorageKey(tenantId), decision)
            : (null, decision);
    }
}
