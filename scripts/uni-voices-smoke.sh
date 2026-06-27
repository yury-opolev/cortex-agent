#!/usr/bin/env bash
# Rebuild the uni-voices image (now baking models at build time), recreate the
# container, wait until all three engines actually load, then synth one sentence
# per engine. Proves: image builds, no runtime internet needed, GPU used, audio
# produced. Run from the cortex repo root.
set +e
export MSYS_NO_PATHCONV=1   # stop git-bash translating /paths passed to docker exec
cd "C:/Users/yurio/Documents/github/cortex" || exit 1

echo "===== BUILD (bakes Kokoro/Røst/Silero weights) ====="
docker compose --profile tts build uni-voices 2>&1 | tail -50
echo "BUILD_EXIT=${PIPESTATUS[0]}"

echo "===== RECREATE CONTAINER ====="
docker compose --profile tts up -d --force-recreate uni-voices 2>&1
echo "UP_EXIT=$?"

echo "===== WAIT until all 3 engines report loaded (up to 6 min) ====="
for i in $(seq 1 72); do
  body=$(curl -s -m 8 http://127.0.0.1:8000/health 2>/dev/null)
  n=$(printf '%s' "$body" | grep -o '"loaded":[[:space:]]*true' | wc -l | tr -d ' ')
  echo "  t+$((i*5))s loaded=$n"
  if [ "${n:-0}" -ge 3 ]; then echo "ALL_LOADED"; break; fi
  sleep 5
done
echo "--- /health ---"; curl -s http://127.0.0.1:8000/health; echo

echo "===== SYNTH per engine (expect HTTP=200, size>0) ====="
synth () {
  echo "--- $1 ---"
  curl -s -m 120 -X POST http://127.0.0.1:8000/v1/synthesize/stream \
    -H 'content-type: application/json' -d "$2" \
    -o "/tmp/uv_$1.pcm" -w "  $1 HTTP=%{http_code} size=%{size_download} time=%{time_total}s\n"
}
synth kokoro '{"engine":"kokoro","voice":"af_heart","text":"Hello, this is a GPU smoke test."}'
synth roest  '{"language":"da","text":"Hej, dette er en test."}'
synth silero '{"engine":"silero-v5-russian","voice":"kseniya","text":"Привет, это тест."}'

echo "===== GPU USAGE (should show python + VRAM) ====="
docker exec cortex-uni-voices nvidia-smi --query-gpu=name,memory.used --format=csv 2>&1 | head -4
echo "===== CONTAINER STATUS (crash check) ====="
docker ps --filter name=cortex-uni-voices --format '{{.Names}} {{.Status}}'
echo "===== LAST 30 LOG LINES ====="
docker logs --tail 30 cortex-uni-voices 2>&1
echo "ALL_DONE"
