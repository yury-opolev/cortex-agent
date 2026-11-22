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

        // ── Embeddings GPU (Bridge-wide) ─────────────────────────
        embeddingsGpu: null,
        embeddingsGpuBusy: false,

        // ── Status ───────────────────────────────────────────────
        status: null,
        connected: true,
        statusTimer: null,
        backfillRunning: false,

        // ── Delete history ───────────────────────────────────────
        channels: [],
        purge: { channelId: "", time: "all", from: "", olderThan: "", dependentMemories: "review" },
        preview: null,
        previewState: recallPurge.newState(),
        previewError: "",
        previewing: false,
        showPurgeModal: false,
        purging: false,

        // ── Needs review ─────────────────────────────────────────
        quarantine: [],
        quarantineTotal: 0,
        quarantineNext: 0,
        reviewPageSize: 200,
        reviewProgress: "",
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

        async setEmbeddingsGpu(enabled) {
            this.embeddingsGpuBusy = true;
            try {
                this.embeddingsGpu = await api.put("/api/recall/embeddings-gpu", { enabled });
                Alpine.store("toast").success(enabled
                    ? "Embeddings will run on the GPU; the sidecar is being recreated (see below)"
                    : "Embeddings will run on the CPU; the sidecar is being recreated (see below)");
            } catch (e) {
                Alpine.store("toast").error("Embeddings GPU: " + e.message);
            }
            this.embeddingsGpuBusy = false;
            setTimeout(() => this.loadEmbeddingsGpu(), 1500);
        },

        async loadEmbeddingsGpu() {
            try {
                this.embeddingsGpu = await api.get("/api/recall/embeddings-gpu");
            } catch {
                // keep the last known view; the next refresh tries again
            }
        },

        embeddingsGpuBadge() {
            const state = this.embeddingsGpu?.sidecar?.state;
            return state === "running" ? "bg-success"
                : state === "failed" ? "bg-danger"
                : state === "starting" ? "bg-warning text-dark"
                : "bg-secondary";
        },

        embeddingsPlacement() {
            const view = this.embeddingsGpu;
            if (!view?.mode) return "not running";
            const where = view.mode === "gpu" ? "GPU" : "CPU";
            return view.processor ? `${where} container, model: ${view.processor}` : `${where} container, model not loaded right now`;
        },

        // ── Status ───────────────────────────────────────────────
        async loadStatus() {
            this.loadEmbeddingsGpu();
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
            recallPurge.reset(this.previewState);
            this.previewError = this.purgeScopeError();
            if (this.previewError) {
                this.previewing = false;
                return;
            }
            this.previewing = true;
            try {
                const result = await recallPurge.startPreview(this.previewState, this.tenantId, this.purgeScope(), this.purge.dependentMemories);
                if (result.stale) return;
                this.preview = result.preview;
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
            const invalid = this.purgeScopeError();
            if (invalid) {
                Alpine.store("toast").error(invalid);
                return;
            }
            this.purging = true;
            try {
                const resp = await recallPurge.run(this.previewState, this.tenantId, this.purgeScope(), this.purge.dependentMemories);
                Alpine.store("toast").success(resp?.message ?? "History deleted");
                this.showPurgeModal = false;
                await Promise.all([this.loadChannels(), this.loadQuarantine(), this.loadStatus()]);
                await this.refreshPreview();
            } catch (e) {
                Alpine.store("toast").error("Delete failed: " + e.message);
                if (e.stale || e.status === 409) {
                    this.showPurgeModal = false;
                    await this.refreshPreview();
                }
            }
            this.purging = false;
        },

        prettyName(id) { return prettifyChannelId(id); },

        // ── Needs review ─────────────────────────────────────────
        async loadQuarantine(more = false) {
            const offset = more ? this.quarantineNext : 0;
            try {
                const data = await api.get(`${this.base}/quarantine?offset=${offset}&limit=${this.reviewPageSize}`);
                const items = data?.items || [];
                if (more) {
                    const have = new Set(this.quarantine.map((q) => q.memory.memoryId));
                    this.quarantine = this.quarantine.concat(items.filter((q) => !have.has(q.memory.memoryId)));
                } else {
                    this.quarantine = items;
                }
                this.quarantineTotal = data?.total ?? this.quarantine.length;
                this.quarantineNext = offset + (data?.limit ?? this.reviewPageSize);
            } catch {
                if (!more) {
                    this.quarantine = [];
                    this.quarantineTotal = 0;
                    this.quarantineNext = 0;
                }
            }
            if (this.quarantineTotal === 0) this.selected = {};
        },

        get selectedIds() {
            return Object.keys(this.selected).filter((id) => this.selected[id]);
        },

        get allSelected() {
            return this.quarantine.length > 0 && this.quarantine.every((q) => this.selected[q.memory.memoryId]);
        },

        get hasMoreQuarantine() {
            return this.quarantine.length < this.quarantineTotal;
        },

        toggleAll(checked) {
            this.selected = {};
            if (checked) {
                for (const q of this.quarantine) this.selected[q.memory.memoryId] = true;
            }
        },

        /** Selects every quarantined memory, not just the loaded page. */
        async selectEverything() {
            this.reviewBusy = true;
            try {
                const data = await api.get(`${this.base}/quarantine/ids`);
                this.selected = {};
                for (const id of data?.ids || []) this.selected[id] = true;
            } catch (e) {
                Alpine.store("toast").error("Could not select all: " + e.message);
            }
            this.reviewBusy = false;
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

        /** Keeps or deletes ids in sequential batches of at most 1000 (the server limit); stops at the first error. */
        async decide(ids, keep) {
            if (ids.length === 0) return;
            const batchSize = 1000;
            this.reviewBusy = true;
            let done = 0;
            let affected = 0;
            let skipped = 0;
            try {
                for (let i = 0; i < ids.length; i += batchSize) {
                    const batch = ids.slice(i, i + batchSize);
                    this.reviewProgress = ids.length > batchSize ? `${done} of ${ids.length}…` : "";
                    const r = await api.post(`${this.base}/quarantine/${keep ? "keep" : "delete"}`, { memoryIds: batch });
                    done += batch.length;
                    affected += r.affected;
                    skipped += r.skipped?.length || 0;
                    for (const id of batch) delete this.selected[id];
                }
                const note = skipped ? ` (${skipped} no longer in review)` : "";
                Alpine.store("toast").success(`${keep ? "Kept" : "Deleted"} ${affected} memories${note}`);
            } catch (e) {
                const progress = ids.length > batchSize ? ` after ${done} of ${ids.length}` : "";
                Alpine.store("toast").error(`${keep ? "Keep" : "Delete"} stopped${progress}: ${e.message}`);
            }
            this.reviewProgress = "";
            this.showBulkDeleteModal = false;
            await Promise.all([this.loadQuarantine(), this.loadStatus()]);
            this.reviewBusy = false;
        },
    };
}
