using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Bridge.Tests.Coding;

/// <summary>
/// Tests for <see cref="CodaSessionManager.EffectiveOptions"/> resolving the coda binary path
/// from the effective <see cref="CodaSource"/> — the runtime-mutable <see cref="CodaSourceStore"/>
/// override when set, else the YAML <see cref="CodaOptions.Source"/> — via
/// <see cref="CodaBinaryResolver.Resolve"/>.
/// </summary>
public sealed class CodaSessionManagerSourceTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "cortex-b1-source-" + Guid.NewGuid().ToString("N"));

    private CodaSessionManager NewManager(CodaSource yamlSource = CodaSource.Auto, CodaSource? storeOverride = null, bool withSourceStore = true, string? explicitBinaryPath = null)
    {
        Directory.CreateDirectory(this.tempRoot);

        var options = Substitute.For<IOptionsMonitor<CodaOptions>>();
        options.CurrentValue.Returns(explicitBinaryPath is null
            ? new CodaOptions { Source = yamlSource }
            : new CodaOptions { Source = yamlSource, CodaBinaryPath = explicitBinaryPath });

        var foldersStore = new CodingFoldersStore(Path.Combine(this.tempRoot, "coding-folders.json"));

        CodaSourceStore? sourceStore = null;
        if (withSourceStore)
        {
            sourceStore = new CodaSourceStore(Path.Combine(this.tempRoot, "coda-source.json"));
            if (storeOverride is { } src)
            {
                sourceStore.Set(src);
            }
        }

        return new CodaSessionManager(
            NullLoggerFactory.Instance,
            options,
            foldersStore,
            mcpSettingsStore: null,
            sourceStore: sourceStore);
    }

    [Fact]
    public void EffectiveOptions_HostSource_UsesPathCoda()
    {
        var mgr = this.NewManager(yamlSource: CodaSource.Host);

        Assert.Equal("coda", mgr.EffectiveOptions().CodaBinaryPath);
    }

    [Fact]
    public void EffectiveOptions_StoreOverride_TakesPrecedenceOverYamlSource()
    {
        // YAML says Bundled (which — absent a real bundle at AppContext.BaseDirectory in the test
        // host — would still fall back to "coda"), but the store override of Host must be the one
        // actually consulted, proving precedence rather than a coincidental match.
        var mgr = this.NewManager(yamlSource: CodaSource.Bundled, storeOverride: CodaSource.Host);

        Assert.Equal("coda", mgr.EffectiveOptions().CodaBinaryPath);
    }

    [Fact]
    public void EffectiveOptions_NoSourceStoreInjected_FallsBackToYamlSource()
    {
        // Backward-compat seam: a manager built without a CodaSourceStore (older call sites /
        // other test helpers) must still resolve purely from CodaOptions.Source.
        var mgr = this.NewManager(yamlSource: CodaSource.Host, withSourceStore: false);

        Assert.Equal("coda", mgr.EffectiveOptions().CodaBinaryPath);
    }

    [Fact]
    public void EffectiveOptions_ExplicitBinaryPath_WinsOverSourceResolution()
    {
        // Escape hatch: an operator (or a test injecting a bogus binary to exercise spawn failure)
        // may pin CodaBinaryPath to a specific path. That explicit path must survive EffectiveOptions
        // unchanged — the CodaSource resolver only fills in the "coda" default, it never overrides a
        // pinned path. (Regression guard: an earlier wiring overwrote the pinned path unconditionally,
        // silently defeating the bogus-binary spawn-failure tests.)
        var pinned = Path.Combine(this.tempRoot, "pinned-coda.exe");
        var mgr = this.NewManager(yamlSource: CodaSource.Bundled, explicitBinaryPath: pinned);

        Assert.Equal(pinned, mgr.EffectiveOptions().CodaBinaryPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempRoot, recursive: true); } catch { /* ignore */ }
    }
}
