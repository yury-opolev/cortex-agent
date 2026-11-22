using System.Text.Json;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Recall;
using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Recall;

/// <summary>The Recall page API: authorization, disconnected agents, validation and relaying.</summary>
public sealed class RecallEndpointsTests
{
    private readonly IRecallAgentClient agent = Substitute.For<IRecallAgentClient>();

    private static int StatusOf(IResult result)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        return status.StatusCode ?? 200;
    }

    private static JsonElement BodyOf(IResult result)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result);
        return JsonSerializer.SerializeToElement(value, value!.GetType(), JsonSerializerOptions.Web);
    }

    [Fact]
    public void AllRecallRoutes_RequireAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapRecallEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        Assert.Equal(10, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>()));
        Assert.All(endpoints, endpoint => Assert.Contains("/recall", endpoint.RoutePattern.RawText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetSettings_AgentDisconnected_Returns503()
    {
        Assert.Equal(503, StatusOf(await RecallEndpoints.GetSettingsAsync(null, CancellationToken.None)));
    }

    [Fact]
    public async Task UpdateSettings_RejectedByAgent_Returns400WithErrors()
    {
        this.agent.UpdateSettingsAsync(Arg.Any<RecallSettingsPatchDto>(), Arg.Any<CancellationToken>())
            .Returns(new RecallSettingsUpdateResultDto { Applied = false, Errors = ["rerankTimeoutMs must be in [100, 30000]."], Settings = new RecallSettingsDto() });

        var result = await RecallEndpoints.UpdateSettingsAsync(this.agent, new RecallSettingsPatchDto { RerankTimeoutMs = 1 }, CancellationToken.None);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal("rerankTimeoutMs must be in [100, 30000].", BodyOf(result).GetProperty("errors")[0].GetString());
    }

    [Fact]
    public async Task UpdateSettings_Applied_Returns200()
    {
        this.agent.UpdateSettingsAsync(Arg.Any<RecallSettingsPatchDto>(), Arg.Any<CancellationToken>())
            .Returns(new RecallSettingsUpdateResultDto { Applied = true, Settings = new RecallSettingsDto { HistoryEnabled = true } });

        var result = await RecallEndpoints.UpdateSettingsAsync(this.agent, new RecallSettingsPatchDto { HistoryEnabled = true }, CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
    }

    [Theory]
    [InlineData("purge", null, null, "dependentMemories must be review or delete")]
    [InlineData("review", "2026-11-02T00:00:00Z", "2026-11-01T00:00:00Z", "from must be before olderThan")]
    public async Task Purge_InvalidBody_Returns400WithoutCallingTheAgent(string action, string? from, string? olderThan, string error)
    {
        var body = new HistoryPurgeRequestDto
        {
            DependentMemories = action,
            From = from is null ? null : DateTimeOffset.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            OlderThan = olderThan is null ? null : DateTimeOffset.Parse(olderThan, System.Globalization.CultureInfo.InvariantCulture),
        };

        var result = await RecallEndpoints.PurgeAsync(this.agent, body, CancellationToken.None);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal(error, BodyOf(result).GetProperty("error").GetString());
        await this.agent.DidNotReceive().PurgeAsync(Arg.Any<HistoryPurgeRequestDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_DeleteChosen_NormalizesAndReportsTheChoice()
    {
        this.agent.PurgeAsync(Arg.Any<HistoryPurgeRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new HistoryPurgeResultDto { Deleted = 4, ErasureComplete = true, DeletedMemories = 2, DependentMemories = DependentMemoryActions.Delete });

        var result = await RecallEndpoints.PurgeAsync(this.agent, new HistoryPurgeRequestDto { ChannelId = " ch ", DependentMemories = "DELETE" }, CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        await this.agent.Received(1).PurgeAsync(
            Arg.Is<HistoryPurgeRequestDto>(r => r.ChannelId == "ch" && r.DependentMemories == DependentMemoryActions.Delete), Arg.Any<CancellationToken>());
        Assert.Contains("You chose Delete: 2 memories", BodyOf(result).GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_ReturnsCountsAndBothConfirmations()
    {
        this.agent.PreviewPurgeAsync(Arg.Any<HistoryPurgeRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new HistoryPurgePreviewDto { Messages = 12, DependentMemories = 3 });

        var body = BodyOf(await RecallEndpoints.PreviewPurgeAsync(this.agent, new HistoryPurgeRequestDto(), CancellationToken.None));

        Assert.Equal(12, body.GetProperty("messages").GetInt32());
        Assert.Equal(3, body.GetProperty("dependentMemories").GetInt32());
        Assert.Contains("You chose Review", body.GetProperty("confirmations").GetProperty("review").GetString(), StringComparison.Ordinal);
        Assert.Contains("erased permanently", body.GetProperty("confirmations").GetProperty("delete").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relay_AgentRejects_Returns400_TransportFails_Returns503()
    {
        Assert.Equal(400, StatusOf(await RecallEndpoints.RelayAsync<int>(() => throw new HubException("bad"), _ => Results.Ok())));
        Assert.Equal(503, StatusOf(await RecallEndpoints.RelayAsync<int>(() => throw new InvalidOperationException("gone"), _ => Results.Ok())));
    }

    [Fact]
    public async Task Decide_EmptyOrTooManyIds_Returns400()
    {
        var tooMany = Enumerable.Range(0, QuarantineLimits.MaxBatch + 1).Select(i => "m" + i).ToList();

        Assert.Equal(400, StatusOf(await RecallEndpoints.DecideAsync(this.agent, new QuarantineIdsRequest([]), keep: true, CancellationToken.None)));
        Assert.Equal(400, StatusOf(await RecallEndpoints.DecideAsync(this.agent, new QuarantineIdsRequest(tooMany), keep: false, CancellationToken.None)));
        await this.agent.DidNotReceive().DeleteAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Decide_Delete_RelaysTheIds()
    {
        this.agent.DeleteAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(new QuarantineDecisionResultDto { Affected = 2 });

        var result = await RecallEndpoints.DecideAsync(this.agent, new QuarantineIdsRequest(["a", "b"]), keep: false, CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        await this.agent.Received(1).DeleteAsync(Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void SetReranker_PersistsThenStartsConverging()
    {
        var config = new BridgeConfig();
        var persisted = new List<bool>();
        var runner = Substitute.For<IRerankerComposeRunner>();
        using var reranker = new RerankerSwitch(
            config,
            c => persisted.Add(c.Reranker.Enabled),
            new RerankerSidecarLifecycle(runner, () => "k", NullLogger<RerankerSidecarLifecycle>.Instance),
            _ => Task.CompletedTask,
            NullLogger<RerankerSwitch>.Instance);
        var converging = 0;

        var result = RecallEndpoints.SetReranker(reranker, new RerankerToggleRequest(true), _ => converging++);

        Assert.Equal(200, StatusOf(result));
        Assert.True(config.Reranker.Enabled);
        Assert.Equal([true], persisted);
        Assert.Equal(1, converging);
    }
}
