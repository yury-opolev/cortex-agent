"use strict";

/**
 * Restart-on-save UX. Used by settings pages whose saves require a Bridge
 * process restart to take effect (LLM providers, channel singletons, Web UI
 * port). The Bridge side is RestartCoordinator + POST /api/control/restart-bridge
 * (exit code 73); the Launcher side respawns the child process when it sees
 * that code. This module owns the browser-side flow: confirm, fire the restart
 * endpoint, poll /health, and reload the page once the new Bridge is up.
 *
 * Usage:
 *
 *   // Show the modal asking "settings changed — restart now or later?"
 *   // and, if the user picks Restart, runs the full restart-and-reload flow.
 *   cortexRestart.promptAndRestart({
 *       reason: "LLM provider changes require a restart.",
 *   });
 */
(function () {
    const HEALTH_POLL_INTERVAL_MS = 1000;
    const HEALTH_POLL_TIMEOUT_MS = 60_000;

    function $modal() {
        return document.getElementById("cortexRestartModal");
    }

    function setText(id, text) {
        const el = document.getElementById(id);
        if (el) el.textContent = text;
    }

    function setVisible(id, visible) {
        const el = document.getElementById(id);
        if (!el) return;
        el.style.display = visible ? "" : "none";
    }

    function showModal({ reason }) {
        // Render the "confirm restart" body. Buttons are wired in modal markup.
        setText("cortexRestartReason", reason || "Some settings require a restart to take effect.");
        setVisible("cortexRestartConfirmBody", true);
        setVisible("cortexRestartProgressBody", false);

        const modalEl = $modal();
        if (!modalEl || !window.bootstrap) {
            // Fallback: confirm() if Bootstrap isn't loaded yet (shouldn't happen
            // in production, but keeps unit-mode dev pages usable).
            return Promise.resolve(window.confirm(reason + "\n\nRestart now?"));
        }

        return new Promise((resolve) => {
            const modal = window.bootstrap.Modal.getOrCreateInstance(modalEl, {
                backdrop: "static",
                keyboard: false,
            });

            const onConfirm = () => {
                cleanup();
                resolve(true);
            };
            const onCancel = () => {
                cleanup();
                modal.hide();
                resolve(false);
            };

            function cleanup() {
                document.getElementById("cortexRestartConfirmBtn")?.removeEventListener("click", onConfirm);
                document.getElementById("cortexRestartCancelBtn")?.removeEventListener("click", onCancel);
            }

            document.getElementById("cortexRestartConfirmBtn")?.addEventListener("click", onConfirm, { once: true });
            document.getElementById("cortexRestartCancelBtn")?.addEventListener("click", onCancel, { once: true });

            modal.show();
        });
    }

    function showProgress() {
        setVisible("cortexRestartConfirmBody", false);
        setVisible("cortexRestartProgressBody", true);
        setText("cortexRestartProgressMessage", "Restarting Cortex…");
    }

    async function postRestart() {
        const resp = await fetch("/api/control/restart-bridge", {
            method: "POST",
            credentials: "same-origin",
        });
        // 202 Accepted is the expected response; the Bridge will exit shortly
        // after returning it. Anything else means we shouldn't start polling.
        if (resp.status !== 202 && !resp.ok) {
            throw new Error(`Restart request failed: HTTP ${resp.status}`);
        }
    }

    async function pollHealth() {
        const start = Date.now();
        let everSawDown = false;

        while (Date.now() - start < HEALTH_POLL_TIMEOUT_MS) {
            try {
                const resp = await fetch("/health", {
                    method: "GET",
                    cache: "no-store",
                });
                const body = await resp.json().catch(() => null);

                // Wait until we've seen at least one failure (the old process
                // is gone) AND then a healthy response (the new process is up).
                // Otherwise the very first poll could match the old Bridge that
                // hasn't shut down yet, and we'd reload prematurely.
                if (resp.ok && body && body.healthy === true) {
                    if (everSawDown) {
                        return true;
                    }
                    // First poll, possibly still the old Bridge — keep going.
                } else {
                    everSawDown = true;
                }
            } catch (e) {
                // fetch failed (Bridge is down) — that's the expected
                // intermediate state during the restart.
                everSawDown = true;
            }

            setText("cortexRestartProgressMessage",
                `Restarting Cortex… ${Math.round((Date.now() - start) / 1000)}s`);
            await new Promise((r) => setTimeout(r, HEALTH_POLL_INTERVAL_MS));
        }

        return false;
    }

    async function promptAndRestart({ reason } = {}) {
        const confirmed = await showModal({ reason });
        if (!confirmed) {
            return false;
        }

        try {
            showProgress();
            await postRestart();
            const ok = await pollHealth();
            if (ok) {
                // New Bridge is up. Soft-reload so the UI reads fresh settings
                // shape from the server.
                window.location.reload();
                return true;
            }
            setText("cortexRestartProgressMessage",
                "Cortex did not come back within 60 seconds. Try reloading manually.");
            return false;
        } catch (e) {
            setText("cortexRestartProgressMessage", "Restart failed: " + (e.message || e));
            return false;
        }
    }

    // Public API
    window.cortexRestart = {
        promptAndRestart,
    };
})();
