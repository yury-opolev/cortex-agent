"use strict";

// SECURITY: All connector-supplied fields (displayName, key, instanceId, channelId, remoteEndpoint)
// are UNTRUSTED input and MUST be rendered with x-text or :title, NEVER with x-html or by
// string-concatenating into markup. This is enforced throughout this file and in the corresponding
// app.html tab panel.

/**
 * Alpine component for the "Connectors" tab in Global Settings.
 *
 * Lists paired connector plugins with live attach status, supports the master kill-switch,
 * per-connector enable toggles, revoke, and approve/deny of pending pairing requests
 * (the human verifies the displayed code out-of-band before approving).
 *
 * Security: the connector's durable token is never returned by the API — the list projection
 * only ever carries non-secret metadata.
 */
function connectorsPage() {
    return {
        // ── State ──────────────────────────────────────────
        loading: true,
        masterEnabled: true,
        savingMaster: false,
        connectors: [],
        pending: [],
        _pollTimer: null,

        // ── Lifecycle ──────────────────────────────────────
        async init() {
            await this.load();
            this._pollTimer = setInterval(() => {
                if (document.visibilityState === "visible") {
                    this.load();
                }
            }, 5000);
        },

        destroy() {
            if (this._pollTimer) {
                clearInterval(this._pollTimer);
                this._pollTimer = null;
            }
        },

        async load() {
            this.loading = true;
            try {
                const data = await api.get("/api/connectors");
                this.masterEnabled = !!data.enabled;
                this.connectors = data.connectors || [];
                this.pending = data.pending || [];
            } catch (e) {
                Alpine.store("toast").error("Failed to load connectors: " + e.message);
            } finally {
                this.loading = false;
            }
        },

        // ── Master toggle ──────────────────────────────────
        async saveMaster() {
            this.savingMaster = true;
            try {
                const data = await api.post("/api/connectors/toggle", { enabled: this.masterEnabled });
                this.masterEnabled = !!data.enabled;
                Alpine.store("toast").success("Connectors " + (data.enabled ? "enabled" : "disabled"));
                await this.load();
            } catch (e) {
                Alpine.store("toast").error("Failed to save: " + e.message);
                await this.load();
            } finally {
                this.savingMaster = false;
            }
        },

        // ── Per-connector enable toggle (live) ─────────────
        async toggleConnector(c) {
            try {
                await api.put("/api/connectors/" + encodeURIComponent(c.channelId), { enabled: c.enabled });
                Alpine.store("toast").success((c.enabled ? "Enabled" : "Disabled") + " " + c.channelId);
                await this.load();
            } catch (e) {
                Alpine.store("toast").error("Failed to save: " + e.message);
                await this.load();
            }
        },

        async revoke(c) {
            if (!confirm("Revoke connector '" + c.channelId + "'? The connector will need to pair again.")) {
                return;
            }
            try {
                await api.del("/api/connectors/" + encodeURIComponent(c.channelId));
                Alpine.store("toast").success("Revoked " + c.channelId);
                await this.load();
            } catch (e) {
                Alpine.store("toast").error("Failed to revoke: " + e.message);
            }
        },

        // ── Pairing decisions ──────────────────────────────
        async approve(p) {
            try {
                await api.post("/api/connectors/pairing/" + encodeURIComponent(p.requestId) + "/approve");
                Alpine.store("toast").success("Approved pairing for " + p.channelId);
                await this.load();
            } catch (e) {
                Alpine.store("toast").error("Failed to approve: " + e.message);
                await this.load();
            }
        },

        async deny(p) {
            try {
                await api.post("/api/connectors/pairing/" + encodeURIComponent(p.requestId) + "/deny");
                Alpine.store("toast").success("Denied pairing for " + p.channelId);
                await this.load();
            } catch (e) {
                Alpine.store("toast").error("Failed to deny: " + e.message);
                await this.load();
            }
        },

        // ── Status badge helper ────────────────────────────
        statusBadgeClass(status) {
            switch (status) {
                case "connected":  return "bg-success";
                case "disabled":   return "bg-secondary";
                default:           return "bg-warning text-dark";
            }
        },
    };
}
