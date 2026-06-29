using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Source-generated, greppable telemetry for the GitHub Copilot setup flow. Every message is
/// prefixed <c>[copilot-setup]</c> so a run against a GitHub Enterprise host can be diagnosed
/// from the Bridge log: <c>grep "\[copilot-setup\]" bridge-YYYYMMDD.log</c>. Uses
/// <c>LoggerMessage</c> source generation to satisfy CA1848.
/// </summary>
internal static partial class CopilotSetupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[copilot-setup] device-flow init flow=device host={Host} customClientId={CustomClientId}")]
    public static partial void DeviceFlowInit(ILogger logger, string host, bool customClientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[copilot-setup] device-flow init OK host={Host}")]
    public static partial void DeviceFlowInitOk(ILogger logger, string host);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[copilot-setup] device-flow init FAILED host={Host} status={Status} detail={Detail}")]
    public static partial void DeviceFlowInitFailed(ILogger logger, string host, int? status, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[copilot-setup] device-flow init NETWORK-FAILED host={Host} detail={Detail}")]
    public static partial void DeviceFlowInitNetworkFailed(ILogger logger, string host, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "[copilot-setup] token-poll SUCCESS host={Host}")]
    public static partial void TokenPollSuccess(ILogger logger, string host);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[copilot-setup] token-poll FAILED host={Host} detail={Detail}")]
    public static partial void TokenPollFailed(ILogger logger, string host, string? detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[copilot-setup] token-poll NETWORK-FAILED host={Host} detail={Detail}")]
    public static partial void TokenPollNetworkFailed(ILogger logger, string host, string detail);
}
