"use strict";

/**
 * Shared history-deletion helper for the History, channel-history and Recall pages.
 * Every deletion first asks the agent for a preview (messages + dependent memories) and shows the
 * confirmation for the owner's choice of what happens to memories created in that period:
 * "review" (hidden until kept or deleted, the default) or "delete" (erased with the history).
 */
const recallPurge = {
    base(tenantId) {
        return `/api/tenants/${encodeURIComponent(tenantId)}/recall`;
    },

    /**
     * Builds the request body. scope: { channelId?, from?, olderThan? } with datetime-local strings or ISO.
     * A bound that is null/undefined means "unbounded"; an empty or unparsable date is a validation error
     * (never silently widened to unbounded).
     */
    body(scope, dependentMemories) {
        const iso = (v, label) => {
            if (v === null || v === undefined) return null;
            const d = v === "" ? new Date(NaN) : new Date(v);
            if (Number.isNaN(d.getTime())) throw new Error(`Choose a valid ${label} date.`);
            return d.toISOString();
        };
        return {
            channelId: scope.channelId || null,
            from: iso(scope.from, "start"),
            olderThan: iso(scope.olderThan, "end"),
            dependentMemories: dependentMemories === "delete" ? "delete" : "review",
        };
    },

    /** The part of a body that defines what is deleted (the dependent-memory choice is made at confirm time). */
    scopeKey(body) {
        return JSON.stringify([body.channelId, body.from, body.olderThan]);
    },

    /** Per-page state: the latest preview request number and the scope that was previewed. */
    newState() {
        return { seq: 0, previewed: null };
    },

    /** Invalidates any preview and any response still in flight. */
    reset(state) {
        state.seq++;
        state.previewed = null;
    },

    /**
     * Previews the scope. Returns { stale: true } when a newer preview started meanwhile (the result is
     * dropped), otherwise { preview }. Validation and server errors throw only for the latest request.
     */
    async startPreview(state, tenantId, scope, dependentMemories) {
        const seq = ++state.seq;
        state.previewed = null;
        const body = this.body(scope, dependentMemories);
        let preview;
        try {
            preview = await api.post(`${this.base(tenantId)}/purge/preview`, body);
        } catch (e) {
            if (seq !== state.seq) return { stale: true };
            throw e;
        }
        if (seq !== state.seq) return { stale: true };
        state.previewed = { key: this.scopeKey(body), body, messages: preview?.messages ?? 0 };
        return { preview };
    },

    /**
     * Runs the deletion for exactly the previewed scope. Throws an error with stale = true (and sends
     * nothing) when the current scope no longer matches the preview.
     */
    async run(state, tenantId, scope, dependentMemories) {
        const current = this.body(scope, dependentMemories);
        const previewed = state.previewed;
        if (!previewed || previewed.key !== this.scopeKey(current)) {
            const error = new Error("The selection changed since the preview. Check the new preview before deleting.");
            error.stale = true;
            throw error;
        }
        return api.post(this.base(tenantId), {
            ...previewed.body,
            dependentMemories: current.dependentMemories,
            expectedMessages: previewed.messages,
        });
    },

    /** The confirmation sentence for the chosen option (server-built from the preview). */
    confirmation(preview, dependentMemories) {
        if (!preview) return "";
        return dependentMemories === "delete" ? preview.confirmations?.delete : preview.confirmations?.review;
    },

    /** "from … to …" in the browser's locale, or "". */
    range(preview) {
        if (!preview?.rangeFrom) return "";
        const f = (ts) => new Date(ts).toLocaleString();
        return `${f(preview.rangeFrom)} – ${f(preview.rangeTo)}`;
    },
};
