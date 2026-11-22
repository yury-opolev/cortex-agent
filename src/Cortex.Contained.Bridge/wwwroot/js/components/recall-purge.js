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

    /** Builds the request body. scope: { channelId?, from?, olderThan? } with datetime-local strings or ISO. */
    body(scope, dependentMemories) {
        const iso = (v) => (v ? new Date(v).toISOString() : null);
        return {
            channelId: scope.channelId || null,
            from: iso(scope.from),
            olderThan: iso(scope.olderThan),
            dependentMemories: dependentMemories === "delete" ? "delete" : "review",
        };
    },

    async preview(tenantId, scope, dependentMemories) {
        return api.post(`${this.base(tenantId)}/purge/preview`, this.body(scope, dependentMemories));
    },

    async run(tenantId, scope, dependentMemories) {
        return api.post(`${this.base(tenantId)}/purge`, this.body(scope, dependentMemories));
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
