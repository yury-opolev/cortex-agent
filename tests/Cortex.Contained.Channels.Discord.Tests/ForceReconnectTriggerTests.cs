using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public sealed class ForceReconnectTriggerTests
{
    [Fact]
    public void Resolve_DaveMls_ReturnsMlsTrigger()
        => Assert.Equal("dave-mls-failure", ForceReconnectTrigger.Resolve(daveMlsSuspect: true, decryptFloodSuspect: false));

    [Fact]
    public void Resolve_DecryptFlood_ReturnsFloodTrigger()
        => Assert.Equal("dave-decrypt-flood", ForceReconnectTrigger.Resolve(daveMlsSuspect: false, decryptFloodSuspect: true));

    [Fact]
    public void Resolve_Neither_ReturnsAudioDeathTrigger()
        => Assert.Equal("audio-death-signal", ForceReconnectTrigger.Resolve(daveMlsSuspect: false, decryptFloodSuspect: false));

    [Fact]
    public void Resolve_MlsTakesPrecedenceOverFlood()
        => Assert.Equal("dave-mls-failure", ForceReconnectTrigger.Resolve(daveMlsSuspect: true, decryptFloodSuspect: true));
}
