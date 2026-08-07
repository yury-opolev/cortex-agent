using Cortex.Contained.Bridge.Connectors.Pairing;
using Cortex.Contained.Bridge.Endpoints;

namespace Cortex.Contained.Bridge.Tests.Connectors;

/// <summary>
/// Tests the internal seams of <see cref="ConnectorEndpoints"/> (<c>/api/connectors/*</c>).
/// The minimal-API handlers themselves pull in <c>ConnectorHost</c> / <c>ConnectorConfigStore</c>,
/// so — as with <c>SubagentSettingsEndpointTests</c> — the decision logic is extracted into static
/// seams and tested directly rather than through a <c>WebApplicationFactory</c>.
/// </summary>
public sealed class ConnectorEndpointsTests
{
    [Fact]
    public void ValidateChannelId_ValidId_ReturnsNull()
    {
        Assert.Null(ConnectorEndpoints.ValidateChannelId("plugin:terminal:default"));
    }

    [Fact]
    public void ValidateChannelId_InvalidId_ReturnsError()
    {
        Assert.False(string.IsNullOrEmpty(ConnectorEndpoints.ValidateChannelId("notplugin:x")));
    }

    [Fact]
    public void ValidateChannelId_Empty_ReturnsError()
    {
        Assert.False(string.IsNullOrEmpty(ConnectorEndpoints.ValidateChannelId(string.Empty)));
    }

    [Fact]
    public void TryApproveRequest_CoordinatorReturnsFalse_ReturnsFalse()
    {
        var coordinator = Substitute.For<IConnectorPairingCoordinator>();
        coordinator.Approve("unknown").Returns(false);

        Assert.False(ConnectorEndpoints.TryApproveRequest(coordinator, "unknown"));
        coordinator.Received(1).Approve("unknown");
    }

    [Fact]
    public void TryApproveRequest_CoordinatorReturnsTrue_ReturnsTrue()
    {
        var coordinator = Substitute.For<IConnectorPairingCoordinator>();
        coordinator.Approve("req-1").Returns(true);

        Assert.True(ConnectorEndpoints.TryApproveRequest(coordinator, "req-1"));
    }

    [Fact]
    public void TryDenyRequest_CoordinatorReturnsFalse_ReturnsFalse()
    {
        var coordinator = Substitute.For<IConnectorPairingCoordinator>();
        coordinator.Deny("unknown", Arg.Any<string>()).Returns(false);

        Assert.False(ConnectorEndpoints.TryDenyRequest(coordinator, "unknown"));
        coordinator.Received(1).Deny("unknown", Arg.Any<string>());
    }

    [Fact]
    public void TryDenyRequest_CoordinatorReturnsTrue_ReturnsTrue()
    {
        var coordinator = Substitute.For<IConnectorPairingCoordinator>();
        coordinator.Deny("req-1", Arg.Any<string>()).Returns(true);

        Assert.True(ConnectorEndpoints.TryDenyRequest(coordinator, "req-1"));
    }

    [Fact]
    public void IsWellFormedRequestId_IdIssuedByThePairingService_IsAccepted()
    {
        // The pairing service issues Guid.NewGuid().ToString("N").
        Assert.True(ConnectorEndpoints.IsWellFormedRequestId(Guid.NewGuid().ToString("N")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../../etc/passwd")]
    [InlineData("req-1")]
    [InlineData("injected\r\nWARN fake log line")]
    [InlineData("\u001b[31mred")]
    public void IsWellFormedRequestId_HostileOrMalformedId_IsRejected(string requestId)
    {
        Assert.False(ConnectorEndpoints.IsWellFormedRequestId(requestId));
    }

    [Fact]
    public void ValidateChannelId_ErrorMessage_DoesNotEchoTheSuppliedValue()
    {
        // Reflecting untrusted route text back into an error body invites both log and
        // response injection; the message must describe the rule, not repeat the input.
        const string hostile = "plugin:<script>alert(1)</script>:x";

        var error = ConnectorEndpoints.ValidateChannelId(hostile);

        Assert.False(string.IsNullOrEmpty(error));
        Assert.DoesNotContain("script", error, StringComparison.OrdinalIgnoreCase);
    }
}
