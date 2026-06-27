// Cortex Web Chat -- Client-side application
// Connects to WebChatHub via SignalR at /hub/webchat
// Single conversation: webchat-default (no sidebar, no conversation CRUD)
//
// Initialization is deferred: the SPA shell calls window.initChat() once the
// chat template is rendered and its DOM elements exist.

"use strict";

// -- Constants --------------------------------------------------------
const CHANNEL_ID = "webchat-default";

// -- State ------------------------------------------------------------
const state = {
    connection: null,
    isStreaming: false,
    streamingText: "",
};

// -- DOM Elements (resolved lazily during initChat) -------------------
const $ = (sel) => document.querySelector(sel);
let messagesContainer;
let streamingIndicator;
let chatForm;
let messageInput;
let sendBtn;
let abortBtn;
let connectionStatus;
let statusText;

function resolveDomElements() {
    messagesContainer  = $("#messages");
    streamingIndicator = $("#streaming-indicator");
    chatForm           = $("#chat-form");
    messageInput       = $("#message-input");
    sendBtn            = $("#send-btn");
    abortBtn           = $("#abort-btn");
    connectionStatus   = $("#connection-status");
    statusText         = $(".status-text");
}

// -- Markdown Rendering -----------------------------------------------
function initMarked() {
    if (typeof marked === "undefined") return;

    marked.setOptions({
        breaks: true,
        gfm: true,
        highlight: function (code, lang) {
            if (typeof hljs !== "undefined" && lang && hljs.getLanguage(lang)) {
                try {
                    return hljs.highlight(code, { language: lang }).value;
                } catch (_) { /* fall through */ }
            }
            if (typeof hljs !== "undefined") {
                try {
                    return hljs.highlightAuto(code).value;
                } catch (_) { /* fall through */ }
            }
            return code;
        },
    });
}

function renderMarkdown(text) {
    if (typeof marked === "undefined") {
        return escapeHtml(text);
    }
    try {
        return marked.parse(text);
    } catch (_) {
        return escapeHtml(text);
    }
}

function escapeHtml(str) {
    const div = document.createElement("div");
    div.textContent = str;
    return div.innerHTML;
}

// Post-process rendered markdown to add code block headers with copy buttons
function enhanceCodeBlocks(container) {
    container.querySelectorAll("pre > code").forEach((codeEl) => {
        const pre = codeEl.parentElement;
        if (pre.querySelector(".code-header")) return; // already enhanced

        // Detect language from class
        const langClass = Array.from(codeEl.classList).find((c) => c.startsWith("language-"));
        const lang = langClass ? langClass.replace("language-", "") : "";

        const header = document.createElement("div");
        header.className = "code-header";
        header.innerHTML = `<span>${escapeHtml(lang)}</span><button class="copy-btn" type="button">Copy</button>`;

        header.querySelector(".copy-btn").addEventListener("click", () => {
            navigator.clipboard.writeText(codeEl.textContent).then(() => {
                const btn = header.querySelector(".copy-btn");
                btn.textContent = "Copied!";
                setTimeout(() => { btn.textContent = "Copy"; }, 2000);
            });
        });

        pre.insertBefore(header, codeEl);
    });
}

// -- Message Rendering ------------------------------------------------
function formatTimestamp(date) {
    if (!date) return "";
    const d = date instanceof Date ? date : new Date(date);
    if (isNaN(d.getTime())) return "";
    return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

function createMessageElement(role, text, messageId, timestamp) {
    // Role mapping: "assistant" -> agent, "user" -> user, anything else -> system
    const roleClass = role === "user" ? "msg-user"
                    : role === "assistant" ? "msg-agent"
                    : "msg-system";
    const bubbleClass = role === "user" ? "msg-bubble msg-bubble-user"
                      : role === "assistant" ? "msg-bubble msg-bubble-agent"
                      : "msg-bubble msg-bubble-system";
    const roleLabel = role === "user" ? "You" : "Cortex";

    const msg = document.createElement("div");
    msg.className = `msg ${roleClass}`;
    if (messageId) msg.dataset.messageId = messageId;

    const bubble = document.createElement("div");
    bubble.className = bubbleClass;

    const content = document.createElement("div");
    content.className = "message-content";

    if (role === "user") {
        content.textContent = text;
    } else {
        content.innerHTML = renderMarkdown(text);
        enhanceCodeBlocks(content);
    }

    bubble.appendChild(content);
    msg.appendChild(bubble);

    // Timestamp line (below the bubble)
    if (role !== "system") {
        const timeStr = formatTimestamp(timestamp || new Date());
        if (timeStr) {
            const timeLine = document.createElement("span");
            timeLine.className = "msg-time";
            timeLine.textContent = `${roleLabel} \u00B7 ${timeStr}`;
            msg.appendChild(timeLine);
        }
    }

    return msg;
}

function appendMessage(role, text, messageId, timestamp) {
    const el = createMessageElement(role, text, messageId, timestamp);
    messagesContainer.appendChild(el);
    scrollToBottom();
    return el;
}

function createErrorMessage(text) {
    const msg = document.createElement("div");
    msg.className = "msg msg-system";

    const bubble = document.createElement("div");
    bubble.className = "msg-bubble msg-bubble-system msg-error";

    const content = document.createElement("div");
    content.className = "message-content";
    content.innerHTML = `<i class="bi bi-exclamation-triangle-fill"></i> ${escapeHtml(text)}`;

    bubble.appendChild(content);
    msg.appendChild(bubble);

    messagesContainer.appendChild(msg);
    scrollToBottom();
}

function scrollToBottom() {
    messagesContainer.scrollTop = messagesContainer.scrollHeight;
}

// -- UI State Updates -------------------------------------------------
function setStreamingState(streaming) {
    state.isStreaming = streaming;
    if (streaming) {
        streamingIndicator.classList.remove("hidden");
        sendBtn.classList.add("hidden");
        abortBtn.classList.remove("hidden");
    } else {
        streamingIndicator.classList.add("hidden");
        sendBtn.classList.remove("hidden");
        abortBtn.classList.add("hidden");
    }
    updateSendButton();
}

function updateSendButton() {
    sendBtn.disabled = !messageInput.value.trim() || state.isStreaming;
}

function setConnectionStatus(status) {
    const isConnected = status === "connected";
    connectionStatus.className = `conn-badge ${isConnected ? "connected" : "disconnected"}`;
    // Drive the presence pulse: amber vital sign when live, grey when off.
    const dot = connectionStatus.querySelector(".presence");
    if (dot) {
        dot.className = `presence ${isConnected ? "is-live" : "is-off"}`;
    }
    statusText.textContent = isConnected ? "Connected" : "Disconnected";

    // Update appShell connection status for the sidebar indicator
    if (typeof Alpine !== "undefined" && Alpine.store) {
        try {
            const conn = Alpine.store("connection");
            if (conn) {
                conn.ok = isConnected;
            }
        } catch (_) { /* ignore */ }
    }
}

// -- Load History from REST API ---------------------------------------
async function loadHistory() {
    try {
        const res = await fetch(`/api/tenants/default/history/${encodeURIComponent(CHANNEL_ID)}?limit=100`);
        if (!res.ok) {
            console.error("Failed to load history:", res.status);
            return;
        }

        const data = await res.json();
        const messages = data?.messages || [];
        messagesContainer.innerHTML = "";

        if (messages && messages.length > 0) {
            // Show chat area
            messagesContainer.classList.add("active");

            messages.forEach((msg) => {
                // Map 'Role' from MessageRecord to display role
                const role = msg.role === "assistant" ? "assistant" : "user";
                appendMessage(role, msg.content, msg.messageId, msg.timestamp);
            });
        } else {
            // Show empty state with chat area ready
            messagesContainer.classList.add("active");
        }
    } catch (err) {
        console.error("Failed to load history:", err);
    }
}

// -- Send Message -----------------------------------------------------
async function sendMessage() {
    const text = messageInput.value.trim();
    if (!text || state.isStreaming) return;

    // Clear input
    messageInput.value = "";
    autoResizeInput();
    updateSendButton();

    // Show user message immediately
    appendMessage("user", text);

    // Ensure chat is visible
    messagesContainer.classList.add("active");

    // Start streaming state
    setStreamingState(true);
    state.streamingText = "";

    try {
        await state.connection.invoke("SendMessage", CHANNEL_ID, text);
    } catch (err) {
        console.error("Failed to send message:", err);
        setStreamingState(false);
        createErrorMessage("Failed to send message. Please try again.");
    }
}

// -- Abort Generation -------------------------------------------------
async function abortGeneration() {
    try {
        await state.connection.invoke("AbortGeneration", CHANNEL_ID);
    } catch (err) {
        console.error("Failed to abort:", err);
    }
    setStreamingState(false);
}

// -- Auto-resize Textarea --------------------------------------------
function autoResizeInput() {
    messageInput.style.height = "auto";
    messageInput.style.height = Math.min(messageInput.scrollHeight, parseInt(getComputedStyle(document.documentElement).getPropertyValue("--input-max-height"))) + "px";
}

// -- SignalR Connection -----------------------------------------------
function buildConnection() {
    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hub/webchat")
        .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 30000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();

    // -- Server -> Client event handlers --

    // Final message from the agent
    connection.on("OnMessage", (conversationId, messageId, text) => {
        if (conversationId !== CHANNEL_ID) return;

        // If we were streaming, this is a non-streaming response
        if (!state.streamingText) {
            appendMessage("assistant", text, messageId);
        }
        setStreamingState(false);
    });

    // Typing indicator
    connection.on("OnTyping", (conversationId) => {
        if (conversationId !== CHANNEL_ID) return;
        setStreamingState(true);
    });

    // Streaming text update (partial)
    connection.on("OnStreamingUpdate", (conversationId, partialText) => {
        if (conversationId !== CHANNEL_ID) return;

        state.streamingText = partialText;

        // Find or create the streaming message element
        let streamingMsg = messagesContainer.querySelector(".msg.msg-agent.streaming");
        if (!streamingMsg) {
            streamingMsg = createMessageElement("assistant", partialText);
            streamingMsg.classList.add("streaming");
            const contentEl = streamingMsg.querySelector(".message-content");
            contentEl.classList.add("streaming-cursor");
            messagesContainer.appendChild(streamingMsg);
        } else {
            const contentEl = streamingMsg.querySelector(".message-content");
            contentEl.innerHTML = renderMarkdown(partialText);
            enhanceCodeBlocks(contentEl);
        }

        scrollToBottom();
    });

    // Streaming finalization
    connection.on("OnStreamingFinalize", (conversationId, messageId, finalText) => {
        if (conversationId !== CHANNEL_ID) return;

        // Replace streaming message with final version
        const streamingMsg = messagesContainer.querySelector(".msg.msg-agent.streaming");
        if (streamingMsg) {
            streamingMsg.classList.remove("streaming");
            streamingMsg.dataset.messageId = messageId;
            const contentEl = streamingMsg.querySelector(".message-content");
            contentEl.classList.remove("streaming-cursor");
            contentEl.innerHTML = renderMarkdown(finalText);
            enhanceCodeBlocks(contentEl);
        } else {
            appendMessage("assistant", finalText, messageId);
        }

        state.streamingText = "";
        setStreamingState(false);
    });

    // Error from the hub
    connection.on("OnError", (conversationId, errorMessage) => {
        if (conversationId !== CHANNEL_ID) return;
        setStreamingState(false);
        createErrorMessage(errorMessage);
    });

    // -- Connection lifecycle --

    connection.onreconnecting(() => {
        setConnectionStatus("connecting");
    });

    connection.onreconnected(async () => {
        setConnectionStatus("connected");
        await loadHistory();
    });

    connection.onclose(() => {
        setConnectionStatus("disconnected");
    });

    return connection;
}

async function startConnection() {
    setConnectionStatus("connecting");

    try {
        await state.connection.start();
        setConnectionStatus("connected");
        await loadHistory();
    } catch (err) {
        console.error("SignalR connection failed:", err);
        setConnectionStatus("disconnected");

        // If auth failure, redirect to login (SignalR doesn't use fetch interceptor)
        if (err?.statusCode === 401 || err?.message?.includes("401")) {
            window.location.href = "/login.html?returnTo=" + encodeURIComponent(window.location.pathname);
            return;
        }

        // Retry after delay
        setTimeout(startConnection, 5000);
    }
}

// -- Event Listeners --------------------------------------------------
let listenerAbortController = null;

function initEventListeners() {
    if (listenerAbortController) {
        listenerAbortController.abort();
    }
    listenerAbortController = new AbortController();
    const signal = listenerAbortController.signal;

    // Send message
    chatForm.addEventListener("submit", (e) => {
        e.preventDefault();
        sendMessage();
    }, { signal });

    // Abort
    abortBtn.addEventListener("click", abortGeneration, { signal });

    // Input handling
    messageInput.addEventListener("input", () => {
        updateSendButton();
        autoResizeInput();
    }, { signal });

    // Ctrl+Enter or Enter to send (Shift+Enter for newline)
    messageInput.addEventListener("keydown", (e) => {
        if (e.key === "Enter" && !e.shiftKey) {
            e.preventDefault();
            sendMessage();
        }
    }, { signal });
}

// -- Init (called by SPA shell when chat template is rendered) --------
// Called each time the chat page becomes active. The SignalR connection
// is created once and reused, but DOM refs and event listeners are
// re-bound every time because x-if destroys/recreates the template.
async function initChat() {
    if (window._chatInitialized) return;

    resolveDomElements();

    // Verify DOM elements exist (template may not have rendered yet)
    if (!messagesContainer || !chatForm || !messageInput) {
        console.warn("Chat DOM elements not found — deferring init");
        return;
    }

    window._chatInitialized = true;

    initMarked();
    initEventListeners();

    // Show chat area immediately (single conversation, always active)
    messagesContainer.classList.add("active");

    // Build connection only once; reuse on subsequent navigations
    if (!state.connection) {
        state.connection = buildConnection();
        await startConnection();
    } else if (state.connection.state === "Disconnected") {
        await startConnection();
    } else {
        // Connection is still alive — just reload history into the fresh DOM
        await loadHistory();
        setConnectionStatus("connected");
    }
}

// Expose for the SPA shell (layout.js calls window.initChat())
window.initChat = initChat;
window._chatInitialized = false;
