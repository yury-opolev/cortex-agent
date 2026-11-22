using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>A deletion was refused because far more messages are in scope than the owner's preview showed.</summary>
public sealed class HistoryScopeChangedException : InvalidOperationException
{
    /// <summary>Prefix of every message, shared with the Bridge.</summary>
    public const string Prefix = HistoryPurgeErrors.ScopeChangedPrefix;

    public HistoryScopeChangedException(int expected, int actual)
        : base($"{Prefix} the deletion now covers {actual} messages but the preview showed {expected}. Preview again.")
    {
    }
}
