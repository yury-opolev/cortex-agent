using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class CodingFoldersEndpointsTests
{
    private static readonly System.Text.Json.JsonSerializerOptions WebJson =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    // ── ValidateAddRequest ─────────────────────────────────────────────

    [Fact]
    public void ValidateAddRequest_AbsoluteExistingPath_ReturnsOk()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        var (ok, error) = CodingFoldersEndpoints.ValidateAddRequest(dir, "YoloSafe");

        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateAddRequest_NullPath_ReturnsError()
    {
        var (ok, error) = CodingFoldersEndpoints.ValidateAddRequest(null, "YoloSafe");

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("path", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAddRequest_EmptyPath_ReturnsError()
    {
        var (ok, error) = CodingFoldersEndpoints.ValidateAddRequest("", "YoloSafe");

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateAddRequest_RelativePath_ReturnsError()
    {
        var (ok, error) = CodingFoldersEndpoints.ValidateAddRequest("relative/path", "YoloSafe");

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("absolute", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAddRequest_NonExistentPath_ReturnsError()
    {
        var (ok, error) = CodingFoldersEndpoints.ValidateAddRequest(
            @"C:\definitely-does-not-exist-xyz-12345", "YoloSafe");

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("exist", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAddRequest_InvalidPolicy_ReturnsError()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        var (ok, error) = CodingFoldersEndpoints.ValidateAddRequest(dir, "NotAPolicy");

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("policy", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAddRequest_NullPolicy_DefaultsToYoloSafe()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        var (ok, error) = CodingFoldersEndpoints.ValidateAddRequest(dir, null);

        Assert.True(ok);
        Assert.Null(error);
    }

    // ── ParsePolicy ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Prompt", CodingPolicy.Prompt)]
    [InlineData("YoloSafe", CodingPolicy.YoloSafe)]
    [InlineData("Yolo", CodingPolicy.Yolo)]
    [InlineData(null, CodingPolicy.YoloSafe)]  // null → default
    [InlineData("", CodingPolicy.YoloSafe)]    // empty → default
    public void ParsePolicy_KnownValues_Succeeds(string? input, CodingPolicy expected)
    {
        var result = CodingFoldersEndpoints.ParsePolicy(input);

        Assert.Equal(expected, result);
    }

    // ── ToDto ─────────────────────────────────────────────────────────

    [Fact]
    public void ToDto_MapsAllFields_ExistsTrue()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var entry = new CodingFolderEntry
        {
            Path = dir,
            Label = "my-label",
            DefaultPolicy = CodingPolicy.Prompt,
        };

        var dto = CodingFoldersEndpoints.ToDto(entry);

        Assert.Equal(dir, dto.Path);
        Assert.Equal("my-label", dto.Label);
        Assert.Equal("Prompt", dto.Policy);
        Assert.True(dto.Exists);
    }

    [Fact]
    public void ToDto_NonExistentPath_ExistsFalse()
    {
        var entry = new CodingFolderEntry
        {
            Path = @"C:\does-not-exist-xyz-99999",
            DefaultPolicy = CodingPolicy.YoloSafe,
        };

        var dto = CodingFoldersEndpoints.ToDto(entry);

        Assert.False(dto.Exists);
    }

    // ── AuthStatus ────────────────────────────────────────────────────

    [Fact]
    public void AuthStatus_BogusPath_BinaryFoundFalse_NonEmptyHint()
    {
        var result = CodingFoldersEndpoints.AuthStatus(@"C:\bogus-coda-path-xyz\coda.exe");

        Assert.False(result.BinaryFound);
        Assert.False(string.IsNullOrWhiteSpace(result.Hint));
    }

    [Fact]
    public void AuthStatus_ExistingFile_BinaryFoundTrue()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var result = CodingFoldersEndpoints.AuthStatus(tmp);

            Assert.True(result.BinaryFound);
            Assert.False(string.IsNullOrWhiteSpace(result.Hint));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void AuthStatus_HintNeverEmpty_ForCodaDefault()
    {
        var result = CodingFoldersEndpoints.AuthStatus("coda");

        // binaryFound may be true or false depending on dev PATH; hint is always set
        Assert.False(string.IsNullOrWhiteSpace(result.Hint));
    }

    // ── JSON serialization shape (regression guard for the ValueTuple bug) ──

    [Fact]
    public void AuthStatus_SerializesWithCamelCaseNamedProperties()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                CodingFoldersEndpoints.AuthStatus(tmp), WebJson);

            // The web UI reads codaAuthStatus.binaryFound / .hint — must be present and named.
            Assert.Contains("\"binaryFound\":true", json);
            Assert.Contains("\"hint\":", json);
            Assert.DoesNotContain("Item1", json);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void ToDto_SerializesWithNamedPropertiesAndPolicyAsString()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var entry = new CodingFolderEntry { Path = dir, Label = "lbl", DefaultPolicy = CodingPolicy.YoloSafe };

        var json = System.Text.Json.JsonSerializer.Serialize(
            CodingFoldersEndpoints.ToDto(entry), WebJson);

        // The web UI reads f.path / f.label / f.policy (string 'YoloSafe') / f.exists.
        Assert.Contains("\"path\":", json);
        Assert.Contains("\"policy\":\"YoloSafe\"", json);
        Assert.Contains("\"exists\":true", json);
        Assert.DoesNotContain("Item1", json);
    }
}
