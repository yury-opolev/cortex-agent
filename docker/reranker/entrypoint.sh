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

# --log-verbosity 2 keeps warnings and errors only: request lines (info) and request bodies (debug)
# are never logged. /health stays public (llama-server exempts it from the API key).
exec /app/llama-server \
    --model /models/reranker.gguf \
    --alias "${RERANKER_MODEL_ALIAS}" \
    --reranking \
    --host 0.0.0.0 \
    --port 8080 \
    --ctx-size 8192 \
    --batch-size 2048 \
    --ubatch-size 2048 \
    --no-webui \
    --no-slots \
    --log-verbosity 2 \
    "$@"
