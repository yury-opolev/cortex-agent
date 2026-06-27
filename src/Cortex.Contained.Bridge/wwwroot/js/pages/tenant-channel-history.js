"use strict";

/**
 * Per-channel history page.
 *
 * The storage model treats each channel as exactly one conversation
 * (conversationId == channelId), so this page renders the messages directly —
 * no conversation-list/select step. Includes a scoped "clear this channel's
 * history" control.
 */
function tenantChannelHistoryPage() {
    return {
        messages: [],
        totalMessages: 0,
        loading: true,

        // Message pagination
        msgLimit: 50,
        msgOffset: 0,

        get tenantId() { return Alpine.store("router").tenantId; },
        get channelId() { return Alpine.store("router").channelId; },
        get prettyChannel() { return prettifyChannelId(this.channelId); },

        async init() {
            await this.loadMessages();
        },

        async loadMessages() {
            this.loading = true;
            try {
                const data = await api.get(
                    `/api/tenants/${encodeURIComponent(this.tenantId)}/history/${encodeURIComponent(this.channelId)}` +
                    `?limit=${this.msgLimit}&offset=${this.msgOffset}`
                );
                this.messages = data?.messages || [];
                this.totalMessages = data?.totalCount || 0;
            } catch (e) {
                Alpine.store("toast").error("Failed to load messages: " + e.message);
            }
            this.loading = false;
        },

        backToOverview() {
            Alpine.store("router").navigate(
                `/tenants/${encodeURIComponent(this.tenantId)}/history`
            );
        },

        // ── Clear-this-channel modal ──────────────────────────────────
        showClearModal: false,
        clearScope: "older", // "older" or "all"
        clearOlderThan: "",
        clearing: false,

        openClearModal() {
            // Default to 7 days ago — matches the "Clear History" modal on the overview.
            const d = new Date();
            d.setDate(d.getDate() - 7);
            this.clearOlderThan = d.toISOString().slice(0, 16);
            this.clearScope = "older";
            this.showClearModal = true;
        },

        async confirmClear() {
            this.clearing = true;
            try {
                let url = `/api/tenants/${encodeURIComponent(this.tenantId)}/channels/${encodeURIComponent(this.channelId)}/history`;
                if (this.clearScope === "older") {
                    const olderThan = new Date(this.clearOlderThan).toISOString();
                    url += `?olderThan=${encodeURIComponent(olderThan)}`;
                }
                await api.del(url);
                Alpine.store("toast").success("Channel history cleared");
                this.showClearModal = false;
                this.msgOffset = 0;
                await this.loadMessages();
            } catch (e) {
                Alpine.store("toast").error("Failed to clear channel history: " + e.message);
            }
            this.clearing = false;
        },

        // ── Pagination ───────────────────────────────────────────────
        get hasMoreMessages() { return this.msgOffset + this.msgLimit < this.totalMessages; },
        get hasPrevMessages() { return this.msgOffset > 0; },
        async nextMessages() { this.msgOffset += this.msgLimit; await this.loadMessages(); },
        async prevMessages() { this.msgOffset = Math.max(0, this.msgOffset - this.msgLimit); await this.loadMessages(); },

        // ── Formatting ───────────────────────────────────────────────
        roleIcon(role) { return role === "user" ? "bi-person-fill" : "bi-robot"; },
        roleBadge(role) { return role === "user" ? "bg-primary" : "bg-success"; },
        // Categories: 0 Normal, 1 Internal (filtered server-side), 2 System, 3 Proactive, 4 Transfer.
        // System and Transfer both render dimmed/italic; Transfer gets its own badge so users can
        // distinguish "the agent moved this conversation here" from a regular system event.
        isSystemMessage(msg) { return msg.category === 2 || msg.category === 4; },
        isProactiveMessage(msg) { return msg.category === 3; },
        messageBadge(msg) {
            if (msg.category === 2) return { text: "System", cls: "bg-warning text-dark" };
            if (msg.category === 3) return { text: "Proactive", cls: "bg-info text-dark" };
            if (msg.category === 4) return { text: "Transfer", cls: "bg-secondary text-light" };
            return null;
        },
        formatTime(ts) {
            if (!ts) return "";
            const d = new Date(ts);
            return d.toLocaleString();
        },
    };
}
