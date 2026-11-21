using Cortex.Contained.Common;

namespace Cortex.Contained.Bridge.Tests.Common;

public sealed class HostTimeZoneTests
{
    [Fact]
    public void ToIanaId_ZoneWithIanaId_ReturnsItUnchanged()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

        Assert.Equal("Asia/Tokyo", HostTimeZone.ToIanaId(zone));
    }

    [Fact]
    public void ToIanaId_WindowsZone_ConvertsToIana()
    {
        // The zone is looked up by its Windows id; where the runtime reports it with an IANA id there is nothing to convert.
        if (!TimeZoneInfo.TryFindSystemTimeZoneById("Tokyo Standard Time", out var zone) || zone.HasIanaId)
        {
            return;
        }

        Assert.Equal("Asia/Tokyo", HostTimeZone.ToIanaId(zone));
    }

    [Fact]
    public void ToIanaId_CustomZoneWithoutMapping_ReturnsEmpty()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Not A Real Zone", TimeSpan.FromHours(3), "custom", "custom");

        Assert.Equal(string.Empty, HostTimeZone.ToIanaId(zone));
    }

    [Fact]
    public void GetIanaId_LocalZone_IsEmptyOrAnIdTheRuntimeResolves()
    {
        var iana = HostTimeZone.GetIanaId();

        if (iana.Length > 0)
        {
            Assert.Equal(iana, TimeZoneInfo.FindSystemTimeZoneById(iana).Id);
        }
    }

    [Fact]
    public void ComposeEnvironment_NothingSetYet_ExportsTheZone()
    {
        var environment = HostTimeZone.ComposeEnvironment(existing: null, ianaId: "Europe/Berlin");

        Assert.Equal("Europe/Berlin", environment[HostTimeZone.EnvironmentVariable]);
        Assert.Single(environment);
    }

    [Theory]
    [InlineData("Asia/Tokyo")]
    [InlineData("UTC")]
    public void ComposeEnvironment_UserAlreadySetTheVariable_LeavesItAlone(string existing)
    {
        Assert.Empty(HostTimeZone.ComposeEnvironment(existing, "Europe/Berlin"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ComposeEnvironment_ExistingBlank_StillExportsTheZone(string? existing)
    {
        Assert.Single(HostTimeZone.ComposeEnvironment(existing, "Europe/Berlin"));
    }

    [Fact]
    public void ComposeEnvironment_ZoneUnknown_ExportsNothing()
    {
        Assert.Empty(HostTimeZone.ComposeEnvironment(existing: null, ianaId: string.Empty));
    }
}
