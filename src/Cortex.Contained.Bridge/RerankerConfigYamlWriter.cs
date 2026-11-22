using System.Text;

using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge;

/// <summary>
/// Serializes <see cref="RerankerSettingsConfig"/> (<c>reranker:</c>) into the bridge YAML file, so the
/// Recall page's reranker switch survives restarts and every other settings save keeps it.
/// </summary>
internal static class RerankerConfigYamlWriter
{
    public static void AppendRerankerSection(StringBuilder sb, RerankerSettingsConfig reranker)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(reranker);
        sb.AppendLine();
        sb.AppendLine("reranker:");
        sb.AppendLine(reranker.Enabled ? "  enabled: true" : "  enabled: false");
    }
}
