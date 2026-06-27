# GPU image. Requires NVIDIA Container Toolkit on the host (Docker Desktop
# on Windows handles this automatically when WSL2 GPU passthrough is on).
#
# To revert to CPU-only:
#   - swap the FROM line for `python:3.11-slim`
#   - drop the `apt install python3 python3-venv` line
#   - change the pip --extra-index-url to .../whl/cpu
#   - remove the `deploy.resources` block in docker-compose.yml
FROM nvidia/cuda:12.4.1-cudnn-runtime-ubuntu22.04

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    HF_HOME=/root/.cache/huggingface \
    PATH=/opt/venv/bin:$PATH

RUN apt-get update && apt-get install -y --no-install-recommends \
        python3 python3-venv python3-pip \
        ffmpeg libsndfile1 espeak-ng \
        ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

# Isolated venv keeps pip out of the system Python and avoids
# --break-system-packages noise on modern Debian/Ubuntu.
RUN python3 -m venv /opt/venv \
    && pip install --upgrade pip

WORKDIR /app

COPY requirements.txt .
RUN pip install --extra-index-url https://download.pytorch.org/whl/cu124 \
        -r requirements.txt

COPY app.py .

EXPOSE 8000

# Long start-period: the first boot downloads ~1 GB of weights from
# Hugging Face before /health flips to model_loaded=true.
HEALTHCHECK --interval=30s --timeout=10s --start-period=600s --retries=3 \
    CMD python -c "import urllib.request,sys; \
        r=urllib.request.urlopen('http://localhost:8000/health'); \
        sys.exit(0 if r.status==200 else 1)" || exit 1

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8000"]
