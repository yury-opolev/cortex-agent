using System.Text;

using Cortex.Contained.Bridge;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests;

/// <summary>
/// Tests for <see cref="MemoryConfigYamlWriter"/>. Prevents regressions where
/// memory fields silently stop being persisted — the prior bug was that
/// idleCompactionEnabled and idleResetMinutes were missing from the writer,
/// so UI-set values were dropped on every save and replaced with the code
/// default on next load.
/// </summary>
public class MemoryConfigYamlWriterTests
{
    [Fact]
    public void AppendMemorySection_WritesIdleResetMinutes()
    {
        var mem = new MemorySettingsConfig { IdleResetMinutes = 120 };
        var sb = new StringBuilder();

        MemoryConfigYamlWriter.AppendMemorySection(sb, mem);

        Assert.Contains("idleResetMinutes: 120", sb.ToString());
    }

    [Fact]
    public void AppendMemorySection_WritesIdleCompactionEnabledFalse()
    {
        var mem = new MemorySettingsConfig { IdleCompactionEnabled = false };
        var sb = new StringBuilder();

        MemoryConfigYamlWriter.AppendMemorySection(sb, mem);

        Assert.Contains("idleCompactionEnabled: false", sb.ToString());
    }

    [Fact]
    public void AppendMemorySection_WritesIdleCompactionEnabledTrue()
    {
        var mem = new MemorySettingsConfig { IdleCompactionEnabled = true };
        var sb = new StringBuilder();

        MemoryConfigYamlWriter.AppendMemorySection(sb, mem);

        Assert.Contains("idleCompactionEnabled: true", sb.ToString());
    }

    [Fact]
    public void AppendMemorySection_WritesCompactionPreserveRecentTurns()
    {
        var mem = new MemorySettingsConfig { CompactionPreserveRecentTurns = 6 };
        var sb = new StringBuilder();

        MemoryConfigYamlWriter.AppendMemorySection(sb, mem);

        Assert.Contains("compactionPreserveRecentTurns: 6", sb.ToString());
    }

    [Fact]
    public void AppendMemorySection_WritesEveryMemoryField()
    {
        var mem = new MemorySettingsConfig
        {
            DuplicateThreshold = 0.95f,
            CompactionSimilarityThreshold = 0.75f,
            CompactionEnabled = true,
            IdleCompactionEnabled = true,
            IdleResetMinutes = 360,
            CompactionPreserveRecentTurns = 8,
        };
        var sb = new StringBuilder();

        MemoryConfigYamlWriter.AppendMemorySection(sb, mem);
        var yaml = sb.ToString();

        Assert.Contains("duplicateThreshold: 0.95", yaml);
        Assert.Contains("compactionSimilarityThreshold: 0.75", yaml);
        Assert.Contains("compactionEnabled: true", yaml);
        Assert.Contains("idleCompactionEnabled: true", yaml);
        Assert.Contains("idleResetMinutes: 360", yaml);
        Assert.Contains("compactionPreserveRecentTurns: 8", yaml);
    }

    [Fact]
    public void AppendMemorySection_StartsWithMemoryHeader()
    {
        var mem = new MemorySettingsConfig();
        var sb = new StringBuilder();

        MemoryConfigYamlWriter.AppendMemorySection(sb, mem);

        Assert.Contains("\nmemory:", sb.ToString());
    }

    [Fact]
    public void AppendMemorySection_WritesEmbeddingEndpoint_WhenSet()
    {
        var mem = new MemorySettingsConfig { EmbeddingEndpoint = "http://mac:11434" };
        var sb = new StringBuilder();

        MemoryConfigYamlWriter.AppendMemorySection(sb, mem);

        Assert.Contains("embeddingEndpoint: http://mac:11434", sb.ToString());
    }

    [Fact]
    public void AppendMemorySection_OmitsEmbeddingEndpoint_WhenNull()
    {
        var mem = new MemorySettingsConfig { EmbeddingEndpoint = null };
        var sb = new StringBuilder();

        MemoryConfigYamlWriter.AppendMemorySection(sb, mem);

        Assert.DoesNotContain("embeddingEndpoint", sb.ToString());
    }

    [Fact]
    public void AppendMemorySection_OmitsEmbeddingEndpoint_WhenBlank()
    {
        var mem = new MemorySettingsConfig { EmbeddingEndpoint = "   " };
        var sb = new StringBuilder();

        MemoryConfigYamlWriter.AppendMemorySection(sb, mem);

        Assert.DoesNotContain("embeddingEndpoint", sb.ToString());
    }
}
