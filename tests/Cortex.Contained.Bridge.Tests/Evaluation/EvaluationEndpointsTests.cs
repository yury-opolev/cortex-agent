using System.Text.Json;
using Cortex.Contained.Bridge.Evaluation;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Cortex.Contained.Bridge.Tests.Evaluation;

/// <summary>The evaluation API: re-authentication for anything that starts a job or carries workspace text.</summary>
public sealed class EvaluationEndpointsTests
{
    private const string Password = "owner-password";

    private readonly IEvaluationAgentClient agent = Substitute.For<IEvaluationAgentClient>();

    public EvaluationEndpointsTests()
    {
        this.agent.StartAsync(Arg.Any<EvaluationStartRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationJobDto { Id = "j1", Tool = "history-eval", Command = "ablate", State = EvaluationJobStates.Running });
    }

    private static bool Verify(string? password) => password == Password;

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static List<RouteEndpoint> Endpoints()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        app.MapEvaluationEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    [Fact]
    public void AllRoutes_RequireAuthorization_AndTheContentRoutesAreRateLimited()
    {
        var endpoints = Endpoints();

        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<IAuthorizeData>()));
        Assert.All(
            endpoints.Where(e => e.RoutePattern.RawText is "/api/tenants/{tenantId}/eval/files/read" or "/api/tenants/{tenantId}/eval/files/write"
                || (e.RoutePattern.RawText == "/api/tenants/{tenantId}/eval/jobs" && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"))),
            e => Assert.Equal("reauth", e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName));
    }

    [Fact]
    public async Task Start_WrongPassword_ForbiddenAndNothingStarted()
    {
        var result = await EvaluationEndpoints.StartAsync(this.agent, Verify, new EvaluationJobRequest("wrong", "history-eval", ["ablate"]), CancellationToken.None);

        Assert.Equal(403, StatusOf(result));
        await this.agent.DidNotReceive().StartAsync(Arg.Any<EvaluationStartRequestDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_Accepted_RelaysToolArgsAndPassphrase()
    {
        var result = await EvaluationEndpoints.StartAsync(
            this.agent, Verify, new EvaluationJobRequest(Password, "eval-corpus", ["export", "--version", "v1"], "correct horse battery staple"), CancellationToken.None);

        Assert.Equal(202, StatusOf(result));
        await this.agent.Received(1).StartAsync(
            Arg.Is<EvaluationStartRequestDto>(r => r.Tool == "eval-corpus" && string.Join(' ', r.Args) == "export --version v1" && r.Passphrase == "correct horse battery staple"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_RefusedByTheAgent_Conflict()
    {
        this.agent.StartAsync(Arg.Any<EvaluationStartRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationJobDto { Id = string.Empty, Tool = "history-eval", State = EvaluationJobStates.Refused, Output = "Another evaluation job is running" });

        Assert.Equal(409, StatusOf(await EvaluationEndpoints.StartAsync(this.agent, Verify, new EvaluationJobRequest(Password, "history-eval", ["ablate"]), CancellationToken.None)));
    }

    [Fact]
    public async Task WriteFile_WrongPasswordOrTooLarge_RefusedBeforeTheAgent()
    {
        Assert.Equal(403, StatusOf(await EvaluationEndpoints.WriteFileAsync(this.agent, Verify, new EvaluationFileWriteRequest("wrong", "history-eval", "dataset.jsonl", "x"), CancellationToken.None)));
        Assert.Equal(400, StatusOf(await EvaluationEndpoints.WriteFileAsync(
            this.agent, Verify, new EvaluationFileWriteRequest(Password, "history-eval", "dataset.jsonl", new string('x', EvaluationEndpoints.MaxFileChars + 1)), CancellationToken.None)));

        await this.agent.DidNotReceive().WriteFileAsync(Arg.Any<EvaluationFileWriteDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadFile_AgentNotConnected_ServiceUnavailable()
    {
        var result = await EvaluationEndpoints.ReadFileAsync(null, Verify, new EvaluationFileReadRequest(Password, "history-eval", "dataset.jsonl"), CancellationToken.None);

        Assert.Equal(503, StatusOf(result));
        Assert.Contains("not connected", JsonSerializer.Serialize(result.GetType().GetProperty("Value")!.GetValue(result)), StringComparison.Ordinal);
    }
}
