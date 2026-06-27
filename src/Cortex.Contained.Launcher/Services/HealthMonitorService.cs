using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Launcher.Services;

public sealed partial class HealthMonitorService
{
    private const string BridgeHealthUrl = "http://127.0.0.1:5080/health";
    private const string AgentHealthUrl = "http://127.0.0.1:5100/health";

    private readonly HttpClient httpClient;
    private readonly ILogger<HealthMonitorService> logger;

    public HealthMonitorService(HttpClient httpClient, ILogger<HealthMonitorService> logger)
    {
        this.httpClient = httpClient;
        this.logger = logger;
    }

    public event Action<HealthStatus>? HealthChanged;

    public HealthStatus LastStatus { get; private set; } = new(false, false);

    public async Task<HealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var bridgeTask = this.CheckEndpointAsync(BridgeHealthUrl, cancellationToken);
        var agentTask = this.CheckEndpointAsync(AgentHealthUrl, cancellationToken);

        var bridgeHealthy = await bridgeTask.ConfigureAwait(false);
        var agentHealthy = await agentTask.ConfigureAwait(false);

        var status = new HealthStatus(bridgeHealthy, agentHealthy);

        if (status != this.LastStatus)
        {
            this.LogHealthChanged(bridgeHealthy, agentHealthy);
            this.LastStatus = status;
            this.HealthChanged?.Invoke(status);
        }

        return status;
    }

    public async Task MonitorAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await this.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.LogHealthCheckError(ex.Message);
                }

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — monitoring stopped.
        }
    }

    private async Task<bool> CheckEndpointAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await this.httpClient.GetAsync(url, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Health status changed — Bridge: {BridgeHealthy}, Agent: {AgentHealthy}")]
    private partial void LogHealthChanged(bool bridgeHealthy, bool agentHealthy);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health check error: {ErrorMessage}")]
    private partial void LogHealthCheckError(string errorMessage);
}
