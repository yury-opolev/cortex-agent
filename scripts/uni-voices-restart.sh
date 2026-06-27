#!/usr/bin/env bash
# Stop the old single-engine danish-tts container (it squats on :8000), then
# start the unified uni-voices sidecar and validate GPU + all three engines.
set +e
cd "C:/Users/yurio/Documents/github/cortex" || exit 1

echo "===== CURRENT CONTAINERS ====="
docker ps --format '{{.Names}}\t{{.Image}}\t{{.Ports}}'

echo "===== STOP/REMOVE OLD danish-tts (orphan on :8000) ====="
for c in $(docker ps -a --format '{{.Names}}' | grep -i danish); do
  echo "removing $c"; docker stop "$c"; docker rm "$c"
done

echo "===== START uni-voices (remove orphans) ====="
docker compose --profile tts up -d --remove-orphans uni-voices 2>&1
echo "UP_EXIT=$?"

echo "===== WAIT FOR /health (expect engines{} schema) ====="
for i in $(seq 1 120); do
  code=$(curl -s -o /tmp/h.json -w '%{http_code}' http://127.0.0.1:8000/health 2>/dev/null)
  if [ "$code" = "200" ]; then echo "HTTP200 after $((i*5))s"; break; fi
  sleep 5
done
echo "--- /health ---"; cat /tmp/h.json 2>/dev/null; echo

echo "===== CONTAINER STATUS (crash check) ====="
docker ps --filter name=cortex-uni-voices --format '{{.Names}} {{.Status}}'

echo "===== GPU VISIBLE IN CONTAINER ====="
docker exec cortex-uni-voices nvidia-smi 2>&1 | head -22

echo "===== SYNTH per engine (forces GPU model load) ====="
synth () {
  echo "--- $1 ---"
  curl -s -X POST http://127.0.0.1:8000/v1/synthesize/stream \
    -H 'content-type: application/json' -d "$2" \
    --max-time 600 --output "/tmp/uv_$1.pcm" \
    -w "  HTTP=%{http_code} size=%{size_download} time=%{time_total}s\n"
}
synth kokoro '{"engine":"kokoro","voice":"af_heart","text":"Hello, this is a GPU smoke test."}'
synth roest  '{"language":"da","text":"Hej, dette er en test."}'
synth silero '{"engine":"silero-v5-russian","text":"Привет, это тест."}'

echo "===== /health AFTER loads ====="
curl -s http://127.0.0.1:8000/health 2>/dev/null; echo
echo "===== GPU AFTER LOAD (python proc + VRAM) ====="
docker exec cortex-uni-voices nvidia-smi 2>&1 | head -22
echo "===== LAST 50 LOG LINES ====="
docker logs --tail 50 cortex-uni-voices 2>&1
echo "ALL_DONE"
