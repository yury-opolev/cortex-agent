using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation;

/// <summary>A tenant key ring with a fixed test key (never a real key).</summary>
internal static class EvalTestKeys
{
    public static readonly string Key = Convert.ToBase64String(Enumerable.Range(7, 32).Select(i => (byte)i).ToArray());

    public static StorageKeyRing Ring()
    {
        var ring = new StorageKeyRing();
        ring.Provide(Key);
        return ring;
    }
}
