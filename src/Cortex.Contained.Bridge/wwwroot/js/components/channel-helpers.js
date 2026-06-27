"use strict";

/**
 * Pure helpers for presenting channel metadata in the UI.
 * Kept free of Alpine/Bootstrap dependencies so the logic can be eyeballed
 * and, if needed, pulled into a standalone unit-test harness.
 */

/**
 * Turn a raw channel id into a user-friendly label.
 *
 * Examples:
 *   webchat-default         → "Web chat"
 *   discord-dm              → "Discord DMs"
 *   discord-voice-default   → "Discord voice (default)"
 *   discord-voice-foo-bar   → "Discord voice (foo-bar)"
 *   api-anyTenantId         → "API"
 *   custom-thing            → "custom-thing" (raw fallback)
 *
 * Null, undefined, empty, or whitespace → empty string.
 */
function prettifyChannelId(id) {
    if (id === null || id === undefined) {
        return "";
    }
    const s = String(id).trim();
    if (s.length === 0) {
        return "";
    }
    if (s === "webchat-default" || s === "webchat") {
        return "Web chat";
    }
    if (s === "discord-dm") {
        return "Discord DMs";
    }
    if (s.startsWith("discord-voice-")) {
        const suffix = s.substring("discord-voice-".length);
        return `Discord voice (${suffix})`;
    }
    if (s.startsWith("api-")) {
        return "API";
    }
    return s;
}

/**
 * Relative timestamp formatter built on Intl.RelativeTimeFormat.
 * Accepts a Date, an ISO string, a number (ms since epoch), or null/undefined.
 *
 * Returns the empty string for null/undefined/invalid input so callers can
 * bind directly via x-text without extra guards.
 *
 * The threshold ladder is the usual one: seconds / minutes / hours / days /
 * weeks / months / years — with "just now" for deltas under 5 seconds.
 */
function formatRelativeTime(input) {
    if (input === null || input === undefined || input === "") {
        return "";
    }
    const when = input instanceof Date ? input : new Date(input);
    const ts = when.getTime();
    if (Number.isNaN(ts)) {
        return "";
    }

    const deltaSec = Math.round((ts - Date.now()) / 1000);
    const absSec = Math.abs(deltaSec);

    if (absSec < 5) {
        return "just now";
    }

    const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
    const thresholds = [
        { unit: "year",   seconds: 60 * 60 * 24 * 365 },
        { unit: "month",  seconds: 60 * 60 * 24 * 30 },
        { unit: "week",   seconds: 60 * 60 * 24 * 7 },
        { unit: "day",    seconds: 60 * 60 * 24 },
        { unit: "hour",   seconds: 60 * 60 },
        { unit: "minute", seconds: 60 },
        { unit: "second", seconds: 1 },
    ];
    for (const { unit, seconds } of thresholds) {
        if (absSec >= seconds) {
            const value = Math.round(deltaSec / seconds);
            return rtf.format(value, unit);
        }
    }
    return rtf.format(deltaSec, "second");
}
