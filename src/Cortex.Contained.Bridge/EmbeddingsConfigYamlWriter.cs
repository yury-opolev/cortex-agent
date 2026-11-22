using System.Text;

using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge;

/// <summary>
/// Serializes <see cref="EmbeddingsSettingsConfig"/> (<c>embeddings:</c>) into the bridge YAML file, so the
/// Recall page's GPU switch survives restarts and every other settings save keeps it.
/// </summary>
internal static class EmbeddingsConfigYamlWriter
{
    public static void AppendEmbeddingsSection(StringBuilder sb, EmbeddingsSettingsConfig embeddings)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(embeddings);
        sb.AppendLine();
        sb.AppendLine("embeddings:");
        sb.AppendLine(embeddings.Gpu ? "  gpu: true" : "  gpu: false");
    }
}
