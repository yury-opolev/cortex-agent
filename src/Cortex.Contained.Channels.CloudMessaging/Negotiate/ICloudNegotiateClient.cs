namespace Cortex.Contained.Channels.CloudMessaging.Negotiate;

/// <summary>
/// Calls the AI Messenger service's <c>POST /negotiate-bridge</c> endpoint to
/// obtain a Web PubSub client URL scoped to this bridge's tenant groups.
/// </summary>
public interface ICloudNegotiateClient
{
    /// <summary>
    /// Authenticates the bridge and obtains a Web PubSub client connection URL.
    /// </summary>
    Task<NegotiateResult> NegotiateAsync(CancellationToken ct = default);
}
