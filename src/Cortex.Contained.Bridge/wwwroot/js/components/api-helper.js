"use strict";

/**
 * Shared fetch wrapper with auth, error handling, and JSON parsing.
 * Used by all Alpine.js page components.
 */
const api = {
    /** GET request. Returns parsed JSON or null on error. */
    async get(url) {
        return this._request("GET", url);
    },

    /** POST request with optional JSON body. */
    async post(url, body) {
        return this._request("POST", url, body);
    },

    /** PUT request with optional JSON body. */
    async put(url, body) {
        return this._request("PUT", url, body);
    },

    /** DELETE request. */
    async del(url, body) {
        return this._request("DELETE", url, body);
    },

    /** Core request method. Handles auth redirect on 401. */
    async _request(method, url, body) {
        const opts = {
            method,
            headers: {},
        };
        if (body !== undefined) {
            opts.headers["Content-Type"] = "application/json";
            opts.body = JSON.stringify(body);
        }

        const resp = await fetch(url, opts);

        if (resp.status === 401) {
            window.location.href = "/login.html";
            return null;
        }

        if (!resp.ok) {
            const err = await resp.json().catch(() => ({ error: `HTTP ${resp.status}` }));
            const httpError = new Error(err.error || `HTTP ${resp.status}`);
            httpError.body = err;
            throw httpError;
        }

        const text = await resp.text();
        return text ? JSON.parse(text) : null;
    },
};
