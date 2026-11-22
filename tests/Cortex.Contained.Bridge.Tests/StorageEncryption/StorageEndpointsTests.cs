using System.Text;
using System.Text.Json;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Storage;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.StorageEncryption;

/// <summary>The Storage page API: re-authentication, confirmations, key custody and the recovery kit.</summary>
public sealed class StorageEndpointsTests : IDisposable
{
    private const string Password = "owner-password";
    private const string Passphrase = "correct horse battery staple";
    private static readonly string OtherKey = Convert.ToBase64String(Enumerable.Range(7, 32).Select(i => (byte)i).ToArray());

    private readonly string dir = Path.Combine(Path.GetTempPath(), "cortex-storage-api-" + Guid.NewGuid().ToString("N"));
    private readonly SecretManager secrets;
    private readonly IStorageAgentClient agent = Substitute.For<IStorageAgentClient>();
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

    public StorageEndpointsTests()
    {
        this.secrets = new SecretManager(new InMemorySecretStore(), NullLogger<SecretManager>.Instance, this.dir);
        this.agent.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new StorageEncryptionStatusDto());
        this.agent.ProvideKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        this.agent.EnableAsync(Arg.Any<StorageEncryptionEnableRequestDto>(), Arg.Any<CancellationToken>())
            .Returns(new StorageEncryptionEnableResultDto { Accepted = true, RestartScheduled = true });
    }

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static bool Verify(string? password) => password == Password;

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static JsonElement BodyOf(IResult result)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result);
        return JsonSerializer.SerializeToElement(value, value!.GetType(), JsonSerializerOptions.Web);
    }

    private static string KitFor(string tenantId, string key) =>
        Encoding.UTF8.GetString(StorageRecoveryKit.Create(tenantId, key, Passphrase, DateTimeOffset.UnixEpoch, StorageRecoveryKit.MinimumIterations));

    private Task<IResult> Enable(StorageEnableRequest body) =>
        StorageEndpoints.EnableAsync("default", this.agent, this.secrets, Verify, body, CancellationToken.None);

    [Fact]
    public void AllStorageRoutes_RequireAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapStorageEndpoints(this.dir);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();

        Assert.Equal(5, endpoints.Count);
        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<IAuthorizeData>()));
        Assert.All(endpoints, e => Assert.StartsWith("/api/tenants/{tenantId}/storage/", e.RoutePattern.RawText, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/api/tenants/{tenantId}/storage/encryption/enable")]
    [InlineData("/api/tenants/{tenantId}/storage/recovery-kit")]
    [InlineData("/api/tenants/{tenantId}/storage/recovery-kit/restore")]
    public void EveryRouteThatChecksThePasswordAgain_IsRateLimited(string route)
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapStorageEndpoints(this.dir);

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().Single(e => e.RoutePattern.RawText == route);

        Assert.Equal("reauth", endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
    }

    [Fact]
    public async Task Enable_WrongPasswordOrNoConfirmationOrNoKit_RefusedBeforeTouchingTheKey()
    {
        Assert.Equal(403, StatusOf(await this.Enable(new StorageEnableRequest("wrong", "ENCRYPT", AcceptNoRecoveryKit: true))));
        Assert.Equal(400, StatusOf(await this.Enable(new StorageEnableRequest(Password, "encrypt", AcceptNoRecoveryKit: true))));
        Assert.Equal(400, StatusOf(await this.Enable(new StorageEnableRequest(Password, "ENCRYPT"))));

        Assert.Null(this.secrets.TryGetTenantStorageKey("default"));
        await this.agent.DidNotReceive().EnableAsync(Arg.Any<StorageEncryptionEnableRequestDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enable_Confirmed_PushesTheKeyAndSendsItsFingerprint()
    {
        var result = await this.Enable(new StorageEnableRequest(Password, "ENCRYPT", EraseEvaluationArtifacts: false, AcceptNoRecoveryKit: true));

        Assert.Equal(202, StatusOf(result));
        var key = this.secrets.TryGetTenantStorageKey("default")!;
        await this.agent.Received(1).ProvideKeyAsync(key, Arg.Any<CancellationToken>());
        await this.agent.Received(1).EnableAsync(
            Arg.Is<StorageEncryptionEnableRequestDto>(r => r.KeyFingerprint == StorageKeyFingerprint.Compute(key) && !r.EraseEvaluationArtifacts),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enable_AgentHoldsEncryptedDataWithoutBridgeKey_ConflictAndNothingMinted()
    {
        this.agent.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new StorageEncryptionStatusDto { EncryptAtRest = true, HasEncryptedData = true });

        var result = await this.Enable(new StorageEnableRequest(Password, "ENCRYPT", AcceptNoRecoveryKit: true));

        Assert.Equal(409, StatusOf(result));
        Assert.Null(this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Enable_AgentRefusesTheKey_Conflict()
    {
        this.agent.ProvideKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.Equal(409, StatusOf(await this.Enable(new StorageEnableRequest(Password, "ENCRYPT", AcceptNoRecoveryKit: true))));
        await this.agent.DidNotReceive().EnableAsync(Arg.Any<StorageEncryptionEnableRequestDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportKit_ReturnsAKitThatOpensWithThePassphraseAndRecordsTheExport()
    {
        var result = await StorageEndpoints.ExportKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitExportRequest(Password, Passphrase), this.time, CancellationToken.None);

        var file = Assert.IsType<FileContentHttpResult>(result);
        var recovered = StorageRecoveryKit.Open(Encoding.UTF8.GetString(file.FileContents.ToArray()), Passphrase);
        Assert.Equal(this.secrets.TryGetTenantStorageKey("default"), recovered.Base64Key);
        Assert.Equal(this.time.GetUtcNow(), this.secrets.GetStorageKeyExportedAt("default"));
        Assert.StartsWith("cortex-recovery-kit-default-", file.FileDownloadName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportKit_WrongPasswordOrShortPassphrase_Refused()
    {
        Assert.Equal(403, StatusOf(await StorageEndpoints.ExportKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitExportRequest("wrong", Passphrase), this.time, CancellationToken.None)));
        Assert.Equal(400, StatusOf(await StorageEndpoints.ExportKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitExportRequest(Password, "short"), this.time, CancellationToken.None)));
        Assert.Null(this.secrets.GetStorageKeyExportedAt("default"));
    }

    [Fact]
    public async Task Restore_BridgeLostItsKey_InstallsTheKitKeyAndPushesIt()
    {
        var kit = KitFor("default", OtherKey);
        this.agent.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageEncryptionStatusDto { EncryptAtRest = true, HasEncryptedData = true, ExpectedKeyFingerprint = StorageKeyFingerprint.Compute(OtherKey) });

        var result = await StorageEndpoints.RestoreKitAsync("default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, Passphrase, kit), CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        Assert.True(BodyOf(result).GetProperty("applied").GetBoolean());
        Assert.Equal(OtherKey, this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Restore_WorkingKeyOnTheBridgeAndNoComplaintFromTheAgent_NotReplaced()
    {
        var current = this.secrets.GetOrCreateTenantStorageKey("default");

        var result = await StorageEndpoints.RestoreKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, Passphrase, KitFor("default", OtherKey)), CancellationToken.None);

        Assert.Equal(409, StatusOf(result));
        Assert.Equal(current, this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Restore_AgentDataBelongsToTheKitKey_ReplacesTheWrongBridgeKey()
    {
        this.secrets.GetOrCreateTenantStorageKey("default");
        this.agent.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageEncryptionStatusDto { EncryptAtRest = true, HasEncryptedData = true, ExpectedKeyFingerprint = StorageKeyFingerprint.Compute(OtherKey) });

        var result = await StorageEndpoints.RestoreKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, Passphrase, KitFor("default", OtherKey)), CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(OtherKey, this.secrets.TryGetTenantStorageKey("default"));
    }

    [Theory]
    [InlineData(StorageKeyStates.Rejected, "open")]
    [InlineData(StorageKeyStates.Absent, "failed")]
    public async Task Restore_TheAgentRejectsTheBridgeKeyForItsEncryptedData_ReplacesIt(string keyState, string messagesState)
    {
        this.secrets.GetOrCreateTenantStorageKey("default");
        this.agent.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageEncryptionStatusDto { EncryptAtRest = true, HasEncryptedData = true, KeyState = keyState, MessagesState = messagesState });

        var result = await StorageEndpoints.RestoreKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, Passphrase, KitFor("default", OtherKey)), CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(OtherKey, this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Restore_ExplicitOwnerOverride_ReplacesAWorkingKey()
    {
        this.secrets.GetOrCreateTenantStorageKey("default");

        var result = await StorageEndpoints.RestoreKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, Passphrase, KitFor("default", OtherKey), ReplaceExistingKey: true), CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal(OtherKey, this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Restore_OverrideWithoutThePassword_RefusedAndTheKeyKept()
    {
        var current = this.secrets.GetOrCreateTenantStorageKey("default");

        var result = await StorageEndpoints.RestoreKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest("wrong", Passphrase, KitFor("default", OtherKey), ReplaceExistingKey: true), CancellationToken.None);

        Assert.Equal(403, StatusOf(result));
        Assert.Equal(current, this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Restore_OverrideDoesNotBypassAnAgentThatNamesAnotherKey()
    {
        var current = this.secrets.GetOrCreateTenantStorageKey("default");
        this.agent.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageEncryptionStatusDto { EncryptAtRest = true, HasEncryptedData = true, ExpectedKeyFingerprint = StorageKeyFingerprint.Compute(current) });

        var result = await StorageEndpoints.RestoreKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, Passphrase, KitFor("default", OtherKey), ReplaceExistingKey: true), CancellationToken.None);

        Assert.Equal(409, StatusOf(result));
        Assert.Equal(current, this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Enable_AgentStatusUnavailable_ConflictAndNothingMinted()
    {
        this.agent.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new StorageEncryptionStatusDto { Unavailable = true, Error = "x" });

        var result = await this.Enable(new StorageEnableRequest(Password, "ENCRYPT", AcceptNoRecoveryKit: true));

        Assert.Equal(409, StatusOf(result));
        Assert.Null(this.secrets.TryGetTenantStorageKey("default"));
    }
    [Fact]
    public async Task Restore_KitOfAnotherTenantOrWrongPassphrase_Refused()
    {
        Assert.Equal(400, StatusOf(await StorageEndpoints.RestoreKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, Passphrase, KitFor("other", OtherKey)), CancellationToken.None)));
        Assert.Equal(400, StatusOf(await StorageEndpoints.RestoreKitAsync(
            "default", this.agent, this.secrets, Verify, new RecoveryKitRestoreRequest(Password, "wrong passphrase", KitFor("default", OtherKey)), CancellationToken.None)));
        Assert.Null(this.secrets.TryGetTenantStorageKey("default"));
    }

    [Fact]
    public async Task Status_AgentDisconnected_503WithTheBridgeView()
    {
        var key = this.secrets.GetOrCreateTenantStorageKey("default");

        var result = await StorageEndpoints.GetStatusAsync("default", null, this.secrets, this.dir, CancellationToken.None);

        Assert.Equal(503, StatusOf(result));
        var body = BodyOf(result);
        Assert.Equal(StorageKeyFingerprint.Compute(key), body.GetProperty("bridge").GetProperty("keyFingerprint").GetString());
        Assert.DoesNotContain(key, body.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(2, body.GetProperty("bridgeStores").GetArrayLength());
    }
}
