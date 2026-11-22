"use strict";

/**
 * Per-tenant history overview page.
 * Shows one card per channel with a message count + last-activity timestamp.
 * Clicking a card drills into the channel-scoped history page; an overview-level
 * "Clear all history" control is retained for clearing everything at once.
 */
function tenantHistoryPage() {
    return {
        channels: [],
        loading: true,

        get tenantId() { return Alpine.store("router").tenantId; },

        async init() {
            await this.loadChannels();
        },

        async loadChannels() {
            this.loading = true;
            try {
                const data = await api.get(
                    `/api/tenants/${encodeURIComponent(this.tenantId)}/channels`
                );
                // Endpoint returns an array of { id, messageCount, lastActivity }.
                this.channels = Array.isArray(data) ? data : [];
            } catch (e) {
                Alpine.store("toast").error("Failed to load channels: " + e.message);
                this.channels = [];
            }
            this.loading = false;
        },

        openChannel(channel) {
            Alpine.store("router").navigate(
                `/tenants/${encodeURIComponent(this.tenantId)}/history/${encodeURIComponent(channel.id)}`
            );
        },

        prettyName(id) { return prettifyChannelId(id); },
        relativeTime(ts) { return formatRelativeTime(ts); },

        // ── Clear all history modal ──────────────────────────────────
        showClearModal: false,
        clearOlderThan: "",
        clearDependentMemories: "review", // "review" (quarantine) or "delete" — the owner's choice per purge
        clearPreview: null,
        clearPreviewError: "",
        clearing: false,

        async openClearModal() {
            const d = new Date();
            d.setDate(d.getDate() - 7);
            this.clearOlderThan = d.toISOString().slice(0, 16);
            this.clearDependentMemories = "review";
            this.showClearModal = true;
            await this.refreshClearPreview();
        },

        clearScope() {
            return { olderThan: this.clearOlderThan || null };
        },

        async refreshClearPreview() {
            this.clearPreview = null;
            this.clearPreviewError = "";
            // An empty date would mean "everything"; this dialog only deletes older-than.
            if (!this.clearOlderThan) {
                this.clearPreviewError = "Choose a date.";
                return;
            }
            try {
                this.clearPreview = await recallPurge.preview(this.tenantId, this.clearScope(), this.clearDependentMemories);
            } catch (e) {
                this.clearPreviewError = e.message;
            }
        },

        get clearConfirmation() {
            return recallPurge.confirmation(this.clearPreview, this.clearDependentMemories);
        },

        async confirmClear() {
            if (!this.clearOlderThan) {
                this.clearPreviewError = "Choose a date.";
                return;
            }
            this.clearing = true;
            try {
                const resp = await recallPurge.run(this.tenantId, this.clearScope(), this.clearDependentMemories);
                Alpine.store("toast").success(resp?.message ?? `Deleted ${resp?.deletedCount ?? 0} messages`);
                this.showClearModal = false;
                await this.loadChannels();
            } catch (e) {
                Alpine.store("toast").error("Failed to clear history: " + e.message);
            }
            this.clearing = false;
        },
    };
}
