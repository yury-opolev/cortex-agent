namespace Cortex.Contained.Launcher.Services;

public sealed record HealthStatus(bool IsBridgeHealthy, bool IsAgentHealthy);
