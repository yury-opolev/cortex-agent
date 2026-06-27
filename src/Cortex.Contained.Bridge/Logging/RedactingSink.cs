using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Common.Security;
using Serilog.Core;
using Serilog.Events;

namespace Cortex.Contained.Bridge.Logging;

/// <summary>
/// A Serilog sink wrapper that applies <see cref="SensitiveDataRedactor"/> to all
/// string property values before forwarding events to the inner sink.
/// This ensures API keys, tokens, phone numbers, and other sensitive data
/// never appear in log output.
/// </summary>
internal sealed class RedactingSink : ILogEventSink, IDisposable
{
    private readonly ILogEventSink innerSink;

    public RedactingSink(ILogEventSink innerSink)
    {
        ArgumentNullException.ThrowIfNull(innerSink);
        this.innerSink = innerSink;
    }

    public void Emit(LogEvent logEvent)
    {
        var redactedProperties = new List<LogEventProperty>(logEvent.Properties.Count);
        var anyRedacted = false;

        foreach (var kvp in logEvent.Properties)
        {
            if (kvp.Value is ScalarValue { Value: string stringValue })
            {
                var redacted = SensitiveDataRedactor.Redact(stringValue);
                if (!ReferenceEquals(redacted, stringValue) && redacted != stringValue)
                {
                    redactedProperties.Add(new LogEventProperty(kvp.Key, new ScalarValue(redacted)));
                    anyRedacted = true;
                    continue;
                }
            }

            redactedProperties.Add(new LogEventProperty(kvp.Key, kvp.Value));
        }

        if (anyRedacted)
        {
            var redactedEvent = new LogEvent(
                logEvent.Timestamp,
                logEvent.Level,
                logEvent.Exception,
                logEvent.MessageTemplate,
                redactedProperties);
            this.innerSink.Emit(redactedEvent);
        }
        else
        {
            this.innerSink.Emit(logEvent);
        }
    }

    public void Dispose()
    {
        (this.innerSink as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
