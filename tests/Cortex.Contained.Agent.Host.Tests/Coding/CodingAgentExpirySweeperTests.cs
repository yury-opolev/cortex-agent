using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Coding;

public class CodingAgentExpirySweeperTests : IDisposable
{
    private readonly string tempRoot;
    private readonly CodingAgentSessionStore store;

    public CodingAgentExpirySweeperTests()
    {
        this.tempRoot = Path.Combine(Path.GetTempPath(), $"sweeper-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempRoot);
        this.store = new CodingAgentSessionStore(this.tempRoot);
    }

    public void Dispose()
    {
        this.store.Dispose();
        try
        {
            Directory.Delete(this.tempRoot, recursive: true);
        }
        catch
        {
            // ignore
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SweepOnce_EndsIdleSessions()
    {
        var idle = new CodingAgentSessionRecord
        {
            SessionId = "stale",
            ChannelId = "ch",
            WorkingFolder = "C:\\repo",
            Policy = CodingPolicy.Prompt,
            State = CodingSessionState.Idle,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-12),
            LastActivityAt = DateTimeOffset.UtcNow.AddHours(-12),
        };
        var fresh = new CodingAgentSessionRecord
        {
            SessionId = "fresh",
            ChannelId = "ch2",
            WorkingFolder = "C:\\repo",
            Policy = CodingPolicy.Prompt,
            State = CodingSessionState.Idle,
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
        };
        this.store.Upsert(idle);
        this.store.Upsert(fresh);

        var agent = Substitute.For<ICodingAgent>();
        agent.EndSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CodingEndResponse { SessionId = "stale", State = CodingSessionState.Ended });

        var options = Substitute.For<IOptionsMonitor<CodingAgentOptions>>();
        options.CurrentValue.Returns(new CodingAgentOptions { IdleHours = 6 });

        var sweeper = new CodingAgentExpirySweeper(
            TimeProvider.System,
            this.store,
            agent,
            options,
            NullLogger<CodingAgentExpirySweeper>.Instance);

        await sweeper.SweepOnceAsync(CancellationToken.None);

        await agent.Received().EndSessionAsync("stale", Arg.Any<CancellationToken>());
        await agent.DidNotReceive().EndSessionAsync("fresh", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SweepOnce_IdleHoursZero_NoOp()
    {
        var idle = new CodingAgentSessionRecord
        {
            SessionId = "stale",
            ChannelId = "ch",
            WorkingFolder = "C:\\repo",
            Policy = CodingPolicy.Prompt,
            State = CodingSessionState.Idle,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-100),
            LastActivityAt = DateTimeOffset.UtcNow.AddHours(-100),
        };
        this.store.Upsert(idle);

        var agent = Substitute.For<ICodingAgent>();
        var options = Substitute.For<IOptionsMonitor<CodingAgentOptions>>();
        options.CurrentValue.Returns(new CodingAgentOptions { IdleHours = 0 });

        var sweeper = new CodingAgentExpirySweeper(
            TimeProvider.System,
            this.store,
            agent,
            options,
            NullLogger<CodingAgentExpirySweeper>.Instance);

        await sweeper.SweepOnceAsync(CancellationToken.None);

        await agent.DidNotReceive().EndSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
