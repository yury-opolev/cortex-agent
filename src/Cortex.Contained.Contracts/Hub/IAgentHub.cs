namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// SignalR hub methods exposed by the agent (inside Docker container).
/// The Bridge calls these methods.
/// </summary>
/// <remarks>
/// This is a composed interface: the method surface is partitioned into feature
/// interfaces (<see cref="IChatHub"/>, <see cref="IMemoryHub"/>,
/// <see cref="IHistoryHub"/>, <see cref="IVoiceIdHub"/>, <see cref="ICodingHub"/>,
/// <see cref="IMcpHub"/>, <see cref="ISystemHub"/>) for readability and to let
/// consumers depend on the narrow slice they actually use. All methods still share
/// a single SignalR hub connection and route by method name, so the wire protocol
/// is unchanged.
/// </remarks>
public interface IAgentHub : IChatHub, IMemoryHub, IHistoryHub, IVoiceIdHub, ICodingHub, IMcpHub, ISystemHub
{
}
