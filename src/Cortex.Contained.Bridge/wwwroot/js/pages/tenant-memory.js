"use strict";

/**
 * Per-tenant memory management page.
 * List, search, add, edit, delete memories.
 */
function tenantMemoryPage() {
    return {
        memories: [],
        totalCount: 0,
        loading: true,

        // Pagination
        limit: 50,
        offset: 0,

        // Edit/add modal
        showModal: false,
        editing: null,  // null = adding, object = editing
        form: { content: "", tags: "", title: "" },

        // Delete confirmation
        showDeleteModal: false,
        deleteTarget: null,

        get tenantId() { return Alpine.store("router").tenantId; },

        async init() {
            await this.load();
        },

        async load() {
            this.loading = true;
            try {
                const data = await api.get(
                    `/api/tenants/${encodeURIComponent(this.tenantId)}/memories?limit=${this.limit}&offset=${this.offset}`
                );
                this.memories = data?.items || [];
                this.totalCount = data?.totalCount || 0;
            } catch (e) {
                Alpine.store("toast").error("Failed to load memories: " + e.message);
            }
            this.loading = false;
        },

        openAdd() {
            this.editing = null;
            this.form = { content: "", tags: "", title: "" };
            this.showModal = true;
        },

        openEdit(mem) {
            this.editing = mem;
            this.form = {
                content: mem.content,
                tags: (mem.tags || []).join(", "),
                title: mem.title || "",
            };
            this.showModal = true;
        },

        async saveMemory() {
            const tags = this.form.tags.split(",").map(t => t.trim()).filter(Boolean);
            const body = { content: this.form.content, tags, title: this.form.title || null };

            try {
                if (this.editing) {
                    await api.put(`/api/memory/${encodeURIComponent(this.editing.memoryId)}`, body);
                    Alpine.store("toast").success("Memory updated");
                } else {
                    await api.post("/api/memory", body);
                    Alpine.store("toast").success("Memory added");
                }
                this.showModal = false;
                await this.load();
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },

        confirmDelete(mem) {
            this.deleteTarget = mem;
            this.showDeleteModal = true;
        },

        async doDelete() {
            if (!this.deleteTarget) return;
            try {
                await api.del(`/api/memory/${encodeURIComponent(this.deleteTarget.memoryId)}`);
                Alpine.store("toast").success("Memory deleted");
                this.showDeleteModal = false;
                this.deleteTarget = null;
                await this.load();
            } catch (e) {
                Alpine.store("toast").error(e.message);
            }
        },

        // Pagination
        get hasMore() { return this.offset + this.limit < this.totalCount; },
        get hasPrev() { return this.offset > 0; },
        async next() { this.offset += this.limit; await this.load(); },
        async prev() { this.offset = Math.max(0, this.offset - this.limit); await this.load(); },

        formatDate(ts) {
            if (!ts) return "";
            return new Date(ts).toLocaleDateString();
        },

        formatDateTime(ts) {
            if (!ts) return "";
            return new Date(ts).toLocaleString();
        },

        wasUpdated(m) {
            if (!m.updatedAt || !m.createdAt) return false;
            return new Date(m.updatedAt).getTime() - new Date(m.createdAt).getTime() > 1000;
        },
    };
}
