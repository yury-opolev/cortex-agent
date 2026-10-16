using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Contracts.Tests;

public sealed class TenantConfigShowFullLlmErrorsTests
{
    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void ResolveShowFullLlmErrors_DefaultAndExplicit_ResolvesExpected(bool isDefault, bool? explicitValue, bool expected)
    {
        var config = new TenantConfig { Default = isDefault, ShowFullLlmErrors = explicitValue };

        Assert.Equal(expected, config.ResolveShowFullLlmErrors());
    }

    [Fact]
    public void ShowFullLlmErrors_DefaultsToNull()
    {
        Assert.Null(new TenantConfig().ShowFullLlmErrors);
    }
}
