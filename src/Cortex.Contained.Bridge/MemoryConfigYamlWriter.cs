using System.Globalization;
using System.Text;

using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge;

/// <summary>
/// Serializes the <see cref="MemorySettingsConfig"/> portion of the bridge YAML file.
/// Extracted out of <c>Program.PersistSettingsToYaml</c> so it can be covered
/// by unit tests that guard against fields being silently dropped — a prior
/// bug had <c>idleCompactionEnabled</c> and <c>idleResetMinutes</c> missing
/// here, causing UI-set values to vanish on save and revert to the code
/// default on next load.
/// </summary>
internal static class MemoryConfigYamlWriter
{
    public static void AppendMemorySection(StringBuilder sb, MemorySettingsConfig mem)
    {
        sb.AppendLine();
        sb.AppendLine("memory:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  duplicateThreshold: {mem.DuplicateThreshold:F2}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  compactionSimilarityThreshold: {mem.CompactionSimilarityThreshold:F2}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  compactionEnabled: {(mem.CompactionEnabled ? "true" : "false")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  idleCompactionEnabled: {(mem.IdleCompactionEnabled ? "true" : "false")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  idleResetMinutes: {mem.IdleResetMinutes}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  compactionPreserveRecentTurns: {mem.CompactionPreserveRecentTurns}");

        if (!string.IsNullOrWhiteSpace(mem.EmbeddingEndpoint))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  embeddingEndpoint: {mem.EmbeddingEndpoint.Trim()}");
        }
    }
}
