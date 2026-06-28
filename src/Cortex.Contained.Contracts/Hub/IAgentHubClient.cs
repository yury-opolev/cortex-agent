namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Callbacks the agent pushes to the Bridge via SignalR.
/// The Bridge implements these methods.
/// </summary>
/// <remarks>
/// This is a composed interface: the callback surface is partitioned into feature
/// interfaces (<see cref="IChatHubClient"/>, <see cref="ICredentialsHubClient"/>,
/// <see cref="IVoiceIdHubClient"/>, <see cref="ICodingHubClient"/>,
/// <see cref="IMcpHubClient"/>) for readability and to let consumers depend on the
/// narrow slice they actually use. All callbacks still share a single SignalR hub
/// connection and route by method name, so the wire protocol is unchanged.
/// </remarks>
public interface IAgentHubClient : IChatHubClient, ICredentialsHubClient, IVoiceIdHubClient, ICodingHubClient, IMcpHubClient
{
}
