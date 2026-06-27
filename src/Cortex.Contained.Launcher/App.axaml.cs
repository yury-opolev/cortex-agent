using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Cortex.Contained.Launcher.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Cortex.Contained.Launcher;

#pragma warning disable CA1001 // Avalonia manages Application lifecycle and tray icon disposal
public sealed class App : Application
#pragma warning restore CA1001
{
    private TrayIcon? trayIcon;
    private NativeMenuItem? statusItem;
    private NativeMenuItem? startItem;
    private NativeMenuItem? stopItem;

    private DockerService? docker;
    private BridgeProcessService? bridge;
    private HealthMonitorService? health;
    private TokenHolder? holder;
    private ServiceProvider? serviceProvider;

    private CortexState currentState = CortexState.Stopped;

    // 0 = health monitor not running, 1 = running. Guards against starting
    // multiple monitor loops across repeated Start clicks; reset on Stop.
    private int monitorStarted;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            this.InitializeServices();
            this.SetupTrayIcon(desktop);

            // Graceful shutdown on process exit (MSIX upgrade, system shutdown, Ctrl+C)
            desktop.ShutdownRequested += (_, _) => this.GracefulShutdown();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => this.GracefulShutdown();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                this.GracefulShutdown();
                desktop.Shutdown();
            };

            // Auto-start
            _ = this.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog(dispose: true));
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<DockerService>();
        services.AddSingleton<BridgeProcessService>();
        services.AddSingleton<HealthMonitorService>(sp =>
            new HealthMonitorService(
                new HttpClient { Timeout = TimeSpan.FromSeconds(5) },
                sp.GetRequiredService<ILogger<HealthMonitorService>>()));

        this.serviceProvider = services.BuildServiceProvider();
        this.docker = this.serviceProvider.GetRequiredService<DockerService>();
        this.bridge = this.serviceProvider.GetRequiredService<BridgeProcessService>();
        this.health = this.serviceProvider.GetRequiredService<HealthMonitorService>();
        this.holder = new TokenHolder();

        this.health.HealthChanged += status =>
        {
            // Reflect live health in the tray status once startup has settled.
            // Only adjusts the monitoring states (Running/Error) — it never
            // overrides an in-progress Start (Starting) or a user-driven
            // Stop/Stopped. This is what lets the dot self-heal to Running when
            // the Bridge becomes healthy AFTER StartAsync's bootstrap wait has
            // already timed out (e.g. a slow Whisper model cold-load).
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (this.currentState is not (CortexState.Running or CortexState.Error))
                {
                    return;
                }

                var healthy = status.IsBridgeHealthy && status.IsAgentHealthy;
                this.UpdateState(healthy ? CortexState.Running : CortexState.Error);
            });
        };

        this.bridge.OnExited += exitCode =>
        {
            // Exit code 73 is the sentinel emitted by the Bridge when a
            // Web-UI-initiated restart was requested (see
            // Cortex.Contained.Bridge.Control.RestartCoordinator). The Launcher
            // respawns the Bridge child process so settings that require
            // startup wiring (LLM providers, channel singletons, Kestrel port)
            // pick up the persisted YAML. Any other exit code is treated as a
            // crash or normal stop.
            const int RestartExitCode = 73;
            if (exitCode == RestartExitCode)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    Log.Information("Bridge exited with code 73 (restart requested); respawning");
                    this.UpdateState(CortexState.Starting);

                    // Give Kestrel a moment to release port 5080 and the OS to
                    // tear down any half-open sockets before re-binding.
                    await Task.Delay(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(true);

                    try
                    {
                        var bridgeExe = FindBridgeExe();
                        var hubToken = Environment.GetEnvironmentVariable("CORTEX_HUB_TOKEN")
                                    ?? "dev-token-change-me";
                        this.bridge!.Start(bridgeExe, hubToken);
                        this.UpdateState(CortexState.Running);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to respawn Bridge after restart request");
                        this.UpdateState(CortexState.Error);
                    }
                });
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => this.UpdateState(CortexState.Error));
            }
        };
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();

        var versionItem = new NativeMenuItem($"Cortex {GetVersion()}") { IsEnabled = false };
        menu.Items.Add(versionItem);

        this.statusItem = new NativeMenuItem("Status: Stopped") { IsEnabled = false };
        menu.Items.Add(this.statusItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        var openWebUiItem = new NativeMenuItem("Open Web UI");
        openWebUiItem.Click += (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "http://127.0.0.1:5080",
                UseShellExecute = true,
            });
        };
        menu.Items.Add(openWebUiItem);

        this.startItem = new NativeMenuItem("Start");
        this.startItem.Click += (_, _) => _ = this.StartAsync();
        menu.Items.Add(this.startItem);

        this.stopItem = new NativeMenuItem("Stop");
        this.stopItem.Click += (_, _) => _ = this.StopAsync();
        menu.Items.Add(this.stopItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            _ = Task.Run(async () =>
            {
                await this.StopAsync().ConfigureAwait(false);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown());
            });
        };
        menu.Items.Add(exitItem);

        this.trayIcon = new TrayIcon
        {
            ToolTipText = "Cortex — Stopped",
            Menu = menu,
            IsVisible = true,
        };

        // Left click opens the Web UI (right click shows the menu)
        this.trayIcon.Clicked += (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "http://127.0.0.1:5080",
                UseShellExecute = true,
            });
        };

        this.UpdateTrayIconImage();
        this.UpdateMenuState();
    }

    private void UpdateState(CortexState state)
    {
        this.currentState = state;

        if (this.statusItem is not null)
        {
            this.statusItem.Header = $"Status: {state}";
        }

        if (this.trayIcon is not null)
        {
            this.trayIcon.ToolTipText = state switch
            {
                CortexState.Starting => "Cortex — Starting...",
                CortexState.Running => "Cortex — Running",
                CortexState.Stopping => "Cortex — Stopping...",
                CortexState.Stopped => "Cortex — Stopped",
                CortexState.Error => "Cortex — Error",
                _ => "Cortex",
            };
        }

        this.UpdateTrayIconImage();
        this.UpdateMenuState();
    }

    private void UpdateTrayIconImage()
    {
        if (this.trayIcon is null)
        {
            return;
        }

        var isLight = TrayIconRenderer.IsSystemLightTheme();
        var pngBytes = TrayIconRenderer.RenderPng(64, isLight, this.currentState);

        using var stream = new MemoryStream(pngBytes);
        this.trayIcon.Icon = new WindowIcon(stream);
    }

    private void UpdateMenuState()
    {
        var isStopped = this.currentState == CortexState.Stopped
                     || this.currentState == CortexState.Error;
        var isRunning = this.currentState == CortexState.Running;
        var isTransitioning = this.currentState == CortexState.Starting
                           || this.currentState == CortexState.Stopping;

        if (this.startItem is not null)
        {
            this.startItem.IsVisible = isStopped;
            this.startItem.IsEnabled = isStopped;
        }

        if (this.stopItem is not null)
        {
            this.stopItem.IsVisible = isRunning || isTransitioning;
            this.stopItem.IsEnabled = isRunning;
        }
    }

    private async Task StartAsync()
    {
        if (this.docker is null || this.bridge is null || this.health is null || this.holder is null)
        {
            return;
        }

        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.UpdateState(CortexState.Starting));

            if (!await this.docker.IsDockerAvailableAsync(this.holder.Cts.Token).ConfigureAwait(false))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    this.UpdateState(CortexState.Error);
                    Log.Warning("Docker is not available. Please start Docker Desktop.");
                });
                return;
            }

            // Only start/recreate containers if needed
            var needsCompose = true;
            try
            {
                var preCheck = await this.health.CheckHealthAsync(this.holder.Cts.Token).ConfigureAwait(false);
                if (preCheck.IsAgentHealthy)
                {
                    var outdated = await this.docker.IsContainerImageOutdatedAsync(
                        "cortex-agent", "cortex-agent:latest", this.holder.Cts.Token).ConfigureAwait(false);
                    if (outdated)
                    {
                        Log.Information("Agent image updated, recreating containers");
                    }
                    else
                    {
                        Log.Information("Agent already running with latest image, skipping container startup");
                        needsCompose = false;
                    }
                }
            }
            catch
            {
                // Agent not reachable — start containers
            }

            if (needsCompose)
            {
                var composeFile = FindComposeFile();
                await this.docker.StartContainersAsync(composeFile, this.holder.Cts.Token).ConfigureAwait(false);
            }

            await WaitForConditionAsync(
                async () =>
                {
                    var h = await this.health.CheckHealthAsync(this.holder.Cts.Token).ConfigureAwait(false);
                    return h.IsAgentHealthy;
                },
                TimeSpan.FromSeconds(60),
                this.holder.Cts.Token).ConfigureAwait(false);

            var bridgeExe = FindBridgeExe();
            var hubToken = Environment.GetEnvironmentVariable("CORTEX_HUB_TOKEN")
                        ?? "dev-token-change-me";
            this.bridge.Start(bridgeExe, hubToken);

            await WaitForConditionAsync(
                async () =>
                {
                    var h = await this.health.CheckHealthAsync(this.holder.Cts.Token).ConfigureAwait(false);
                    return h.IsBridgeHealthy;
                },
                // The Bridge cold start loads the Whisper STT model (~874 MB +
                // GPU init) and connects the Discord gateway before Kestrel binds
                // :5080, so /health can take well over 90 s to answer. Allow 180 s.
                TimeSpan.FromSeconds(180),
                this.holder.Cts.Token).ConfigureAwait(false);

            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.UpdateState(CortexState.Running));

            this.EnsureMonitorStarted();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start Cortex");

            // Startup didn't confirm health in time, but the services may still
            // be coming up. Start the monitor anyway so the tray self-heals to
            // Running once health is confirmed, instead of staying stuck on Error.
            this.EnsureMonitorStarted();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.UpdateState(CortexState.Error));
        }
    }

    private void GracefulShutdown()
    {
        try
        {
            Log.Information("Graceful shutdown triggered");
            this.bridge?.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token)
                .GetAwaiter().GetResult();

            var composeFile = FindComposeFile();
            this.docker?.StopContainersAsync(composeFile, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error during graceful shutdown");
        }
    }

    private async Task StopAsync()
    {
        if (this.docker is null || this.bridge is null || this.holder is null)
        {
            return;
        }

        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.UpdateState(CortexState.Stopping));

            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await this.bridge.StopAsync(stopCts.Token).ConfigureAwait(false);

            var composeFile = FindComposeFile();
            await this.docker.StopContainersAsync(composeFile, stopCts.Token).ConfigureAwait(false);

            this.holder.Reset();
            Interlocked.Exchange(ref this.monitorStarted, 0);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.UpdateState(CortexState.Stopped));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during shutdown");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.UpdateState(CortexState.Error));
        }
    }

    /// <summary>
    /// Starts the periodic health monitor once. The monitor drives the tray
    /// status via <see cref="HealthMonitorService.HealthChanged"/>, so it must
    /// run whether StartAsync confirmed health in time or timed out waiting.
    /// Reset on Stop so a later Start re-arms it against the fresh token.
    /// </summary>
    private void EnsureMonitorStarted()
    {
        if (this.health is null || this.holder is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref this.monitorStarted, 1) == 0)
        {
            _ = this.health.MonitorAsync(TimeSpan.FromSeconds(10), this.holder.Cts.Token);
        }
    }

    private static async Task WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeoutCts.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Condition not met within {timeout.TotalSeconds}s");
    }

    private static string FindComposeFile()
    {
        // Use a fixed location so docker compose doesn't recreate containers
        // when the MSIX version (and thus the base directory) changes.
        var cortexDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cortex");
        var stablePath = Path.Combine(cortexDataDir, "docker-compose.yml");

        // Copy from the app bundle to the stable location if newer or missing
        var baseDir = AppContext.BaseDirectory;
        var bundledPath = Path.Combine(baseDir, "docker-compose.yml");

        if (File.Exists(bundledPath))
        {
            Directory.CreateDirectory(cortexDataDir);
            File.Copy(bundledPath, stablePath, overwrite: true);
            return stablePath;
        }

        if (File.Exists(stablePath))
        {
            return stablePath;
        }

        var repoRoot = FindRepoRoot(baseDir);
        if (repoRoot is not null)
        {
            var repoPath = Path.Combine(repoRoot, "docker-compose.yml");
            if (File.Exists(repoPath))
            {
                return repoPath;
            }
        }

        throw new FileNotFoundException("docker-compose.yml not found");
    }

    private static string FindBridgeExe()
    {
        var baseDir = AppContext.BaseDirectory;

        var bridgePath = Path.Combine(baseDir, "Bridge", "Cortex.Contained.Bridge.exe");
        if (File.Exists(bridgePath))
        {
            return bridgePath;
        }

        var repoRoot = FindRepoRoot(baseDir);
        if (repoRoot is not null)
        {
            var devPath = Path.Combine(
                repoRoot, "src", "Cortex.Contained.Bridge", "bin", "Debug",
                "net10.0-windows", "Cortex.Contained.Bridge.exe");
            if (File.Exists(devPath))
            {
                return devPath;
            }
        }

        throw new FileNotFoundException("Cortex.Contained.Bridge.exe not found");
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = startDir;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static string GetVersion()
    {
        var versionFile = Path.Combine(AppContext.BaseDirectory, "version.json");
        if (File.Exists(versionFile))
        {
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(versionFile));
                var root = json.RootElement;
                var major = root.GetProperty("major").GetInt32();
                var minor = root.GetProperty("minor").GetInt32();
                var patch = root.GetProperty("patch").GetInt32();
                return $"v{major}.{minor}.{patch}";
            }
            catch
            {
                // Fall through
            }
        }

        return "v0.0.0";
    }
}
