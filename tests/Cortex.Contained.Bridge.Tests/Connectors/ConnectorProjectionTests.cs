using System.Text.Json;
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Security;

namespace Cortex.Contained.Bridge.Tests.Connectors;

/// <summary>
/// Tests <see cref="ConnectorProjection"/> — the pure seam that turns a token-free
/// <see cref="ConnectorSummary"/> plus live attach state into the shape returned by
/// <c>GET /api/connectors</c>. Mirrors the discipline of <c>McpServerProjectionTests</c>:
/// status labels are asserted for every combination, and the serialized payload is asserted
/// to never carry a token.
/// </summary>
public sealed class ConnectorProjectionTests
{
    private static ConnectorSummary Summary(bool enabled = true) => new()
    {
        ChannelId = "plugin:terminal:default",
        Key = "terminal",
        InstanceId = "default",
        DisplayName = "My Terminal",
        PairedAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
        Enabled = enabled,
    };

    private static JsonElement ProjectAsJson(ConnectorSummary summary, bool attached, bool masterEnabled) =>
        JsonSerializer.SerializeToElement(ConnectorProjection.Project(summary, attached, masterEnabled));

    private static string Status(ConnectorSummary summary, bool attached, bool masterEnabled) =>
        ProjectAsJson(summary, attached, masterEnabled).GetProperty("status").GetString() ?? string.Empty;

    [Fact]
    public void Project_MasterDisabled_StatusIsDisabled()
    {
        Assert.Equal("disabled", Status(Summary(), attached: true, masterEnabled: false));
    }

    [Fact]
    public void Project_ConnectorDisabled_StatusIsDisabled()
    {
        Assert.Equal("disabled", Status(Summary(enabled: false), attached: true, masterEnabled: true));
    }

    [Fact]
    public void Project_MasterEnabledConnectorEnabledAttached_StatusIsConnected()
    {
        Assert.Equal("connected", Status(Summary(), attached: true, masterEnabled: true));
    }

    [Fact]
    public void Project_MasterEnabledConnectorEnabledNotAttached_StatusIsOffline()
    {
        Assert.Equal("offline", Status(Summary(), attached: false, masterEnabled: true));
    }

    [Fact]
    public void Project_NeverIncludesToken_JsonDoesNotContainTokenProperty()
    {
        var json = JsonSerializer.Serialize(
            ConnectorProjection.Project(Summary(), attached: true, masterEnabled: true));

        // ConnectorSummary carries no token by construction (only ConnectorRecord does), so the
        // projection cannot leak one. This asserts the invariant so a future field addition trips here.
        using var doc = JsonDocument.Parse(json);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            Assert.DoesNotContain("token", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", property.Name, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_ExposesAllExpectedFields()
    {
        var summary = Summary();
        var projected = ProjectAsJson(summary, attached: true, masterEnabled: true);

        Assert.Equal(summary.ChannelId, projected.GetProperty("channelId").GetString());
        Assert.Equal(summary.Key, projected.GetProperty("key").GetString());
        Assert.Equal(summary.InstanceId, projected.GetProperty("instanceId").GetString());
        Assert.Equal(summary.DisplayName, projected.GetProperty("displayName").GetString());
        Assert.Equal(summary.PairedAt, projected.GetProperty("pairedAt").GetDateTimeOffset());
        Assert.Equal(summary.LastSeenAt, projected.GetProperty("lastSeenAt").GetDateTimeOffset());
        Assert.True(projected.GetProperty("enabled").GetBoolean());
        Assert.True(projected.GetProperty("attached").GetBoolean());
        Assert.Equal("connected", projected.GetProperty("status").GetString());
    }

    [Fact]
    public void Project_NullSummary_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ConnectorProjection.Project(null!, attached: false, masterEnabled: true));
    }
}
