using System.Globalization;
using System.Text;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>
/// Renders a <see cref="DeliveryReport"/> as the third-party-labelled note that precedes a message
/// delivered into a conversation from elsewhere. It replaces the reconstructed <c>send_message</c>
/// call: the model is told, by the system and with evidence IDs, who produced the message and why,
/// instead of being shown a tool call it never made in this conversation.
/// </summary>
public static class DeliveryReportRenderer
{
    /// <summary>Opening label; the note occupies the user slot without the user having said anything.</summary>
    public const string Label = "[delivery report — recorded by the system; the user did not write this]";

    private const int DigestChars = 160;

    /// <summary>The report text; <paramref name="eventGetAvailable"/> says whether the model can actually call <c>event_get</c>.</summary>
    public static string Render(DeliveryReport report, bool eventGetAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.Append(Label).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"A message was delivered to {report.ChannelId} at {report.AttemptedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC (send state: {report.State}");
        if (report.OperationId is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $", operation {report.OperationId}");
        }

        if (report.DeliveryEventId is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $", event {report.DeliveryEventId}");
        }

        text.Append(").\n");
        text.Append(CultureInfo.InvariantCulture, $"Produced by: {Producer(report)}");
        if (report.RunId is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $", in run {report.RunId}");
        }

        text.Append(" — not in this conversation, which is why nothing here leads up to it.\n");

        if (!report.EvidenceAvailable)
        {
            text.Append("Evidence: not recorded for this send.\n");
        }
        else if (report.Evidence.Count == 0)
        {
            text.Append("Evidence: that run recorded no tool results before sending.\n");
        }
        else
        {
            text.Append(eventGetAvailable
                ? "Evidence recorded by that run before sending (event_get returns the full record):\n"
                : "Evidence recorded by that run before sending:\n");
            foreach (var item in report.Evidence)
            {
                text.Append(CultureInfo.InvariantCulture, $"- {item.Tool} {(item.Ok ? "ok" : "failed")} {item.EventId}");
                if (item.Digest is { Length: > 0 } digest)
                {
                    var line = digest.ReplaceLineEndings(" ");
                    text.Append(CultureInfo.InvariantCulture, $": \"{(line.Length > DigestChars ? line[..DigestChars] + "…" : line)}\"");
                }

                text.Append('\n');
            }
        }

        text.Append(report.State == "acknowledged"
            ? "The message that follows is what was delivered. It is real: do not retract it, call it fabricated, or send it again."
            : "The message that follows may not have reached the user; check before relying on it.");
        return text.ToString();
    }

    private static string Producer(DeliveryReport report) => report.ProducerLabel ?? report.ProducerKind switch
    {
        AuthorKinds.ScheduledTask => $"scheduled task {report.ProducerId}",
        AuthorKinds.Timer => "a session timer",
        AuthorKinds.Subagent => $"subagent {report.ProducerId}",
        _ => "you, in another conversation",
    };
}
