using Cortex.Contained.Bridge.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

/// <summary>
/// Tests for the pure per-tenant ceiling helper extracted from
/// <see cref="CodaSessionManager"/>. No process or DI required.
/// </summary>
public sealed class CodaSessionAdmissionTests
{
    [Fact]
    public void CheckTenantCeiling_below_max_for_tenant_returns_null()
    {
        string?[] live = ["t1", "t1", "t2"];

        var error = CodaSessionAdmission.CheckTenantCeiling(live, "t1", maxSessions: 3);

        Assert.Null(error);
    }

    [Fact]
    public void CheckTenantCeiling_at_max_for_tenant_returns_error()
    {
        string?[] live = ["t1", "t1", "t1"];

        var error = CodaSessionAdmission.CheckTenantCeiling(live, "t1", maxSessions: 3);

        Assert.NotNull(error);
        Assert.Equal(CodingAgentErrorCodes.MaxSessionsReached, error.Value.ErrorCode);
    }

    [Fact]
    public void CheckTenantCeiling_other_tenants_do_not_count_against_this_tenant()
    {
        string?[] live = ["t2", "t2", "t2"];

        var error = CodaSessionAdmission.CheckTenantCeiling(live, "t1", maxSessions: 3);

        Assert.Null(error);
    }

    [Fact]
    public void CheckTenantCeiling_null_tenant_ids_are_ignored()
    {
        string?[] live = [null, null];

        var error = CodaSessionAdmission.CheckTenantCeiling(live, "t1", maxSessions: 1);

        Assert.Null(error);
    }
}
