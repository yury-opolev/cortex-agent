using Cortex.Contained.Speech.Tts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Speech.Tests;

public class ModelProvisionerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceToolsDir;
    private readonly string _sourceAccentorDir;
    private readonly string _targetDir;

    public ModelProvisionerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cortex-provisioner-test-{Guid.NewGuid():N}");
        _sourceToolsDir = Path.Combine(_tempDir, "source", "tools");
        _sourceAccentorDir = Path.Combine(_tempDir, "source", "accentor");
        _targetDir = Path.Combine(_tempDir, "target");

        Directory.CreateDirectory(_sourceToolsDir);
        Directory.CreateDirectory(_sourceAccentorDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void EnsureSileroModel_CopiesWhenMissing()
    {
        // Arrange — create source model file
        File.WriteAllText(Path.Combine(_sourceToolsDir, "silero_v5_jit.pt"), "fake model");
        File.WriteAllText(Path.Combine(_sourceAccentorDir, "stress_map.json"), "{}");

        // Act
        ModelProvisioner.EnsureSileroModel(_targetDir, _sourceToolsDir, _sourceAccentorDir, NullLogger.Instance);

        // Assert
        Assert.True(File.Exists(Path.Combine(_targetDir, "silero_v5_jit.pt")));
        Assert.True(File.Exists(Path.Combine(_targetDir, "accentor", "stress_map.json")));
    }

    [Fact]
    public void EnsureSileroModel_SkipsWhenPresent()
    {
        // Arrange — target already has the model
        Directory.CreateDirectory(_targetDir);
        File.WriteAllText(Path.Combine(_targetDir, "silero_v5_jit.pt"), "existing model");
        File.WriteAllText(Path.Combine(_sourceToolsDir, "silero_v5_jit.pt"), "new model");

        // Act
        ModelProvisioner.EnsureSileroModel(_targetDir, _sourceToolsDir, _sourceAccentorDir, NullLogger.Instance);

        // Assert — original content preserved
        var content = File.ReadAllText(Path.Combine(_targetDir, "silero_v5_jit.pt"));
        Assert.Equal("existing model", content);
    }

    [Fact]
    public void EnsureSileroModel_NoSubmodule_DoesNothing()
    {
        var missingSourceDir = Path.Combine(_tempDir, "nonexistent");

        // Act — should not throw
        ModelProvisioner.EnsureSileroModel(_targetDir, missingSourceDir, missingSourceDir, NullLogger.Instance);

        // Assert — target dir may or may not exist, but no model file
        Assert.False(File.Exists(Path.Combine(_targetDir, "silero_v5_jit.pt")));
    }
}
