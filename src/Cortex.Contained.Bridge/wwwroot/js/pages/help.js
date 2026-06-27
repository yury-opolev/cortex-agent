"use strict";

/**
 * Help page — Alpine.js component.
 *
 * Renders a two-pane embedded help section: a topic tree (built from
 * /help/manifest.json) on the left and the rendered markdown article on the
 * right. Articles are static files under wwwroot/help/ served as text/markdown
 * and rendered with the shared renderMarkdown() (marked + highlight.js).
 *
 * Search lazily fetches every article's text once, caches it, and filters
 * client-side. Cross-links between articles (href="help:{id}" or
 * "/help/{id}") are intercepted and routed in-app.
 */
function helpPage() {
    return {
        // ── State ──────────────────────────────────────────
        loading: true,
        error: "",

        sections: [],          // [{ id, title, articles: [{ id, title, file }] }]
        articleIndex: {},      // id -> { id, title, file, sectionTitle }

        currentId: null,
        articleTitle: "",
        articleHtml: "",
        articleLoading: false,
        articleError: "",

        searchQuery: "",
        searchResults: [],
        _searchIndex: null,    // [{ id, title, sectionTitle, text }]
        _indexBuilding: false,

        // ── Lifecycle ──────────────────────────────────────
        async init() {
            try {
                const resp = await fetch("/help-content/manifest.json", { cache: "no-store" });
                if (!resp.ok) {
                    throw new Error("HTTP " + resp.status);
                }
                const manifest = await resp.json();
                this.sections = manifest.sections || [];
                this.sections.forEach((s) => {
                    (s.articles || []).forEach((a) => {
                        this.articleIndex[a.id] = { ...a, sectionTitle: s.title };
                    });
                });
            } catch (e) {
                this.error = e.message || String(e);
                this.loading = false;
                return;
            }
            this.loading = false;

            // Resolve the initial article: URL article id if valid, else the first.
            const wanted = Alpine.store("router").helpArticleId;
            const target = (wanted && this.articleIndex[wanted]) ? wanted : this.firstArticleId();
            if (target) {
                await this.loadArticle(target);
            }

            // Sync with browser back/forward (URL -> article).
            this.$watch("$store.router.helpArticleId", (id) => {
                if (Alpine.store("router").page !== "help") {
                    return;
                }
                const t = (id && this.articleIndex[id]) ? id : this.firstArticleId();
                if (t && t !== this.currentId) {
                    this.loadArticle(t);
                }
            });
        },

        // ── Navigation ─────────────────────────────────────
        firstArticleId() {
            return this.sections[0]?.articles?.[0]?.id || null;
        },

        articleHref(id) {
            return "/help/" + encodeURIComponent(id);
        },

        isActive(id) {
            return this.currentId === id;
        },

        selectArticle(id) {
            // Update the URL (so reload/deep-link works) then render immediately.
            // The $watch guard skips the duplicate load triggered by navigate().
            Alpine.store("router").navigate(this.articleHref(id));
            this.loadArticle(id);
        },

        // ── Article rendering ──────────────────────────────
        async loadArticle(id) {
            const meta = this.articleIndex[id];
            this.currentId = id;
            this.searchQuery = "";
            this.searchResults = [];

            if (!meta) {
                this.articleError = "Article not found.";
                this.articleHtml = "";
                this.articleTitle = "";
                return;
            }

            this.articleTitle = meta.title;
            this.articleError = "";
            this.articleLoading = true;
            try {
                const resp = await fetch("/help-content/" + meta.file, { cache: "no-store" });
                if (!resp.ok) {
                    throw new Error("HTTP " + resp.status);
                }
                const md = await resp.text();
                this.articleHtml = (typeof renderMarkdown === "function") ? renderMarkdown(md) : md;
                this.$nextTick(() => this._wireContentLinks());
            } catch (e) {
                this.articleError = "Could not load this article (" + (e.message || e) + ").";
                this.articleHtml = "";
            } finally {
                this.articleLoading = false;
            }
        },

        _wireContentLinks() {
            const root = this.$refs.articleBody;
            if (!root) {
                return;
            }
            root.querySelectorAll("a[href]").forEach((a) => {
                const href = a.getAttribute("href") || "";
                let targetId = null;
                if (href.startsWith("help:")) {
                    targetId = href.slice(5);
                } else {
                    const m = href.match(/^\/help\/([^/?#]+)/);
                    if (m) {
                        targetId = decodeURIComponent(m[1]);
                    }
                }
                if (targetId) {
                    a.addEventListener("click", (ev) => {
                        ev.preventDefault();
                        this.selectArticle(targetId);
                    });
                } else if (/^https?:/i.test(href)) {
                    a.setAttribute("target", "_blank");
                    a.setAttribute("rel", "noopener");
                }
            });
        },

        // ── Search ─────────────────────────────────────────
        async onSearch() {
            const q = this.searchQuery.trim().toLowerCase();
            if (!q) {
                this.searchResults = [];
                return;
            }
            await this._buildIndex();

            const results = [];
            for (const entry of this._searchIndex) {
                const titleHit = entry.title.toLowerCase().includes(q);
                const bodyIdx = entry.text.toLowerCase().indexOf(q);
                if (!titleHit && bodyIdx < 0) {
                    continue;
                }
                let snippet = "";
                if (bodyIdx >= 0) {
                    const start = Math.max(0, bodyIdx - 40);
                    snippet = (start > 0 ? "…" : "")
                        + entry.text.slice(start, bodyIdx + q.length + 60).trim()
                        + "…";
                }
                results.push({
                    id: entry.id,
                    title: entry.title,
                    sectionTitle: entry.sectionTitle,
                    snippet,
                    rank: titleHit ? 0 : 1,
                });
            }
            results.sort((a, b) => a.rank - b.rank);
            this.searchResults = results;
        },

        async _buildIndex() {
            if (this._searchIndex || this._indexBuilding) {
                return;
            }
            this._indexBuilding = true;
            const entries = [];
            for (const s of this.sections) {
                for (const a of (s.articles || [])) {
                    try {
                        const resp = await fetch("/help-content/" + a.file, { cache: "force-cache" });
                        const md = resp.ok ? await resp.text() : "";
                        entries.push({
                            id: a.id,
                            title: a.title,
                            sectionTitle: s.title,
                            text: this._stripMarkdown(md),
                        });
                    } catch {
                        // Skip articles that fail to load — they just won't be searchable.
                    }
                }
            }
            this._searchIndex = entries;
            this._indexBuilding = false;
        },

        _stripMarkdown(md) {
            return md
                .replace(/```[\s\S]*?```/g, " ")
                .replace(/[#>*_`>\-]/g, " ")
                .replace(/\s+/g, " ")
                .trim();
        },
    };
}
