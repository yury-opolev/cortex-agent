using System.Text.Json;
using Cortex.Contained.Bridge.Coding;

namespace Cortex.Contained.Bridge.Tests.Coding;

public sealed class CodingModelEndpointsTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ── KnownProviders ────────────────────────────────────────────────────────

    [Fact]
    public void KnownProviders_returns_three_entries()
    {
        var providers = CodingModelEndpoints.KnownProviders();
        Assert.Equal(3, providers.Count);
    }

    [Fact]
    public void KnownProviders_contains_expected_ids()
    {
        var providers = CodingModelEndpoints.KnownProviders();
        var ids = providers.Select(p => p.Id).ToList();
        Assert.Contains("claude", ids);
        Assert.Contains("copilot", ids);
        Assert.Contains("apikey", ids);
    }

    [Fact]
    public void KnownProviders_all_have_non_empty_labels()
    {
        var providers = CodingModelEndpoints.KnownProviders();
        Assert.All(providers, p => Assert.False(string.IsNullOrWhiteSpace(p.Label)));
    }

    // ── ValidateProvider ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude")]
    [InlineData("copilot")]
    [InlineData("apikey")]
    public void ValidateProvider_known_ids_return_ok(string id)
    {
        var (ok, error) = CodingModelEndpoints.ValidateProvider(id);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateProvider_null_or_empty_is_ok(string? id)
    {
        var (ok, error) = CodingModelEndpoints.ValidateProvider(id);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("openai")]
    [InlineData("CLAUDE")]
    public void ValidateProvider_unknown_id_returns_error(string id)
    {
        var (ok, error) = CodingModelEndpoints.ValidateProvider(id);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    // ── Serialization shape ───────────────────────────────────────────────────

    [Fact]
    public void CodaModelSettingsDto_serializes_with_camelCase_named_props()
    {
        var dto = new CodaModelSettingsDto(
            "copilot",
            "gpt-4o",
            [new CodaProviderOption("copilot", "GitHub Copilot")]);

        var json = JsonSerializer.Serialize(dto, SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Named camelCase props must be present
        Assert.True(root.TryGetProperty("provider", out _), "Missing 'provider'");
        Assert.True(root.TryGetProperty("model", out _), "Missing 'model'");
        Assert.True(root.TryGetProperty("availableProviders", out _), "Missing 'availableProviders'");

        // No ValueTuple artefacts
        Assert.False(root.TryGetProperty("Item1", out _), "Unexpected 'Item1' (ValueTuple leak)");
        Assert.False(root.TryGetProperty("Item2", out _), "Unexpected 'Item2' (ValueTuple leak)");
    }

    [Fact]
    public void CodaProviderOption_serializes_with_id_and_label()
    {
        var option = new CodaProviderOption("copilot", "GitHub Copilot");
        var json = JsonSerializer.Serialize(option, SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("id", out var idProp));
        Assert.Equal("copilot", idProp.GetString());
        Assert.True(root.TryGetProperty("label", out var labelProp));
        Assert.Equal("GitHub Copilot", labelProp.GetString());
    }

    [Fact]
    public void CodaModelSettingsDto_serializes_null_provider_and_model()
    {
        var dto = new CodaModelSettingsDto(null, null, []);
        var json = JsonSerializer.Serialize(dto, SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("provider", out var provProp));
        Assert.Equal(JsonValueKind.Null, provProp.ValueKind);
        Assert.True(root.TryGetProperty("model", out var modelProp));
        Assert.Equal(JsonValueKind.Null, modelProp.ValueKind);
    }
}
