using Cortex.Contained.Bridge.Logging;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps operational/control endpoints: UI telemetry ingress (<c>/api/telemetry/ui</c>) and
/// the Bridge restart trigger (<c>/api/control/restart-bridge</c>). Both require authorization.
/// </summary>
internal static class ControlEndpoints
{
    /// <summary>Maps the telemetry and control endpoints onto <paramref name="app"/>.</summary>
    public static void MapControlEndpoints(this WebApplication app)
    {
        // --- UI telemetry ingress ---
        // The web UI POSTs structured events here (load start/success/failure, refresh
        // before/after model counts, etc.) so we can correlate UI behavior with
        // server-side activity in the bridge log without asking the user to open
        // browser devtools. Events land as structured log entries under the
        // "Cortex.Contained.Bridge.UITelemetry" category.
        app.MapPost("/api/telemetry/ui", (UiTelemetryEvent evt, ILoggerFactory loggerFactory) =>
        {
            if (string.IsNullOrWhiteSpace(evt.Source) || string.IsNullOrWhiteSpace(evt.Event))
            {
                return Results.Json(new { error = "source and event are required" }, statusCode: 400);
            }

            var uiLogger = loggerFactory.CreateLogger("Cortex.Contained.Bridge.UITelemetry");
            var props = evt.Properties?.ToString() ?? "{}";
            // Warning level for anything named *.error / *.failure so it surfaces in the
            // default log view; everything else is Information.
            if (evt.Event.EndsWith(".error", StringComparison.Ordinal) || evt.Event.EndsWith(".failure", StringComparison.Ordinal))
            {
                BridgeLogMessages.LogUiTelemetryWarn(uiLogger, evt.Source, evt.Event, props);
            }
            else
            {
                BridgeLogMessages.LogUiTelemetryInfo(uiLogger, evt.Source, evt.Event, props);
            }
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // --- Control: restart the Bridge process so settings that require startup
        //     wiring (LLM providers, channel singletons, Kestrel port) take effect.
        //     The Launcher's BridgeProcessService respawns the process when it sees
        //     exit code 73 (RestartCoordinator.RestartExitCode). The Web UI calls
        //     this AFTER persisting the new settings, then polls /health to know when
        //     the Bridge is back. See docs/superpowers/specs/restart-on-save-ux.
        app.MapPost("/api/control/restart-bridge", (
            Cortex.Contained.Bridge.Control.RestartCoordinator coord,
            IHostApplicationLifetime lifetime,
            ILoggerFactory loggerFactory) =>
        {
            var l = loggerFactory.CreateLogger("Cortex.Contained.Bridge.RestartEndpoint");
            BridgeLogMessages.LogRestartRequested(l);
            coord.RequestRestart();

            // Defer the stop briefly so this 202 response can flush to the browser
            // before Kestrel starts draining. Fire-and-forget is intentional — the
            // request handler must return now.
            _ = Task.Run(async () =>
            {
                await Task.Delay(500).ConfigureAwait(false);
                lifetime.StopApplication();
            });

            return Results.Accepted();
        }).RequireAuthorization();
    }
}
