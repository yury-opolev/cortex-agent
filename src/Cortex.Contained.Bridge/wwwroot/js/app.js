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
    messageQueue: [],   // Array of { id, text }
    queueCounter: 0,    // Monotonic counter for stable queue item IDs
    // The thinking block for the turn currently in flight. Pre-tool narration
    // segments ("Let me check…") finalize into this dimmed, collapsible lane
    // instead of overwriting the answer bubble. Reset when an answer finalizes
    // or a new user message is sent.
    thinkingBlock: null,
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

// -- Thinking lane ----------------------------------------------------
// Pre-tool narration ("Let me check the log first.") arrives as a thinking
// finalize. Rather than leaving it in the answer bubble (where the next
// segment overwrites it — the "appears and disappears" flicker), it is moved
// into a dimmed, collapsible "Thinking" block attached to the current turn.
// Multiple thinking segments in one turn append into the same block.

// Creates the per-turn thinking block at `anchor`'s position (or appended to
// the messages container) and stores it on state.thinkingBlock.
function createThinkingBlock(anchor) {
    const block = document.createElement("div");
    block.className = "thinking-block collapsed";

    const header = document.createElement("button");
    header.type = "button";
    header.className = "thinking-header";
    header.innerHTML = '<i class="bi bi-chevron-right thinking-chevron"></i><span class="thinking-title">Thinking</span>';
    header.addEventListener("click", () => {
        block.classList.toggle("collapsed");
    });

    const body = document.createElement("div");
    body.className = "thinking-body";

    block.appendChild(header);
    block.appendChild(body);

    if (anchor && anchor.parentNode === messagesContainer) {
        messagesContainer.insertBefore(block, anchor);
    } else {
        messagesContainer.appendChild(block);
    }

    return block;
}

// Appends one thinking segment's text into the current turn's thinking block,
// creating the block (positioned where `anchor` sits) if needed.
function appendThinkingSegment(text, anchor) {
    if (!state.thinkingBlock || !state.thinkingBlock.parentNode) {
        state.thinkingBlock = createThinkingBlock(anchor);
    }

    const body = state.thinkingBlock.querySelector(".thinking-body");
    const segment = document.createElement("div");
    segment.className = "thinking-segment";
    segment.innerHTML = renderMarkdown(text);
    enhanceCodeBlocks(segment);
    body.appendChild(segment);

    scrollToBottom();
}

// Collapses (but keeps) the current turn's thinking block and stops tracking it,
// so the next turn starts a fresh block. Called when an answer finalizes or a
// new user message is sent.
function finishThinkingBlock() {
    if (state.thinkingBlock) {
        state.thinkingBlock.classList.add("collapsed");
    }
    state.thinkingBlock = null;
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
    sendBtn.disabled = !messageInput.value.trim();
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
        // The DOM was just wiped; drop any dangling thinking-block reference.
        state.thinkingBlock = null;

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
    if (!text) return;

    // Clear input
    messageInput.value = "";
    autoResizeInput();
    updateSendButton();

    // Ensure chat is visible
    messagesContainer.classList.add("active");

    // If a turn is in flight, or the queue is non-empty (preserve order),
    // enqueue the message and show a greyed "queued" bubble.
    if (state.isStreaming || state.messageQueue.length > 0) {
        const queueId = ++state.queueCounter;
        const el = appendQueuedMessage(text, queueId);
        state.messageQueue.push({ id: queueId, text, el });
        return;
    }

    // No turn in flight — render bubble immediately and dispatch.
    appendMessage("user", text);
    await dispatchMessage(text);
}

// Renders a user-aligned bubble in the "queued" state with cancel/edit affordances.
function appendQueuedMessage(text, queueId) {
    const msg = document.createElement("div");
    msg.className = "msg msg-user msg-queued";
    msg.dataset.queueId = queueId;

    const bubble = document.createElement("div");
    bubble.className = "msg-bubble msg-bubble-user";

    const content = document.createElement("div");
    content.className = "message-content";
    content.textContent = text;

    const queueLabel = document.createElement("div");
    queueLabel.className = "msg-queued-label";
    queueLabel.textContent = "queued";

    const actions = document.createElement("div");
    actions.className = "msg-queued-actions";

    const editBtn = document.createElement("button");
    editBtn.type = "button";
    editBtn.className = "msg-queued-btn msg-queued-edit";
    editBtn.title = "Edit queued message";
    editBtn.innerHTML = '<i class="bi bi-pencil"></i>';
    editBtn.addEventListener("click", () => editQueued(queueId, text));

    const cancelBtn = document.createElement("button");
    cancelBtn.type = "button";
    cancelBtn.className = "msg-queued-btn msg-queued-cancel";
    cancelBtn.title = "Cancel queued message";
    cancelBtn.innerHTML = '<i class="bi bi-x"></i>';
    cancelBtn.addEventListener("click", () => cancelQueued(queueId));

    actions.appendChild(editBtn);
    actions.appendChild(cancelBtn);

    bubble.appendChild(content);
    bubble.appendChild(queueLabel);
    bubble.appendChild(actions);
    msg.appendChild(bubble);

    // Timestamp line
    const timeStr = formatTimestamp(new Date());
    if (timeStr) {
        const timeLine = document.createElement("span");
        timeLine.className = "msg-time";
        timeLine.textContent = `You · ${timeStr}`;
        msg.appendChild(timeLine);
    }

    messagesContainer.appendChild(msg);
    scrollToBottom();
    return msg;
}

// Promote the first queued message to a real send on turn completion.
async function dispatchNextQueued() {
    if (state.messageQueue.length === 0) return;
    if (state.isStreaming) return;

    const next = state.messageQueue.shift();

    // Convert the queued bubble to a normal sent bubble.
    if (next.el && next.el.parentNode) {
        next.el.classList.remove("msg-queued");
        next.el.removeAttribute("data-queue-id");
        // Remove the queued-specific label and action buttons from the bubble.
        const bubble = next.el.querySelector(".msg-bubble");
        if (bubble) {
            const label = bubble.querySelector(".msg-queued-label");
            const actionsEl = bubble.querySelector(".msg-queued-actions");
            if (label) {
                label.remove();
            }
            if (actionsEl) {
                actionsEl.remove();
            }
        }
    }

    await dispatchMessage(next.text);
}

// Low-level: invoke SendMessage on the hub. The caller is responsible for
// rendering the user bubble and setting up streaming state before calling.
async function dispatchMessage(text) {
    setStreamingState(true);
    state.streamingText = "";
    // A new user turn starts: release any prior turn's thinking block so the
    // next pre-tool segment opens a fresh one positioned in this turn.
    finishThinkingBlock();

    try {
        await state.connection.invoke("SendMessage", CHANNEL_ID, text);
    } catch (err) {
        console.error("Failed to send message:", err);
        setStreamingState(false);
        createErrorMessage("Failed to send message. Please try again.");
    }
}

// Cancel a queued message: remove from queue array and from DOM.
function cancelQueued(queueId) {
    const index = state.messageQueue.findIndex((item) => item.id === queueId);
    if (index === -1) return; // Already dispatched or not found
    const [removed] = state.messageQueue.splice(index, 1);
    if (removed.el && removed.el.parentNode) {
        removed.el.parentNode.removeChild(removed.el);
    }
}

// Edit a queued message: remove from queue, put text back in input.
function editQueued(queueId, text) {
    cancelQueued(queueId);
    messageInput.value = text;
    autoResizeInput();
    updateSendButton();
    messageInput.focus();
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
        dispatchNextQueued().catch((err) => console.error("Queue dispatch failed:", err));
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

    // Streaming finalization. `isThinking` distinguishes pre-tool narration
    // (which moves into the dimmed thinking lane) from the final answer.
    connection.on("OnStreamingFinalize", (conversationId, messageId, finalText, isThinking) => {
        if (conversationId !== CHANNEL_ID) return;

        const streamingMsg = messagesContainer.querySelector(".msg.msg-agent.streaming");

        if (isThinking) {
            // Pre-tool segment: move the just-streamed text into the per-turn
            // thinking block (positioned where the streaming bubble was), then
            // remove the streaming bubble so the NEXT segment's OnStreamingUpdate
            // creates a fresh one. The thinking text is never overwritten.
            appendThinkingSegment(finalText, streamingMsg);
            if (streamingMsg && streamingMsg.parentNode) {
                streamingMsg.parentNode.removeChild(streamingMsg);
            }

            // A thinking segment does not end the turn — keep streaming state on.
            state.streamingText = "";
            return;
        }

        // Final answer: finalize the streaming bubble as the normal message.
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

        // Collapse and release the turn's thinking block, if any.
        finishThinkingBlock();

        state.streamingText = "";
        setStreamingState(false);
        dispatchNextQueued().catch((err) => console.error("Queue dispatch failed:", err));
    });

    // Error from the hub
    connection.on("OnError", (conversationId, errorMessage) => {
        if (conversationId !== CHANNEL_ID) return;
        setStreamingState(false);
        createErrorMessage(errorMessage);
        // Still try to dispatch next queued message even after an error.
        dispatchNextQueued().catch((err) => console.error("Queue dispatch failed:", err));
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
        // Mark any queued-but-unsent bubbles as stale so the user can see
        // them, but clear the dispatch queue so nothing fires on reconnect.
        // The bubbles remain visible with their text; the user can re-type.
        for (const item of state.messageQueue) {
            if (item.el && item.el.parentNode) {
                item.el.classList.add("msg-queued-stale");
            }
        }
        state.messageQueue = [];
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
