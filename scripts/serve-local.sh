#!/usr/bin/env bash
# serve-local.sh — bring up the local planning-loop models for ember (ADR 10).
#
# Two vLLM servers, co-resident on one ~12 GB NVIDIA card:
#   planner on :8000   critic on :8001
# Run inside WSL2.   Start:  bash serve-local.sh        Stop:  bash serve-local.sh stop
set -euo pipefail

# --- EDIT THESE: the AWQ models you pulled (HF repo id or local path) --------
PLANNER_MODEL="${PLANNER_MODEL:-Qwen/Qwen2.5-7B-Instruct-AWQ}"
CRITIC_MODEL="${CRITIC_MODEL:-Qwen/Qwen2.5-3B-Instruct-AWQ}"
# The --served-model-name values below are fixed aliases that already match
# appsettings.Development.json — leave them alone; only change the lines above.
# -----------------------------------------------------------------------------

LOG_DIR="${LOG_DIR:-$HOME/.ember-vllm}"
mkdir -p "$LOG_DIR"

start_one() {  # name model port served-name gpu-util max-model-len
  echo "starting $1: $2 on :$3 (util $5, max-model-len $6)"
  nohup vllm serve "$2" \
    --served-model-name "$4" \
    --port "$3" \
    --gpu-memory-utilization "$5" \
    --max-model-len "$6" \
    > "$LOG_DIR/$1.log" 2>&1 &
  echo $! > "$LOG_DIR/$1.pid"
}

wait_ready() {  # port name
  echo -n "waiting for $2 on :$1 "
  for _ in $(seq 1 150); do
    if curl -sf "http://localhost:$1/v1/models" >/dev/null 2>&1; then
      echo " ready"; return 0
    fi
    echo -n "."; sleep 2
  done
  echo " TIMED OUT — see $LOG_DIR/$2.log"; return 1
}

stop_all() {
  for name in planner critic; do
    if [[ -f "$LOG_DIR/$name.pid" ]]; then
      kill "$(cat "$LOG_DIR/$name.pid")" 2>/dev/null || true
      rm -f "$LOG_DIR/$name.pid"
      echo "stopped $name"
    fi
  done
}

if [[ "${1:-start}" == "stop" ]]; then
  stop_all
  exit 0
fi

# Planner first: it claims its VRAM before the critic measures free memory.
start_one planner "$PLANNER_MODEL" 8000 Qwen3.6-8B-AWQ 0.55 8192
wait_ready 8000 planner || { stop_all; exit 1; }
start_one critic "$CRITIC_MODEL" 8001 Qwen3.6-4B-AWQ 0.35 8192
wait_ready 8001 critic || { stop_all; exit 1; }

echo
echo "both servers up.   logs: $LOG_DIR    stop: bash serve-local.sh stop"
