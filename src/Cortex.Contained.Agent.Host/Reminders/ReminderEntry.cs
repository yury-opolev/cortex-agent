namespace Cortex.Contained.Agent.Host.Reminders;

/// <summary>
/// One active in-session reminder. Owns its <see cref="System.Threading.Timer"/>
/// for the lifetime of the entry; the service disposes the timer on fire or cancel.
/// </summary>
internal sealed class ReminderEntry : IDisposable
{
    public required string Id { get; init; }

    public required string ConversationId { get; init; }

    public required string ChannelId { get; init; }

    public required int DelaySeconds { get; init; }

    public required string Text { get; init; }

    public required Timer Timer { get; init; }

    public void Dispose()
    {
        this.Timer.Dispose();
    }
}
