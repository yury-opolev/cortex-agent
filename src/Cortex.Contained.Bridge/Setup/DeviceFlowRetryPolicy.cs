using System.Net;

namespace Cortex.Contained.Bridge.Setup;

/// <summary>
/// Classifies whether a device-code HTTP failure is worth retrying.
/// Only 5xx status codes are considered transient. All 4xx codes (including
/// 400, 401, 403, 404, and 429) are terminal and must never be retried.
/// Network-level exceptions are handled by the caller, not here.
/// </summary>
public static class DeviceFlowRetryPolicy
{
    /// <summary>
    /// Returns <see langword="true"/> when the HTTP status represents a transient
    /// server-side failure (5xx) that is safe to retry. Returns
    /// <see langword="false"/> for all 4xx client errors, 2xx successes, and any
    /// other status code.
    /// </summary>
    /// <param name="status">The HTTP status code to classify.</param>
    public static bool IsTransient(HttpStatusCode status)
    {
        var code = (int)status;
        return code >= 500 && code <= 599;
    }
}
