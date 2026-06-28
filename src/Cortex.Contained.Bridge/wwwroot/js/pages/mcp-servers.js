"use strict";

/**
 * Alpine component for the "MCP Servers" tab in Global Settings.
 *
 * Lists configured MCP servers with live status + tool counts, supports add/edit/delete,
 * master + per-server enable toggles (live, like the speech toggles), an OAuth "Connect" flow
 * (opens the host browser, then polls until connected), per-tool allow-list checkboxes, and a
 * "Test connect" handshake that surfaces discovered tools or the server's error/stderr.
 *
 * Security: the secret field is WRITE-ONLY — it is never pre-filled or echoed back from the server.
 * The list/projection responses only ever carry `hasSecret` / `secretRef`, never a secret value.
 */
function mcpServersPage() {
    return {
        // ── State ──────────────────────────────────────────
        loading: true,
        masterEnabled: true,
        savingMaster: false,
        servers: [],

        // Add/edit modal
        showModal: false,
        editing: false,
        saving: false,
        modalError: "",
        form: emptyMcpForm(),

        // Per-server transient UI state, keyed by server key
        testing: {},        // key -> bool
        testResults: {},    // key -> { ok, tools?, error? }
        connecting: {},     // key -> bool
        _pollTimer: null,

        // ── Lifecycle ──────────────────────────────────────
        async init() {
            await this.load();
        },

        async load() {
            this.loading = true;
            try {
                const data = await api.get("/api/mcp/servers");
                this.masterEnabled = !!data.enabled;
                this.servers = data.servers || [];
            } catch (e) {
                Alpine.store("toast").error("Failed to load MCP servers: " + e.message);
            } finally {
                this.loading = false;
            }
        },

        // ── Master toggle ──────────────────────────────────
        async saveMaster() {
            this.savingMaster = true;
            try {
                const data = await api.post("/api/mcp/toggle", { enabled: this.masterEnabled });
                this.masterEnabled = !!data.enabled;
                Alpine.store("toast").success("MCP " + (data.enabled ? "enabled" : "disabled"));
                await this.load();
            } catch (e) {
                Alpine.store("toast").error("Failed to save: " + e.message);
                await this.load();
            } finally {
                this.savingMaster = false;
            }
        },

        // ── Per-server enable toggle (live) ────────────────
        async toggleServer(s) {
            try {
                await api.put("/api/mcp/servers/" + encodeURIComponent(s.key), { enabled: s.enabled });
                Alpine.store("toast").success(s.key + " " + (s.enabled ? "enabled" : "disabled"));
                await this.load();
            } catch (e) {
                Alpine.store("toast").error("Failed to save: " + e.message);
                await this.load();
            }
        },

        // ── Status badge helpers ───────────────────────────
        statusBadgeClass(status) {
            switch (status) {
                case "connected":  return "bg-success";
                case "error":      return "bg-danger";
                case "needsLogin": return "bg-warning text-dark";
                case "connecting": return "bg-info text-dark";
                default:           return "bg-secondary";
            }
        },
        statusLabel(status) {
            switch (status) {
                case "connected":  return "Connected";
                case "error":      return "Error";
                case "needsLogin": return "Needs login";
                case "connecting": return "Connecting";
                case "disabled":   return "Disabled";
                default:           return "Disconnected";
            }
        },
        canConnect(s) {
            return s.transport === "http" && (s.auth === "oauth" || s.auth === "auto");
        },

        // ── Add / Edit modal ───────────────────────────────
        openAdd() {
            this.editing = false;
            this.modalError = "";
            this.form = emptyMcpForm();
            this.showModal = true;
        },
        openEdit(s) {
            this.editing = true;
            this.modalError = "";
            this.form = {
                key: s.key,
                enabled: s.enabled,
                transport: s.transport || "stdio",
                url: s.url || "",
                command: s.command || "",
                argsText: (s.args || []).join("\n"),
                envText: Object.entries(s.env || {}).map(([k, v]) => k + "=" + v).join("\n"),
                auth: s.auth || "auto",
                apiKeyHeader: s.apiKeyHeader || "",
                secret: "",                 // write-only: never pre-filled
                hasSecret: !!s.hasSecret,
                toolAllowList: [...(s.toolAllowList || [])],
                tools: [...(s.tools || [])],
            };
            this.showModal = true;
        },

        // Allow-list: empty list == no restriction (all tools exposed).
        isAllowed(tool) {
            return this.form.toolAllowList.length === 0 || this.form.toolAllowList.includes(tool);
        },
        toggleAllowTool(tool) {
            let list = this.form.toolAllowList;
            if (list.length === 0) {
                // Was "all" — switch to an explicit list of everything except this tool.
                list = this.form.tools.filter(t => t !== tool);
            } else if (list.includes(tool)) {
                list = list.filter(t => t !== tool);
            } else {
                list = [...list, tool];
            }
            this.form.toolAllowList = list;
        },
        allowAll() {
            this.form.toolAllowList = [];
        },

        async saveServer() {
            this.saving = true;
            this.modalError = "";
            try {
                const payload = {
                    enabled: this.form.enabled,
                    transport: this.form.transport,
                    url: this.form.transport === "http" ? this.form.url.trim() : null,
                    command: this.form.transport === "stdio" ? this.form.command.trim() : null,
                    args: parseLines(this.form.argsText),
                    env: parseEnv(this.form.envText),
                    auth: this.form.auth,
                    apiKeyHeader: this.form.apiKeyHeader.trim() || null,
                    toolAllowList: this.form.toolAllowList,
                };
                // Secret is sent ONLY when the user typed one (write-only). Blank = leave unchanged.
                if (this.form.secret && this.form.secret.length > 0) {
                    payload.secret = this.form.secret;
                }

                if (this.editing) {
                    await api.put("/api/mcp/servers/" + encodeURIComponent(this.form.key), payload);
                } else {
                    payload.key = this.form.key.trim();
                    await api.post("/api/mcp/servers", payload);
                }

                this.showModal = false;
                Alpine.store("toast").success("Saved " + this.form.key);
                await this.load();
            } catch (e) {
                this.modalError = e.message;
            } finally {
                this.saving = false;
            }
        },

        async deleteServer(s) {
            if (!confirm("Delete MCP server '" + s.key + "'? Its stored secret is removed too.")) {
                return;
            }
            try {
                await api.del("/api/mcp/servers/" + encodeURIComponent(s.key));
                Alpine.store("toast").success("Deleted " + s.key);
                await this.load();
            } catch (e) {
                Alpine.store("toast").error("Failed to delete: " + e.message);
            }
        },

        // ── Test connect ───────────────────────────────────
        async testServer(s) {
            this.testing[s.key] = true;
            this.testResults[s.key] = null;
            try {
                const data = await api.post("/api/mcp/servers/" + encodeURIComponent(s.key) + "/test");
                this.testResults[s.key] = data;
                if (data.ok) {
                    Alpine.store("toast").success(s.key + ": " + (data.tools || []).length + " tools discovered");
                } else {
                    Alpine.store("toast").error(s.key + " test failed");
                }
            } catch (e) {
                this.testResults[s.key] = { ok: false, error: e.message };
                Alpine.store("toast").error("Test failed: " + e.message);
            } finally {
                this.testing[s.key] = false;
            }
        },

        // ── OAuth connect ──────────────────────────────────
        async connectServer(s) {
            this.connecting[s.key] = true;
            try {
                const data = await api.post("/api/mcp/servers/" + encodeURIComponent(s.key) + "/connect");
                if (!data.browserOpened && data.authUrl) {
                    // Browser couldn't be opened on the host — offer the URL for manual opening.
                    window.open(data.authUrl, "_blank");
                }
                Alpine.store("toast").info("Complete sign-in in the browser, then this updates automatically.");
                this._pollUntilConnected(s.key);
            } catch (e) {
                this.connecting[s.key] = false;
                Alpine.store("toast").error("Connect failed: " + e.message);
            }
        },

        _pollUntilConnected(key) {
            let attempts = 0;
            const maxAttempts = 60; // ~2 minutes at 2s
            if (this._pollTimer) {
                clearInterval(this._pollTimer);
            }
            this._pollTimer = setInterval(async () => {
                attempts++;
                await this.load();
                const s = this.servers.find(x => x.key === key);
                if (!s || s.status === "connected" || attempts >= maxAttempts) {
                    clearInterval(this._pollTimer);
                    this._pollTimer = null;
                    this.connecting[key] = false;
                    if (s && s.status === "connected") {
                        Alpine.store("toast").success(key + " connected");
                    }
                }
            }, 2000);
        },
    };
}

function emptyMcpForm() {
    return {
        key: "",
        enabled: true,
        transport: "stdio",
        url: "",
        command: "",
        argsText: "",
        envText: "",
        auth: "auto",
        apiKeyHeader: "",
        secret: "",
        hasSecret: false,
        toolAllowList: [],
        tools: [],
    };
}

/** Splits a textarea into trimmed, non-empty lines. */
function parseLines(text) {
    return (text || "")
        .split("\n")
        .map(l => l.trim())
        .filter(l => l.length > 0);
}

/** Parses `KEY=VALUE` lines into an object. */
function parseEnv(text) {
    const out = {};
    for (const line of parseLines(text)) {
        const idx = line.indexOf("=");
        if (idx > 0) {
            out[line.slice(0, idx).trim()] = line.slice(idx + 1).trim();
        }
    }
    return out;
}
