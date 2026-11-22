# syntax=docker/dockerfile:1.7
#
# cortex-reranker: the episodic-history reranker sidecar (increment 5).
#
# Stock llama.cpp `llama-server` with a cross-encoder reranker model baked in, so the
# container needs no network at runtime and no model volume. Everything is pinned:
#   - the server image by digest (multi-arch index of tag server[-cuda]-b12602),
#   - the GGUF by sha256 (BuildKit verifies the download before it enters the image).
# Bumping a pin is a reviewed change; bump version.json with it.
#
# VARIANT=cuda (default) runs the model on the GPU and falls back to the CPU when none is
# visible; VARIANT=cpu is the small CPU-only image. Measured on CPU (20 cores): ~365 prompt
# tokens/s, far too slow for the Gate R p95 at the default budget — see docs/reranker.md.
#
# Fallback model (multilingual, bidirectional encoder): replace the ADD below with
#   https://huggingface.co/gpustack/bge-reranker-v2-m3-GGUF/resolve/3093af03b1a635e67b084b1d8c03c5f5e020fd05/bge-reranker-v2-m3-Q8_0.gguf
#   sha256 a43c7c9b11a4c1517e5bf95151960e1621d1b72f7a493364b01e386cf1aaa1d3
# and set RERANKER_MODEL_ALIAS=bge-reranker-v2-m3 (and Reranker:Model in the agent).
ARG VARIANT=cuda

FROM ghcr.io/ggml-org/llama.cpp:server-cuda-b12602@sha256:26f89ebd3bf17a5ecf6caee62bdabfa55abea3058150879f6f07b1fc786baa2d AS server-cuda
FROM ghcr.io/ggml-org/llama.cpp:server-b12602@sha256:ae28f5c2046de51dfd00c9116a36fe44b5e8703ef730d0aea69082781eecb1f7 AS server-cpu

FROM server-${VARIANT}

# 0555, not 0444: ADD applies the mode to the /models directory it creates, which needs +x to be entered.
ADD --chmod=0555 --checksum=sha256:22c9979ce4fbcdc5acdc310c6641c32797eff1aa980b8f7a2db8a8ea23429a48 \
    https://huggingface.co/ggml-org/Qwen3-Reranker-0.6B-Q8_0-GGUF/resolve/a02f48bb4f057028298c21fa033da2b30d7742d5/qwen3-reranker-0.6b-q8_0.gguf \
    /models/reranker.gguf

COPY --chmod=0555 entrypoint.sh /usr/local/bin/cortex-reranker

LABEL org.opencontainers.image.title="cortex-reranker" \
      org.opencontainers.image.description="llama.cpp reranking server with a baked Qwen3-Reranker-0.6B (Q8_0)"

ENV RERANKER_MODEL_ALIAS=qwen3-reranker-0.6b

# Unprivileged: the model is world-readable and the server writes nothing.
USER 65532:65532

HEALTHCHECK --interval=10s --timeout=3s --start-period=60s --retries=3 \
    CMD ["curl", "-fsS", "http://127.0.0.1:8080/health"]

ENTRYPOINT ["/usr/local/bin/cortex-reranker"]
