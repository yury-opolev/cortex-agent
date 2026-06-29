using System.Net;
using Cortex.Contained.Bridge.Setup;

namespace Cortex.Contained.Bridge.Tests.Setup;

public sealed class DeviceFlowRetryPolicyTests
{
    // --- 4xx: all terminal ---

    [Fact]
    public void IsTransient_BadRequest400_ReturnsFalse()
    {
        var result = DeviceFlowRetryPolicy.IsTransient(HttpStatusCode.BadRequest);

        Assert.False(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]       // 401
    [InlineData(HttpStatusCode.Forbidden)]          // 403
    [InlineData(HttpStatusCode.NotFound)]           // 404
    [InlineData(HttpStatusCode.TooManyRequests)]    // 429
    public void IsTransient_ClientErrors_ReturnsFalse(HttpStatusCode status)
    {
        var result = DeviceFlowRetryPolicy.IsTransient(status);

        Assert.False(result);
    }

    // --- 5xx: transient ---

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]   // 500
    [InlineData(HttpStatusCode.BadGateway)]            // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)]    // 503
    public void IsTransient_ServerErrors_ReturnsTrue(HttpStatusCode status)
    {
        var result = DeviceFlowRetryPolicy.IsTransient(status);

        Assert.True(result);
    }

    // --- 2xx: not transient ---

    [Fact]
    public void IsTransient_Ok200_ReturnsFalse()
    {
        var result = DeviceFlowRetryPolicy.IsTransient(HttpStatusCode.OK);

        Assert.False(result);
    }
}
