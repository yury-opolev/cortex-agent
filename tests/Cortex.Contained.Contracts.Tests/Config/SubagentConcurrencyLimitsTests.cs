using System.ComponentModel.DataAnnotations;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Contracts.Tests.Config;

public class SubagentConcurrencyLimitsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    public void IsValid_WithinRange_ReturnsTrue(int value)
    {
        Assert.True(SubagentConcurrencyLimits.IsValid(value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    [InlineData(int.MaxValue)]
    public void IsValid_OutOfRange_ReturnsFalse(int value)
    {
        Assert.False(SubagentConcurrencyLimits.IsValid(value));
    }

    [Fact]
    public void ThrowIfInvalid_ValidValue_DoesNotThrow()
    {
        var exception = Record.Exception(() => SubagentConcurrencyLimits.ThrowIfInvalid(50, "value"));
        Assert.Null(exception);
    }

    [Fact]
    public void ThrowIfInvalid_InvalidValue_ThrowsWithParamName()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => SubagentConcurrencyLimits.ThrowIfInvalid(51, "myParam"));

        Assert.Equal("myParam", ex.ParamName);
    }

    [Fact]
    public void AgentConfig_50_IsValid_51IsInvalid()
    {
        var validConfig = new AgentConfig { MaxConcurrentSubagents = 50 };
        var validResults = new List<ValidationResult>();
        Assert.True(Validator.TryValidateObject(
            validConfig, new ValidationContext(validConfig), validResults, validateAllProperties: true));

        var invalidConfig = new AgentConfig { MaxConcurrentSubagents = 51 };
        var invalidResults = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(
            invalidConfig, new ValidationContext(invalidConfig), invalidResults, validateAllProperties: true));
        Assert.Contains(invalidResults, r => r.MemberNames.Contains(nameof(AgentConfig.MaxConcurrentSubagents)));
    }

    [Fact]
    public void BridgeConfig_50_IsValid_51IsInvalid()
    {
        var validConfig = new BridgeConfig { HubToken = "shared-secret", MaxConcurrentSubagents = 50 };
        var validResults = new List<ValidationResult>();
        Assert.True(Validator.TryValidateObject(
            validConfig, new ValidationContext(validConfig), validResults, validateAllProperties: true));

        var invalidConfig = new BridgeConfig { HubToken = "shared-secret", MaxConcurrentSubagents = 51 };
        var invalidResults = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(
            invalidConfig, new ValidationContext(invalidConfig), invalidResults, validateAllProperties: true));
        Assert.Contains(invalidResults, r => r.MemberNames.Contains(nameof(BridgeConfig.MaxConcurrentSubagents)));
    }
}
