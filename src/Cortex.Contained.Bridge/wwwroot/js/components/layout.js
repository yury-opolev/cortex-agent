"use strict";

/**
 * Shared layout and routing for the unified SPA shell.
 * Provides router store, theme store, and the appShell() component
 * that drives sidebar state and page rendering.
 */
document.addEventListener("alpine:init", () => {

    // -- Router --------------------------------------------------------
    Alpine.store("router", {
        page: "",
        tenantId: null,

        channelId: null,

        // Selected help article id (null = default to first article in the manifest).
        helpArticleId: null,

        /** Parse the current URL and set page + tenantId. */
        resolve() {
            const path = window.location.pathname;

            // / or /chat -> chat page
            if (path === "/" || path === "/chat" || path === "/chat/") {
                this.page = "chat";
                this.tenantId = null;
                this.channelId = null;
                this.helpArticleId = null;
                return;
            }

            // /help or /help/{articleId} -> embedded help section
            const helpMatch = path.match(/^\/help(?:\/([^/]+))?\/?$/);
            if (helpMatch) {
                this.page = "help";
                this.tenantId = null;
                this.channelId = null;
                this.helpArticleId = helpMatch[1] ? decodeURIComponent(helpMatch[1]) : null;
                return;
            }

            // /tenants/{id}/history/{channelId} -> per-channel history page
            // Must come before the more generic sub-page match below.
            const channelMatch = path.match(/^\/tenants\/([^/]+)\/history\/([^/]+)\/?$/);
            if (channelMatch) {
                this.tenantId = decodeURIComponent(channelMatch[1]);
                this.channelId = decodeURIComponent(channelMatch[2]);
                this.page = "tenant-channel-history";
                return;
            }

            // /tenants/{id}/settings|history|memory
            const match = path.match(/^\/tenants\/([^/]+)\/(settings|history|memory)$/);
            if (match) {
                this.tenantId = decodeURIComponent(match[1]);
                this.channelId = null;
                this.page = "tenant-" + match[2];
                return;
            }

            // /tenants/{id} -> tenant settings (default sub-page)
            const tenantMatch = path.match(/^\/tenants\/([^/]+)\/?$/);
            if (tenantMatch) {
                this.tenantId = decodeURIComponent(tenantMatch[1]);
                this.channelId = null;
                this.page = "tenant-settings";
                return;
            }

            // /tenants -> tenant list
            if (path === "/tenants" || path === "/tenants/") {
                this.tenantId = null;
                this.channelId = null;
                this.page = "tenants";
                return;
            }

            // /settings -> global settings
            if (path === "/settings" || path === "/settings/") {
                this.tenantId = null;
                this.channelId = null;
                this.page = "settings";
                return;
            }

            // Default: chat
            this.page = "chat";
            this.tenantId = null;
            this.channelId = null;
        },

        /** Navigate without full page reload. */
        navigate(url) {
            window.history.pushState({}, "", url);
            this.resolve();
        },
    });

    // -- Connection ----------------------------------------------------
    Alpine.store("connection", { ok: false });

    // -- Theme (tri-state: light / dark / auto) ------------------------
    // "auto" follows the OS and reacts live to OS changes. The user cycles
    // light → dark → auto. `dark` is the resolved boolean the rest of the
    // app reads; `mode` is the user's chosen preference.
    const _media = window.matchMedia("(prefers-color-scheme: dark)");
    Alpine.store("theme", {
        mode: localStorage.getItem("theme-mode") || "auto", // "light" | "dark" | "auto"

        get dark() {
            return this.mode === "dark" || (this.mode === "auto" && _media.matches);
        },

        get icon() {
            return this.mode === "auto" ? "bi-circle-half"
                 : this.mode === "dark" ? "bi-moon-stars"
                 : "bi-sun";
        },

        get label() {
            return this.mode.charAt(0).toUpperCase() + this.mode.slice(1);
        },

        _apply() {
            document.documentElement.setAttribute("data-bs-theme", this.dark ? "dark" : "light");
            const hljsLink = document.getElementById("hljs-theme");
            if (hljsLink) {
                hljsLink.href = this.dark
                    ? "/lib/highlightjs/styles/github-dark.min.css"
                    : "/lib/highlightjs/styles/github.min.css";
            }
        },

        // Cycle light → dark → auto. Kept as toggle() so existing callers work.
        toggle() {
            this.mode = this.mode === "light" ? "dark"
                      : this.mode === "dark" ? "auto"
                      : "light";
            localStorage.setItem("theme-mode", this.mode);
            this._apply();
        },

        init() {
            this._apply();
            // Re-resolve when the OS theme changes while in auto mode.
            _media.addEventListener("change", () => {
                if (this.mode === "auto") {
                    this._apply();
                }
            });
        },
    });
});

/**
 * Alpine data component for the main app shell.
 * Drives sidebar state, page routing, and tenant list in sidebar.
 * Usage: <div x-data="appShell()">
 */
function appShell() {
    return {
        get page() { return Alpine.store("router").page; },
        get sidebarCollapsed() {
            return Alpine.store("router").page === "chat";
        },

        // Connection status for the chat SignalR connection
        get connectionOk() { return Alpine.store("connection").ok; },

        // Bridge version from /health endpoint
        bridgeVersion: "",

        // Tenant list for sidebar navigation
        tenants: [],

        // Track which tenants have their sub-nav expanded
        expandedTenants: {},

        async init() {
            Alpine.store("router").resolve();
            window.addEventListener("popstate", () => Alpine.store("router").resolve());
            await this.loadTenants();
            this.loadVersion();

            // Auto-expand the currently active tenant on initial load
            const activeTenant = Alpine.store("router").tenantId;
            if (activeTenant) {
                this.expandedTenants[activeTenant] = true;
            }

            // Watch for page changes to initialize chat when needed.
            // initChat() is safe to call repeatedly — it re-binds DOM refs
            // each time because x-if destroys/recreates the template.
            this.$watch("page", (value) => {
                if (value === "chat") {
                    // Defer to next tick so the template has rendered the DOM
                    this.$nextTick(() => {
                        if (typeof window.initChat === "function") {
                            window.initChat();
                        }
                    });
                } else {
                    window._chatInitialized = false;
                }
            });

            // Auto-expand tenant when navigating to a tenant sub-page
            this.$watch("$store.router.tenantId", (tenantId) => {
                if (tenantId) {
                    this.expandedTenants[tenantId] = true;
                }
            });

            // Handle initial page load ($watch only fires on changes)
            if (this.page === "chat") {
                this.$nextTick(() => {
                    if (typeof window.initChat === "function") {
                        window.initChat();
                    }
                });
            }
        },

        async loadTenants() {
            try {
                this.tenants = await api.get("/api/tenants") || [];
            } catch {
                // ignore — sidebar will just show nav without tenant sub-items
            }
        },

        async loadVersion() {
            try {
                const health = await api.get("/health");
                if (health?.version) {
                    this.bridgeVersion = `v${health.version}`;
                }
            } catch {
                // ignore
            }
        },

        toggleTenant(tenantId) {
            this.expandedTenants[tenantId] = !this.expandedTenants[tenantId];
        },

        nav(url) {
            Alpine.store("router").navigate(url);
        },
    };
}

/**
 * Alpine data component for the tenant sub-page tabs
 * (breadcrumb back-link + Settings/History/Memory).
 * Rendered once at the top of the content area and shared across all
 * /tenants/{id}/* sub-pages so the tab row stays DRY.
 * Usage: <div x-data="tenantTabs()">...</div>
 */
function tenantTabs() {
    return {
        get tenantId() {
            return Alpine.store("router").tenantId || "";
        },
        get page() {
            return Alpine.store("router").page;
        },
        isActive(sub) {
            // Treat the per-channel drill-in page as belonging to the History tab
            // so the tab stays highlighted while drilling into a single channel.
            if (sub === "history" && this.page === "tenant-channel-history") {
                return true;
            }
            return this.page === "tenant-" + sub;
        },
        navSub(sub) {
            if (this.isActive(sub)) {
                return;
            }
            Alpine.store("router").navigate(
                `/tenants/${encodeURIComponent(this.tenantId)}/${sub}`
            );
        },
        backToTenants() {
            Alpine.store("router").navigate("/tenants");
        },
    };
}

/**
 * Legacy appNav() kept for backward compatibility.
 * New code should use appShell() instead.
 */
function appNav() {
    return {
        get page()     { return Alpine.store("router").page; },
        get tenantId() { return Alpine.store("router").tenantId; },
        get dark()     { return Alpine.store("theme").dark; },

        nav(url) {
            Alpine.store("router").navigate(url);
        },

        toggleTheme() {
            Alpine.store("theme").toggle();
        },

        isActive(page) {
            return Alpine.store("router").page === page;
        },
    };
}
