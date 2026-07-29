#!/usr/bin/env bash
#
# tools/simulation/monitor/pg-snapshot.sh — #243 Task 8 Postgres snapshotter.
#
# Three phases around a load-test run, each a subcommand:
#   start   Reset pg_stat_statements and record pg_database_size "before".
#   sample  pg_stat_activity (state/wait_event) + pg_blocking_pids +
#           connection count vs max_connections. Safe to call repeatedly
#           during a run.
#   end     Record pg_database_size "after" (diffed against `start`) and
#           dump the top pg_stat_statements rows.
#
# All queries run in-container via `docker compose -p cluckwork-sim exec -T
# db psql` — matches reset.sh's own pg_stat_statements preflight, never a
# host Postgres connection (db has no published port in this stack).
#
# Docker Block I/O (see docker-stats-sampler.sh) is network/disk TRAFFIC,
# not size — pg_database_size(current_database()) is the only correct way
# to see the database actually grow, hence the dedicated before/after here.
#
# Output lands under tools/simulation/monitor/out/ — a HOST-writable
# directory this script creates itself, deliberately separate from
# tools/simulation/out/ (written into by the app container as root via
# Simulation__CredentialOutputPath — sidestepped, not fought).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SIM_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ENV_FILE="$SIM_DIR/.env.sim"
COMPOSE_FILE="$SIM_DIR/docker-compose.sim.yml"
OUT_DIR="$SCRIPT_DIR/out"
TOP_N=20

usage() {
  cat <<EOF
Usage: $(basename "$0") <start|sample|end> [--out-dir DIR] [--top N]

  start   Reset pg_stat_statements and record pg_database_size "before".
  sample  Snapshot pg_stat_activity + pg_blocking_pids + connection count
          vs max_connections. Safe to call any number of times.
  end     Record pg_database_size "after" (diffed against \`start\`) and
          dump the top N pg_stat_statements rows by total exec time
          (default N=${TOP_N}).

  --out-dir DIR  Output directory (default: tools/simulation/monitor/out)
  --top N        Row count for the \`end\` pg_stat_statements dump
EOF
}

[[ $# -ge 1 ]] || {
  usage >&2
  exit 1
}
CMD="$1"
shift

case "$CMD" in
  start | sample | end) ;;
  -h | --help)
    usage
    exit 0
    ;;
  *)
    echo "Unknown subcommand: $CMD" >&2
    usage >&2
    exit 1
    ;;
esac

while [[ $# -gt 0 ]]; do
  case "$1" in
    --out-dir)
      OUT_DIR="$2"
      shift 2
      ;;
    --top)
      TOP_N="$2"
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
STATE_FILE="$OUT_DIR/.pg-snapshot-state"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "tools/simulation/.env.sim not found — run bootstrap.sh then reset.sh first." >&2
  exit 1
fi

# --- Same hard safety gate as reset.sh: never query against any project
# other than the dedicated cluckwork-sim one. ---
COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-cluckwork-sim}"
if [[ "$COMPOSE_PROJECT_NAME" != "cluckwork-sim" ]]; then
  echo "ABORT: resolved compose project is '${COMPOSE_PROJECT_NAME}', not 'cluckwork-sim'." >&2
  exit 1
fi

mkdir -p "$OUT_DIR"

read_env_value() {
  local key="$1"
  grep -E "^${key}=" "$ENV_FILE" | tail -n1 | cut -d'=' -f2-
}
PGDB="$(read_env_value POSTGRES_DB)"
PGUSER="$(read_env_value POSTGRES_USER)"

psql_() {
  docker compose -p "$COMPOSE_PROJECT_NAME" --env-file "$ENV_FILE" -f "$COMPOSE_FILE" \
    exec -T db psql -U "$PGUSER" -d "$PGDB" -v ON_ERROR_STOP=1 "$@"
}

ts() { date -u +%Y%m%dT%H%M%SZ; }

case "$CMD" in
  start)
    NOW="$(ts)"
    OUT_FILE="$OUT_DIR/pg-snapshot-start-${NOW}.txt"
    {
      echo "== pg-snapshot start @ ${NOW} UTC =="
      echo
      echo "-- CREATE EXTENSION IF NOT EXISTS pg_stat_statements (idempotent) --"
      psql_ -c "CREATE EXTENSION IF NOT EXISTS pg_stat_statements;"
      echo
      echo "-- pg_stat_statements_reset() --"
      psql_ -c "SELECT pg_stat_statements_reset();"
      echo
      echo "-- pg_database_size(current_database()) [before] --"
      psql_ -c "SELECT pg_database_size(current_database()) AS db_size_bytes, pg_size_pretty(pg_database_size(current_database())) AS db_size_pretty;"
    } | tee "$OUT_FILE"

    DB_SIZE_BEFORE="$(psql_ -tA -c "SELECT pg_database_size(current_database());")"
    {
      echo "DB_SIZE_BEFORE=${DB_SIZE_BEFORE}"
      echo "START_TS=${NOW}"
    } >"$STATE_FILE"
    echo "Wrote ${OUT_FILE} and ${STATE_FILE}."
    ;;

  sample)
    NOW="$(ts)"
    OUT_FILE="$OUT_DIR/pg-snapshot-sample-${NOW}.txt"
    {
      echo "== pg-snapshot sample @ ${NOW} UTC =="
      echo
      echo "-- connection count vs max_connections --"
      psql_ -c "SELECT (SELECT count(*) FROM pg_stat_activity) AS current_connections, (SELECT setting FROM pg_settings WHERE name = 'max_connections') AS max_connections;"
      echo
      echo "-- pg_stat_activity (state, wait_event) --"
      psql_ -c "SELECT pid, usename, application_name, state, wait_event_type, wait_event, backend_start, query_start, left(coalesce(query, ''), 100) AS query FROM pg_stat_activity WHERE pid <> pg_backend_pid() ORDER BY backend_start;"
      echo
      echo "-- pg_blocking_pids per active backend (rows only where something is blocked) --"
      psql_ -c "SELECT pid, pg_blocking_pids(pid) AS blocked_by FROM pg_stat_activity WHERE pid <> pg_backend_pid() AND cardinality(pg_blocking_pids(pid)) > 0;"
    } | tee "$OUT_FILE"
    echo "Wrote ${OUT_FILE}."
    ;;

  end)
    NOW="$(ts)"
    OUT_FILE="$OUT_DIR/pg-snapshot-end-${NOW}.txt"
    DB_SIZE_AFTER="$(psql_ -tA -c "SELECT pg_database_size(current_database());")"

    DB_SIZE_BEFORE=""
    START_TS=""
    if [[ -f "$STATE_FILE" ]]; then
      # shellcheck disable=SC1090
      source "$STATE_FILE"
    fi

    {
      echo "== pg-snapshot end @ ${NOW} UTC =="
      echo
      echo "-- pg_database_size(current_database()) [after] --"
      psql_ -c "SELECT pg_database_size(current_database()) AS db_size_bytes, pg_size_pretty(pg_database_size(current_database())) AS db_size_pretty;"
      echo
      if [[ -n "$DB_SIZE_BEFORE" ]]; then
        DELTA=$((DB_SIZE_AFTER - DB_SIZE_BEFORE))
        echo "-- delta vs start (${START_TS:-unknown}) --"
        echo "db_size_before_bytes=${DB_SIZE_BEFORE}"
        echo "db_size_after_bytes=${DB_SIZE_AFTER}"
        echo "db_size_delta_bytes=${DELTA}"
      else
        echo "-- no start state found (${STATE_FILE}) — run \`pg-snapshot.sh start\` first for a before/after delta --"
      fi
      echo
      echo "-- connection count vs max_connections --"
      psql_ -c "SELECT (SELECT count(*) FROM pg_stat_activity) AS current_connections, (SELECT setting FROM pg_settings WHERE name = 'max_connections') AS max_connections;"
      echo
      echo "-- top ${TOP_N} pg_stat_statements by total_exec_time --"
      psql_ -c "SELECT calls, round(total_exec_time::numeric, 2) AS total_exec_time_ms, round(mean_exec_time::numeric, 2) AS mean_exec_time_ms, rows, left(query, 120) AS query FROM pg_stat_statements ORDER BY total_exec_time DESC LIMIT ${TOP_N};"
    } | tee "$OUT_FILE"
    echo "Wrote ${OUT_FILE}."
    ;;
esac
