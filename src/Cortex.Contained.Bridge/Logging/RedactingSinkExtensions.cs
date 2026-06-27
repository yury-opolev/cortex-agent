using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace Cortex.Contained.Bridge.Logging;

/// <summary>
/// Extension methods for configuring the <see cref="RedactingSink"/> in a Serilog pipeline.
/// </summary>
internal static class RedactingSinkExtensions
{
    /// <summary>
    /// Wraps a sink configuration with <see cref="RedactingSink"/> to redact sensitive data
    /// from all log output.
    /// </summary>
    public static LoggerConfiguration Redacted(
        this LoggerSinkConfiguration sinkConfiguration,
        Action<LoggerSinkConfiguration> configureSink,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose)
    {
        ArgumentNullException.ThrowIfNull(sinkConfiguration);
        ArgumentNullException.ThrowIfNull(configureSink);

        var innerSink = LoggerSinkConfiguration.Wrap(
            innerSink => new RedactingSink(innerSink),
            configureSink);

        return sinkConfiguration.Sink(innerSink, restrictedToMinimumLevel);
    }
}
