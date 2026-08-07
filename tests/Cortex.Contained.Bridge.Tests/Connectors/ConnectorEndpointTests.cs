using System.Net;
using Cortex.Contained.Bridge.Connectors;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class ConnectorEndpointTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]   // IPv4-mapped IPv6 loopback
    [InlineData("10.0.0.5", false)]
    [InlineData("192.168.1.1", false)]
    public void IsLoopbackPeer_KnownAddresses_ReturnsExpected(string ipString, bool expected)
    {
        var address = IPAddress.Parse(ipString);
        Assert.Equal(expected, ConnectorEndpoint.IsLoopbackPeer(address));
    }

    [Fact]
    public void IsLoopbackPeer_NullAddress_ReturnsFalse()
    {
        Assert.False(ConnectorEndpoint.IsLoopbackPeer(null));
    }
}
