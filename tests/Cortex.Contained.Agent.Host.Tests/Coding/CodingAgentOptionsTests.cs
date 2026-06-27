using Cortex.Contained.Agent.Host.Coding;

namespace Cortex.Contained.Agent.Host.Tests.Coding;

public class CodingAgentOptionsTests
{
    [Fact]
    public void BridgeInvokeTimeout_Default_IsModest()
    {
        // 45s: above the Bridge's 30s start timeout (Bridge's specific failure wins) and well
        // under the ~2-3 min comms bound. No coding invoke blocks on actual work.
        Assert.Equal(45, new CodingAgentOptions().BridgeInvokeTimeoutSeconds);
    }
}
