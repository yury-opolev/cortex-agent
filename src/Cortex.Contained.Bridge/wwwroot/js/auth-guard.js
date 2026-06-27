// Cortex — Auth guard (global fetch interceptor)
// Include this script BEFORE any other JS on authenticated pages.
// Redirects to login.html on any 401 response from the API.
"use strict";

(function () {
    const originalFetch = window.fetch;

    window.fetch = async function (...args) {
        const response = await originalFetch.apply(this, args);

        if (response.status === 401) {
            const url = typeof args[0] === "string" ? args[0] : args[0]?.url || "";
            // Only redirect for API/hub calls, not for auth endpoints themselves
            if (!url.startsWith("/api/auth/")) {
                const returnTo = encodeURIComponent(window.location.pathname + window.location.search);
                window.location.href = "/login.html?returnTo=" + returnTo;
                // Return a never-resolving promise to prevent further processing
                return new Promise(() => {});
            }
        }

        return response;
    };
})();
