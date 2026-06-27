using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Coding;

/// <summary>
/// Periodic sweeper that ends sessions idle past the configured threshold.
/// </summary>
public sealed partial class CodingAgentExpirySweeper : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);
    private readonly TimeProvider timeProvider;
    private readonly CodingAgentSessionStore store;
    private readonly ICodingAgent externalAgent;
    private readonly IOptionsMonitor<CodingAgentOptions> options;
    private readonly ILogger<CodingAgentExpirySweeper> logger;

    public CodingAgentExpirySweeper(
        TimeProvider timeProvider,
        CodingAgentSessionStore store,
        ICodingAgent externalAgent,
        IOptionsMonitor<CodingAgentOptions> options,
        ILogger<CodingAgentExpirySweeper> logger)
    {
        this.timeProvider = timeProvider;
        this.store = store;
        this.externalAgent = externalAgent;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                this.LogSweepError(ex);
            }

            try
            {
                await Task.Delay(TickInterval, this.timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        var idleHours = this.options.CurrentValue.IdleHours;
        if (idleHours <= 0)
        {
            return;
        }

        var cutoff = this.timeProvider.GetUtcNow() - TimeSpan.FromHours(idleHours);
        var stale = this.store.ListIdleSince(cutoff);

        foreach (var record in stale)
        {
            try
            {
                await this.externalAgent.EndSessionAsync(record.SessionId, cancellationToken).ConfigureAwait(false);
                this.store.MarkEnded(record.SessionId);
                this.LogSessionExpired(record.SessionId, idleHours);
            }
            catch (Exception ex)
            {
                this.LogExpireError(record.SessionId, ex);
            }
        }
    }

    [LoggerMessage(EventId = 9130, Level = LogLevel.Warning, Message = "External agent expiry sweep failed")]
    private partial void LogSweepError(Exception ex);

    [LoggerMessage(EventId = 9131, Level = LogLevel.Information, Message = "External agent session {sessionId} expired after {idleHours}h idle")]
    private partial void LogSessionExpired(string sessionId, int idleHours);

    [LoggerMessage(EventId = 9132, Level = LogLevel.Warning, Message = "Failed to expire coding agent session {sessionId}")]
    private partial void LogExpireError(string sessionId, Exception ex);
}
