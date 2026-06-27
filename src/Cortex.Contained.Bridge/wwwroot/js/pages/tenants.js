"use strict";

/**
 * Tenants list page. Shows all tenants with Discord + API status.
 */
function tenantsPage() {
    return {
        tenants: [],
        loading: true,
        showAddModal: false,
        showDeleteModal: false,
        deleteTarget: null,

        // Add tenant form
        form: { tenantId: "", endpoint: "", port: 0, enabled: true, apiEnabled: false },

        async init() {
            await this.load();
        },

        async load() {
            this.loading = true;
            try {
                this.tenants = await api.get("/api/tenants") || [];
            } catch (e) {
                Alpine.store("toast").error("Failed to load tenants: " + e.message);
            }
            this.loading = false;
        },

        statusBadge(t) {
            const map = {
                Connected:    "bg-success",
                Disconnected: "bg-secondary",
                Unhealthy:    "bg-warning text-dark",
                Error:        "bg-danger",
                Unreachable:  "bg-danger",
                IdleStopped:  "bg-info text-dark",
                Unknown:      "bg-secondary",
            };
            return map[t.status] || "bg-secondary";
        },

        openTenant(id) {
            Alpine.store("router").navigate(`/tenants/${encodeURIComponent(id)}/settings`);
        },

        async setDefault(id) {
            try {
                await api.post(`/api/tenants/${encodeURIComponent(id)}/default`);
                Alpine.store("toast").success("Default tenant updated");
                await this.load();
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },

        confirmDelete(t) {
            this.deleteTarget = t;
            this.showDeleteModal = true;
        },

        async doDelete() {
            if (!this.deleteTarget) return;
            const id = this.deleteTarget.tenantId;
            try {
                if (this.deleteTarget.isDefault) {
                    await api.post(`/api/tenants/${encodeURIComponent(id)}/reset`);
                    Alpine.store("toast").success("Tenant data cleared");
                } else {
                    await api.del(`/api/tenants/${encodeURIComponent(id)}`);
                    Alpine.store("toast").success("Tenant deleted");
                }
                this.showDeleteModal = false;
                this.deleteTarget = null;
                await this.load();
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },

        async addTenant() {
            if (!this.form.tenantId.trim()) return;
            try {
                await api.post("/api/tenants", {
                    tenantId: this.form.tenantId.trim(),
                    endpoint: this.form.endpoint || "",
                    port: this.form.port || 0,
                    enabled: this.form.enabled,
                    apiEnabled: this.form.apiEnabled,
                });
                Alpine.store("toast").success("Tenant created");
                this.showAddModal = false;
                this.form = { tenantId: "", endpoint: "", port: 0, enabled: true, apiEnabled: false };
                await this.load();
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },

        async generateSetupCode(t) {
            try {
                const data = await api.post(`/api/tenants/${encodeURIComponent(t.tenantId)}/setup-code`);
                t.hasSetupCode = true;
                t.setupCodeExpiresAt = data.expiresAt;
                t._setupCode = data.code;
                Alpine.store("toast").success("Setup code generated: " + data.code);
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },

        async unlinkDiscord(t) {
            try {
                await api.del(`/api/tenants/${encodeURIComponent(t.tenantId)}/discord`);
                t.discordUserId = null;
                t.discordUsername = null;
                Alpine.store("toast").success("Discord user unlinked");
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },
    };
}
