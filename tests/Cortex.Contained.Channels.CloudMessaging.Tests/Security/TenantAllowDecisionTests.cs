using Cortex.Contained.Channels.CloudMessaging.Security;

namespace Cortex.Contained.Channels.CloudMessaging.Tests.Security;

/// <summary>
/// Pins the pure tenant-allow guard logic.
/// </summary>
public class TenantAllowDecisionTests
{
    private static readonly IReadOnlyCollection<string> TwoTenants = ["tenant-a", "tenant-b"];

    [Fact]
    public void AllowedTenant_IsAllowed()
    {
        Assert.True(TenantAllowDecision.IsAllowed("tenant-a", TwoTenants));
    }

    [Fact]
    public void SecondAllowedTenant_IsAllowed()
    {
        Assert.True(TenantAllowDecision.IsAllowed("tenant-b", TwoTenants));
    }

    [Fact]
    public void UnknownTenant_IsDenied()
    {
        Assert.False(TenantAllowDecision.IsAllowed("tenant-c", TwoTenants));
    }

    [Fact]
    public void NullTenantId_IsDenied()
    {
        Assert.False(TenantAllowDecision.IsAllowed(null, TwoTenants));
    }

    [Fact]
    public void EmptyTenantId_IsDenied()
    {
        Assert.False(TenantAllowDecision.IsAllowed(string.Empty, TwoTenants));
    }

    [Fact]
    public void WhitespaceTenantId_IsDenied()
    {
        Assert.False(TenantAllowDecision.IsAllowed("   ", TwoTenants));
    }

    [Fact]
    public void EmptyAllowedSet_IsDenied()
    {
        Assert.False(TenantAllowDecision.IsAllowed("tenant-a", []));
    }

    [Fact]
    public void NullAllowedSet_IsDenied()
    {
        Assert.False(TenantAllowDecision.IsAllowed("tenant-a", null!));
    }

    [Fact]
    public void CaseSensitiveMatch_DifferentCase_IsDenied()
    {
        // Wire protocol is case-sensitive (design §8)
        Assert.False(TenantAllowDecision.IsAllowed("Tenant-A", TwoTenants));
    }
}
