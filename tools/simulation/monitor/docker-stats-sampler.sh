#!/usr/bin/env bash
#
# tools/simulation/monitor/docker-stats-sampler.sh — #243 Task 8 container
# resource sampler.
#
# Samples `docker stats --no-stream` for every container in the
# cluckwork-sim compose project (app, db, otel-collector) on a fixed
# interval and appends CSV rows to an output file. Runs strictly under the
# `cluckwork-sim` compose project (same hard safety gate as reset.sh) —
# container names/IDs come from `docker compose -p cluckwork-sim ps -q`,
# never a bare `docker stats` over the whole host.
#
# Stops on Ctrl-C/SIGTERM, or automatically after --duration seconds.
#
# Output lands under tools/simulation/monitor/out/ — a HOST-writable
# directory this script creates itself. Deliberately not
# tools/simulation/out/: that directory is written into by the one-shot
# seed container as root (reset.sh forces --user 0 since the image is
# non-root, #267; Simulation__CredentialOutputPath), so files landing
# there are root-owned and the host user often can't write next to them.
# Using a sibling directory sidesteps that rather than chown/sudo.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SIM_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ENV_FILE="$SIM_DIR/.env.sim"
COMPOSE_FILE="$SIM_DIR/docker-compose.sim.yml"
OUT_DIR="$SCRIPT_DIR/out"

INTERVAL=2
DURATION=0 # 0 = run until Ctrl-C/SIGTERM
OUT_FILE=""

usage() {
  cat <<EOF
Usage: $(basename "$0") [--interval SECONDS] [--duration SECONDS] [--out FILE]

Samples \`docker stats --no-stream\` for every container in the
cluckwork-sim compose project (app, db, otel-collector) on a fixed interval
and appends CSV rows (timestamp,container,cpu_pct,mem,net_io,block_io).

  --interval SECONDS  Sampling interval in seconds (default: 2)
  --duration SECONDS  Stop automatically after this many seconds
                       (default: 0 = run until Ctrl-C / SIGTERM)
  --out FILE          CSV output path
                       (default: tools/simulation/monitor/out/docker-stats-<UTC timestamp>.csv)
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --interval)
      INTERVAL="$2"
      shift 2
      ;;
    --duration)
      DURATION="$2"
      shift 2
      ;;
    --out)
      OUT_FILE="$2"
      shift 2
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ ! -f "$ENV_FILE" ]]; then
  echo "tools/simulation/.env.sim not found — run bootstrap.sh then reset.sh first." >&2
  exit 1
fi

# --- Same hard safety gate as reset.sh: never sample against any project
# other than the dedicated cluckwork-sim one. ---
COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-cluckwork-sim}"
if [[ "$COMPOSE_PROJECT_NAME" != "cluckwork-sim" ]]; then
  echo "ABORT: resolved compose project is '${COMPOSE_PROJECT_NAME}', not 'cluckwork-sim'." >&2
  exit 1
fi

compose() {
  docker compose -p "$COMPOSE_PROJECT_NAME" --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
}

mkdir -p "$OUT_DIR"
if [[ -z "$OUT_FILE" ]]; then
  OUT_FILE="$OUT_DIR/docker-stats-$(date -u +%Y%m%dT%H%M%SZ).csv"
fi

if [[ ! -s "$OUT_FILE" ]]; then
  echo "timestamp,container,cpu_pct,mem,net_io,block_io" >"$OUT_FILE"
fi

STOP=0
trap 'STOP=1' INT TERM

echo "Sampling cluckwork-sim containers every ${INTERVAL}s -> ${OUT_FILE}"
if [[ "$DURATION" -gt 0 ]]; then
  echo "(auto-stop after ${DURATION}s, or Ctrl-C sooner)"
else
  echo "(Ctrl-C to stop)"
fi

start_ts=$(date +%s)
while [[ "$STOP" -eq 0 ]]; do
  # shellcheck disable=SC2046 # container IDs never contain whitespace
  containers=$(compose ps -q)
  if [[ -n "$containers" ]]; then
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    # One `docker stats` call across all containers so every row in this
    # sample shares the same timestamp, rather than smearing it across a
    # per-container loop.
    # shellcheck disable=SC2086
    docker stats --no-stream --format '{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\t{{.NetIO}}\t{{.BlockIO}}' $containers |
      while IFS=$'\t' read -r name cpu mem net_io block_io; do
        printf '%s,%s,%s,"%s","%s","%s"\n' "$now" "$name" "$cpu" "$mem" "$net_io" "$block_io" >>"$OUT_FILE"
      done
  fi

  if [[ "$DURATION" -gt 0 ]]; then
    elapsed=$(($(date +%s) - start_ts))
    if ((elapsed >= DURATION)); then
      break
    fi
  fi

  sleep "$INTERVAL"
done

rows=$(($(wc -l <"$OUT_FILE") - 1))
echo "Stopped. ${rows} sample rows in ${OUT_FILE}"
