using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Coding;

/// <summary>
/// Persistence model for the <c>external_agent_sessions</c> SQLite table.
/// </summary>
public sealed record CodingAgentSessionRecord
{
    public required string SessionId { get; init; }

    public required string ChannelId { get; init; }

    public required string WorkingFolder { get; init; }

    public required CodingPolicy Policy { get; init; }

    public string? SessionName { get; init; }

    public required CodingSessionState State { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset LastActivityAt { get; init; }

    public string? LastUserMessage { get; init; }

    public string? LastAssistantSummary { get; init; }

    public string? LastToolCallsJson { get; init; }

    public DateTimeOffset? EndedAt { get; init; }
}
