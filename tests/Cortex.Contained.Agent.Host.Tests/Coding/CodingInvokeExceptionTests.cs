using Cortex.Contained.Agent.Host.Coding;

namespace Cortex.Contained.Agent.Host.Tests.Coding;

public class CodingInvokeExceptionTests
{
    [Fact]
    public void StartFailed_Message_StatesNoSessionRunning_AndCarriesReason()
    {
        var ex = CodingInvokeException.StartFailed("400 model_not_supported", stderrTail: "boom");

        Assert.Equal("coda_start_failed", ex.Code);
        Assert.True(ex.SessionTerminated);
        Assert.Contains("FAILED to start", ex.Message);
        Assert.Contains("No session is running", ex.Message);
        Assert.Contains("400 model_not_supported", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public void StartFailed_NoStderr_OmitsTrailingNoise()
    {
        var ex = CodingInvokeException.StartFailed("provider unavailable", stderrTail: null);

        Assert.Equal("coda_start_failed", ex.Code);
        Assert.Contains("provider unavailable", ex.Message);
    }

    [Fact]
    public void Unreachable_Message_StatesUnknownState_NotVagueTimeout()
    {
        var ex = CodingInvokeException.Unreachable(45);

        Assert.Equal("coda_unreachable", ex.Code);
        Assert.False(ex.SessionTerminated);
        Assert.Contains("state is unknown", ex.Message);
        Assert.Contains("45", ex.Message);
    }
}
