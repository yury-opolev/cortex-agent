"use strict";

/**
 * Per-tenant Recall page: history search / embeddings / reranker / memory-search settings, live status
 * of the recall stack, history deletion with the dependent-memory choice, and the review of quarantined
 * memories (bulk keep / delete).
 */
function tenantRecallPage() {
    return {
        get tenantId() { return Alpine.store("router").tenantId; },
        get base() { return `/api/tenants/${encodeURIComponent(this.tenantId)}/recall`; },

        // ── Settings ─────────────────────────────────────────────
        settings: null,
        form: null,
        saving: false,
        notices: [],
        settingsError: "",

        // ── Reranker (Bridge-wide) ───────────────────────────────
        reranker: null,
        rerankerBusy: false,

        // ── Status ───────────────────────────────────────────────
        status: null,
        connected: true,
        statusTimer: null,
        backfillRunning: false,

        // ── Delete history ───────────────────────────────────────
        channels: [],
        purge: { channelId: "", time: "all", from: "", olderThan: "", dependentMemories: "review" },
        preview: null,
        previewError: "",
        previewing: false,
        showPurgeModal: false,
        purging: false,

        // ── Needs review ─────────────────────────────────────────
        quarantine: [],
        selected: {},
        expanded: {},
        reviewBusy: false,
        showBulkDeleteModal: false,

        async init() {
            await Promise.all([this.loadSettings(), this.loadStatus(), this.loadChannels(), this.loadQuarantine()]);
            this.statusTimer = setInterval(() => this.loadStatus(), 5000);
            await this.refreshPreview();
        },

        destroy() {
            if (this.statusTimer) clearInterval(this.statusTimer);
        },

        // ── Settings ─────────────────────────────────────────────
        async loadSettings() {
            try {
                this.settings = await api.get(`${this.base}/settings`);
                this.form = { ...this.settings };
                this.settingsError = "";
            } catch (e) {
                this.settingsError = e.message;
            }
        },

        isOverridden(name) {
            return (this.settings?.overridden || []).includes(name);
        },

        changedPatch() {
            const patch = {};
            for (const key of [
                "historyEnabled", "historySearchMode", "historyDenseEnabled", "memorySearchMode",
                "rerankHistoryTopN", "rerankHistoryMaxDocChars", "rerankMemoryTopN", "rerankMemoryMaxDocChars", "rerankTimeoutMs",
            ]) {
                let value = this.form[key];
                if (typeof this.settings[key] === "number") value = Number(value);
                if (value !== this.settings[key]) patch[key] = value;
            }
            return patch;
        },

        get dirty() {
            return this.form && this.settings && Object.keys(this.changedPatch()).length > 0;
        },

        async saveSettings(reset = false) {
            this.saving = true;
            this.notices = [];
            try {
                const body = reset ? { resetToDefaults: true } : this.changedPatch();
                const result = await api.put(`${this.base}/settings`, body);
                this.settings = result.settings;
                this.form = { ...result.settings };
                this.notices = result.notices || [];
                this.settingsError = "";
                Alpine.store("toast").success(reset ? "Recall settings reset to defaults" : "Recall settings saved");
                await this.loadStatus();
            } catch (e) {
                this.settingsError = e.body?.errors?.join(" ") || e.message;
                Alpine.store("toast").error("Not saved: " + this.settingsError);
            }
            this.saving = false;
        },

        async setReranker(enabled) {
            this.rerankerBusy = true;
            try {
                this.reranker = await api.put("/api/recall/reranker", { enabled });
                Alpine.store("toast").success(enabled
                    ? "Reranker switched on; the sidecar is starting (see Status)"
                    : "Reranker switched off");
            } catch (e) {
                Alpine.store("toast").error("Reranker: " + e.message);
            }
            this.rerankerBusy = false;
            setTimeout(() => this.loadStatus(), 1500);
        },

        // ── Status ───────────────────────────────────────────────
        async loadStatus() {
            try {
                const data = await api.get(`${this.base}/status`);
                this.status = data?.agent || null;
                this.reranker = data?.reranker || this.reranker;
                this.connected = true;
                this.backfillRunning = this.status?.eventBackfill?.running || false;
            } catch (e) {
                this.connected = false;
                this.reranker = e.body?.reranker || this.reranker;
            }
        },

        percent(part, total) {
            return total > 0 ? Math.round((100 * part) / total) : 0;
        },

        formatDateTime(ts) {
            return ts ? new Date(ts).toLocaleString() : "—";
        },

        sidecarBadge() {
            const state = this.reranker?.sidecar?.state;
            return state === "running" ? "bg-success"
                : state === "failed" ? "bg-danger"
                : state === "starting" ? "bg-warning text-dark"
                : "bg-secondary";
        },

        async runBackfill() {
            if (!confirm("Run the event backfill now? Recall and history deletions pause while it runs (usually seconds).")) return;
            this.backfillRunning = true;
            try {
                const r = await api.post(`/api/tenants/${encodeURIComponent(this.tenantId)}/history/event-backfill`);
                Alpine.store("toast").success(`Event backfill: ${r.inserted} of ${r.scanned} messages received an event (${r.elapsedMs} ms)`);
            } catch (e) {
                Alpine.store("toast").error("Event backfill failed: " + e.message);
            }
            await this.loadStatus();
        },

        // ── Delete history ───────────────────────────────────────
        async loadChannels() {
            try {
                const data = await api.get(`/api/tenants/${encodeURIComponent(this.tenantId)}/channels`);
                this.channels = Array.isArray(data) ? data : [];
            } catch {
                this.channels = [];
            }
        },

        purgeScope() {
            return {
                channelId: this.purge.channelId || null,
                from: this.purge.time === "between" ? this.purge.from : null,
                olderThan: this.purge.time === "all" ? null : this.purge.olderThan,
            };
        },

        purgeScopeError() {
            if (this.purge.time !== "all" && !this.purge.olderThan) return "Choose the end date.";
            if (this.purge.time === "between" && !this.purge.from) return "Choose the start date.";
            if (this.purge.time === "between" && new Date(this.purge.from) >= new Date(this.purge.olderThan)) {
                return "The start date must be before the end date.";
            }
            return "";
        },

        async refreshPreview() {
            this.preview = null;
            this.previewError = this.purgeScopeError();
            if (this.previewError) return;
            this.previewing = true;
            try {
                this.preview = await recallPurge.preview(this.tenantId, this.purgeScope(), this.purge.dependentMemories);
            } catch (e) {
                this.previewError = e.message;
            }
            this.previewing = false;
        },

        get confirmation() {
            return recallPurge.confirmation(this.preview, this.purge.dependentMemories);
        },

        get previewRange() {
            return recallPurge.range(this.preview);
        },

        async confirmPurge() {
            this.purging = true;
            try {
                const resp = await recallPurge.run(this.tenantId, this.purgeScope(), this.purge.dependentMemories);
                Alpine.store("toast").success(resp?.message ?? "History deleted");
                this.showPurgeModal = false;
                await Promise.all([this.loadChannels(), this.loadQuarantine(), this.loadStatus()]);
                await this.refreshPreview();
            } catch (e) {
                Alpine.store("toast").error("Delete failed: " + e.message);
            }
            this.purging = false;
        },

        prettyName(id) { return prettifyChannelId(id); },

        // ── Needs review ─────────────────────────────────────────
        async loadQuarantine() {
            try {
                const data = await api.get(`${this.base}/quarantine`);
                this.quarantine = data?.items || [];
            } catch {
                this.quarantine = [];
            }
            const ids = new Set(this.quarantine.map((q) => q.memory.memoryId));
            for (const id of Object.keys(this.selected)) {
                if (!ids.has(id)) delete this.selected[id];
            }
        },

        get selectedIds() {
            return Object.keys(this.selected).filter((id) => this.selected[id]);
        },

        get allSelected() {
            return this.quarantine.length > 0 && this.selectedIds.length === this.quarantine.length;
        },

        toggleAll(checked) {
            this.selected = {};
            if (checked) {
                for (const q of this.quarantine) this.selected[q.memory.memoryId] = true;
            }
        },

        preview300(text) {
            return text && text.length > 300 ? text.slice(0, 300) + "…" : text;
        },

        purgeSource(q) {
            const where = q.purgeChannelId ? this.prettyName(q.purgeChannelId) : "all channels";
            return `From a deletion of ${where} on ${this.formatDateTime(q.purgedAt)}`;
        },

        askDelete(memoryId) {
            this.selected = { [memoryId]: true };
            this.showBulkDeleteModal = true;
        },

        async decide(ids, keep) {
            if (ids.length === 0) return;
            this.reviewBusy = true;
            try {
                const r = await api.post(`${this.base}/quarantine/${keep ? "keep" : "delete"}`, { memoryIds: ids });
                const skipped = r.skipped?.length ? ` (${r.skipped.length} no longer in review)` : "";
                Alpine.store("toast").success(`${keep ? "Kept" : "Deleted"} ${r.affected} memories${skipped}`);
                this.showBulkDeleteModal = false;
                await Promise.all([this.loadQuarantine(), this.loadStatus()]);
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
            this.reviewBusy = false;
        },
    };
}
