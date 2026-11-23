using Cortex.Contained.Bridge.Evaluation;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Evaluation;

/// <summary>"Run evaluation": password for the commands, a session-bound grant for the decrypted grading items.</summary>
public sealed class EvaluationRunEndpointsTests
{
    private const string Password = "owner-password";

    private readonly IEvaluationAgentClient agent = Substitute.For<IEvaluationAgentClient>();
    private readonly FakeTimeProvider time = new();

    public EvaluationRunEndpointsTests()
    {
        this.agent.ControlRunAsync(Arg.Any<EvaluationRunCommandDto>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationRunCommandResultDto { Ok = true, Run = new EvaluationRunDto { Exists = true, Status = "running" } });
        this.agent.GetGradingItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new GradingItemDto { Exists = true, Query = "zqquery", Candidate = "zqcandidate", Progress = new GradingProgressDto { Total = 75 } });
    }

    private static bool Verify(string? password) => password == Password;

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    [Fact]
    public void Routes_RequireAuthorization_PasswordRoutesAndGradingRoutesAreRateLimited()
    {
        var app = WebApplication.CreateBuilder().Build();
        app.MapEvaluationRunEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();

        Assert.Equal(11, endpoints.Count);
        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<IAuthorizeData>()));
        string? Policy(string route) => endpoints.Single(e => e.RoutePattern.RawText == route).Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        Assert.Equal("reauth", Policy("/api/tenants/{tenantId}/eval/run/start"));
        Assert.Equal("reauth", Policy("/api/tenants/{tenantId}/eval/run/export"));
        Assert.Equal("reauth", Policy("/api/tenants/{tenantId}/eval/grading/session"));
        Assert.Equal("eval-grading", Policy("/api/tenants/{tenantId}/eval/grading/items/{index:int}"));
        Assert.Equal("eval-grading", Policy("/api/tenants/{tenantId}/eval/grading/items/{index:int}/grade"));
        Assert.Equal("eval-grading", Policy("/api/tenants/{tenantId}/eval/grading/submit"));
    }

    [Fact]
    public async Task Control_WrongPassword_ForbiddenAndNothingRelayed()
    {
        var result = await EvaluationRunEndpoints.ControlAsync(this.agent, Verify, EvaluationRunActions.Start, new RunCommandRequest("wrong", Fresh: true), CancellationToken.None);

        Assert.Equal(403, StatusOf(result));
        await this.agent.DidNotReceive().ControlRunAsync(Arg.Any<EvaluationRunCommandDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Control_Export_RelaysPassphraseAndTerms()
    {
        var result = await EvaluationRunEndpoints.ControlAsync(
            this.agent, Verify, EvaluationRunActions.Export, new RunCommandRequest(Password, Passphrase: "correct horse battery", Terms: [new DictionaryTermDto("PersonA", "person")]), CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        await this.agent.Received(1).ControlRunAsync(
            Arg.Is<EvaluationRunCommandDto>(c => c.Action == "export" && c.Passphrase == "correct horse battery" && c.Terms!.Single().Term == "PersonA"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Control_RefusedByTheRun_Conflict()
    {
        this.agent.ControlRunAsync(Arg.Any<EvaluationRunCommandDto>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationRunCommandResultDto { Ok = false, Error = "in progress", Run = new EvaluationRunDto() });

        Assert.Equal(409, StatusOf(await EvaluationRunEndpoints.ControlAsync(this.agent, Verify, EvaluationRunActions.Start, new RunCommandRequest(Password), CancellationToken.None)));
    }

    [Fact]
    public void Grants_BoundToTheSessionAndExpireWhenIdle()
    {
        var grants = new GradingGrants(this.time);
        var session = EvaluationRunEndpoints.Session(Verify, grants, "cookie-a", new PasswordBody(Password));
        Assert.Equal(403, StatusOf(EvaluationRunEndpoints.Session(Verify, grants, "cookie-a", new PasswordBody("wrong"))));
        Assert.Equal(200, StatusOf(session));
        var token = grants.Issue("cookie-a");

        Assert.True(grants.Validate(token, "cookie-a"));
        Assert.False(grants.Validate(token, "cookie-b"));
        Assert.False(grants.Validate("forged", "cookie-a"));
        this.time.Advance(TimeSpan.FromMinutes(14));
        Assert.True(grants.Validate(token, "cookie-a"));
        this.time.Advance(GradingGrants.IdleTimeout + TimeSpan.FromSeconds(1));
        Assert.False(grants.Validate(token, "cookie-a"));
    }

    [Fact]
    public async Task Item_WithoutGrant_ForbiddenAndNothingDecrypted()
    {
        Assert.Equal(403, StatusOf(await EvaluationRunEndpoints.ItemAsync(this.agent, granted: false, 0, CancellationToken.None)));
        await this.agent.DidNotReceive().GetGradingItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        Assert.Equal(200, StatusOf(await EvaluationRunEndpoints.ItemAsync(this.agent, granted: true, 0, CancellationToken.None)));
    }

    [Fact]
    public async Task SubmitGrading_NeedsTheGrantAndRelaysTheDoneAction()
    {
        Assert.Equal(403, StatusOf(await EvaluationRunEndpoints.SubmitGradingAsync(this.agent, granted: false, CancellationToken.None)));
        await this.agent.DidNotReceive().ControlRunAsync(Arg.Any<EvaluationRunCommandDto>(), Arg.Any<CancellationToken>());

        Assert.Equal(200, StatusOf(await EvaluationRunEndpoints.SubmitGradingAsync(this.agent, granted: true, CancellationToken.None)));
        await this.agent.Received(1).ControlRunAsync(Arg.Is<EvaluationRunCommandDto>(c => c.Action == "submit-grading"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Grade_OutOfRange_BadRequest()
    {
        Assert.Equal(400, StatusOf(await EvaluationRunEndpoints.GradeAsync(this.agent, granted: true, 0, new GradeRequest(3), CancellationToken.None)));
        await this.agent.DidNotReceive().SubmitGradeAsync(Arg.Any<GradeRequestDto>(), Arg.Any<CancellationToken>());
    }
}
