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
        clearing: false,

        openClearModal() {
            const d = new Date();
            d.setDate(d.getDate() - 7);
            this.clearOlderThan = d.toISOString().slice(0, 16);
            this.showClearModal = true;
        },

        async confirmClear() {
            this.clearing = true;
            try {
                const olderThan = new Date(this.clearOlderThan).toISOString();
                const resp = await api.del(
                    `/api/tenants/${encodeURIComponent(this.tenantId)}/history?olderThan=${encodeURIComponent(olderThan)}`
                );
                Alpine.store("toast").success(`Deleted ${resp?.deletedCount ?? 0} messages`);
                this.showClearModal = false;
                await this.loadChannels();
            } catch (e) {
                Alpine.store("toast").error("Failed to clear history: " + e.message);
            }
            this.clearing = false;
        },
    };
}
