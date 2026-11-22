#!/bin/sh
# cortex-reranker entrypoint: refuses to serve without authentication, then runs llama-server
# with the reranking endpoint only (no web UI, no slot monitoring) and without request logging.
set -eu

if [ -z "${LLAMA_API_KEY:-}" ]; then
    echo "cortex-reranker: LLAMA_API_KEY is empty; refusing to start without authentication (the Bridge passes the key)." >&2
    exit 64
fi

# The base image sets these; the explicit flags below are the single source of truth.
unset LLAMA_ARG_HOST LLAMA_ARG_PORT

# Sizing (override through the container environment; empty means the default). Lean defaults,
# measured to fit next to the speech sidecars on an 8 GB GPU (docs/reranker.md). Per-slot context is
# RERANKER_CTX_SIZE / RERANKER_PARALLEL, and RERANKER_UBATCH_SIZE must be at least the token count of
# the longest query + document, or llama-server rejects that request.

# --log-verbosity 2 keeps warnings and errors only: request lines (info) and request bodies (debug)
# are never logged. /health stays public (llama-server exempts it from the API key).
exec /app/llama-server \
    --model /models/reranker.gguf \
    --alias "${RERANKER_MODEL_ALIAS}" \
    --reranking \
    --host 0.0.0.0 \
    --port 8080 \
    --ctx-size "${RERANKER_CTX_SIZE:-4096}" \
    --parallel "${RERANKER_PARALLEL:-2}" \
    --batch-size "${RERANKER_BATCH_SIZE:-2048}" \
    --ubatch-size "${RERANKER_UBATCH_SIZE:-2048}" \
    --flash-attn "${RERANKER_FLASH_ATTN:-on}" \
    --cache-type-k "${RERANKER_CACHE_TYPE:-q8_0}" \
    --cache-type-v "${RERANKER_CACHE_TYPE:-q8_0}" \
    --no-webui \
    --no-slots \
    --log-verbosity 2 \
    "$@"
