#!/usr/bin/env bash
#
# tools/simulation/run-baseline.sh — #243 Task 9 orchestrator.
#
# Runs REPS (default 3) independent baseline reps against a FRESH
# cluckwork-sim stack each time. Per rep:
#   reset.sh (fresh stack + seed)
#   -> start docker-stats-sampler.sh (background) + pg-snapshot.sh start
#   -> k6 run baseline.js (RUN_ID + REP env, per-rep SUMMARY_OUT)
#   -> pg-snapshot.sh end + stop the sampler
#   -> collect that rep's capacity summary + monitor outputs into
#      tools/simulation/monitor/out/<run-id>/rep-<n>/
# then aggregates across reps (median + spread) and renders an honest
# findings doc from tools/simulation/findings/TEMPLATE.md into
# tools/simulation/findings/<run-id>-findings.md.
#
# SHORT durations by default: this script does not redeclare or override
# any WARMUP_*/CAPACITY_*/BASE_URL default — those are k6/baseline.js's own
# (20s warmup + 2m capacity), inherited by the k6 subprocess exactly like
# any other exported env var. There is exactly one place that owns those
# defaults (baseline.js), so this script can never drift from it. For a
# REAL #243 Task 10 run, override via env, e.g.:
#
#   REPS=3 CAPACITY_DURATION=40m bash tools/simulation/run-baseline.sh
#
# For a fast dev/verification cycle:
#
#   REPS=2 WARMUP_DURATION=10s CAPACITY_DURATION=25s \
#     bash tools/simulation/run-baseline.sh
#
# #243 Task 9 MUST-FIX verification: every rep's capacity summary is parsed
# back out and checked for all 5 personas (Owner/Manager/Sales/Worker/
# ReadOnly) actually producing capacity-phase requests — surfaced both on
# stdout below and in the findings doc's own "Persona coverage" section.
#
# NOTE on pg_stat_statements scope: `pg-snapshot.sh start` runs once per
# rep, right before `k6 run` (i.e. before warmup too, not just capacity) —
# so the pg-snapshot dump covers warmup+capacity together, while every
# number this script pulls out of k6's own summary.json stays
# capacity-only (k6's `phase:capacity` tag filter, baked into baseline.js).
# The alternative (delaying pg-snapshot start until capacity's own
# startTime) would need this script to duplicate baseline.js's drain-gap
# arithmetic and race k6's own process-launch jitter to hit the boundary —
# fragile for a difference the findings doc documents plainly instead. See
# the findings template's "Database growth" section.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SIM_DIR="$SCRIPT_DIR"
REPO_ROOT="$(cd "$SIM_DIR/../.." && pwd)"
MONITOR_DIR="$SIM_DIR/monitor"
MONITOR_OUT_DIR="$MONITOR_DIR/out"
RESET_SCRIPT="$SIM_DIR/reset.sh"
SAMPLER_SCRIPT="$MONITOR_DIR/docker-stats-sampler.sh"
PGSNAP_SCRIPT="$MONITOR_DIR/pg-snapshot.sh"
K6_SCRIPT="$SIM_DIR/k6/baseline.js"
FINDINGS_DIR="$SIM_DIR/findings"
FINDINGS_TEMPLATE="$FINDINGS_DIR/TEMPLATE.md"
SIM_OUT_MANIFEST="$SIM_DIR/out/manifest.json"
SIM_CAST_FILE="$SIM_DIR/.sim-cast.json"
ENV_SIM_FILE="$SIM_DIR/.env.sim"
K6_SHELL_NIX="$SIM_DIR/k6/shell.nix"
# PR #279 review — pinned k6 version (see k6/shell.nix's own header for why):
# baseline.js's inter-phase drain gap was only ever live-probed against this
# exact version. Bump the two together, deliberately, never independently.
EXPECTED_K6_VERSION="v2.0.0"

RENDER_ONLY_RUN_ID=""

usage() {
  cat <<EOF
Usage: $(basename "$0") [--render-only RUN_ID]

  --render-only RUN_ID   Skip reset/docker/k6 entirely and re-run just the
                          aggregation + findings-doc render step against an
                          EXISTING tools/simulation/monitor/out/RUN_ID/rep-*/
                          tree. Useful for verifying a rendering/TEMPLATE.md
                          change without re-running a (possibly multi-hour)
                          live baseline.

Env vars this script reads directly:
  REPS               Number of reps (default: 3)
  RUN_ID             Run identifier / output subdir name (default: run-<UTC timestamp>)
  SAMPLER_INTERVAL   docker-stats-sampler.sh --interval seconds (default: 2)
  PG_TOP_N           pg-snapshot.sh end --top N (default: 20)

Every other env var (WARMUP_VUS, WARMUP_DURATION, CAPACITY_VUS,
CAPACITY_DURATION, WARMUP_GRACEFUL_STOP, INTER_PHASE_DRAIN_BUFFER_SECONDS,
CAPACITY_LOGIN_JITTER_SECONDS, BASE_URL, ...) is simply inherited by the k6
subprocess exactly as k6/baseline.js itself defines/defaults it — see that
file's header. Requires bootstrap.sh to have already run once
(.env.sim/.sim-cast.json present); reset.sh is run once per rep.

Examples:
  bash tools/simulation/run-baseline.sh                        # 3 reps, dev-short durations
  REPS=2 WARMUP_DURATION=10s CAPACITY_DURATION=25s bash tools/simulation/run-baseline.sh
  REPS=3 CAPACITY_DURATION=40m bash tools/simulation/run-baseline.sh   # #243 Task 10 real run
  bash tools/simulation/run-baseline.sh --render-only run-20260729T170130Z
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -h | --help)
      usage
      exit 0
      ;;
    --render-only)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "--render-only requires a RUN_ID argument" >&2
        usage >&2
        exit 1
      fi
      RENDER_ONLY_RUN_ID="$2"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

# --- preflight: required files/tools --------------------------------------

if [[ ! -f "$FINDINGS_TEMPLATE" ]]; then
  echo "run-baseline: required file missing: $FINDINGS_TEMPLATE" >&2
  exit 1
fi
command -v python3 >/dev/null 2>&1 || { echo "run-baseline: python3 not found (needed for aggregation)." >&2; exit 1; }
command -v git >/dev/null 2>&1 || { echo "run-baseline: git not found." >&2; exit 1; }

if [[ -n "$RENDER_ONLY_RUN_ID" ]]; then
  # --render-only: re-aggregate + re-render an EXISTING run's already-
  # collected rep-*/summary.json files — no reset/docker/k6/sampler/
  # pg-snapshot involved at all, so none of the live-path checks below apply.
  RUN_ID="$RENDER_ONLY_RUN_ID"
  if [[ ! "$RUN_ID" =~ ^[A-Za-z0-9._-]+$ ]]; then
    echo "run-baseline: RUN_ID '${RUN_ID}' contains characters outside [A-Za-z0-9._-] — refusing." >&2
    exit 1
  fi
  RUN_DIR="$MONITOR_OUT_DIR/$RUN_ID"
  if [[ ! -d "$RUN_DIR" ]] || ! find "$RUN_DIR" -mindepth 1 -maxdepth 1 -name 'rep-*' -print -quit | grep -q .; then
    echo "run-baseline: --render-only ${RUN_ID} but ${RUN_DIR} has no rep-* directories to render from." >&2
    exit 1
  fi
  # REPS is otherwise set by the live path below (default 3, or an env
  # override) — in render-only mode there's no live run to read it from, so
  # derive it from what's actually on disk (only used for display/the
  # params table's `REPS` row; the aggregator itself globs rep-* directly).
  REPS="$(find "$RUN_DIR" -mindepth 1 -maxdepth 1 -type d -name 'rep-*' | wc -l | tr -d ' ')"
  echo "== #243 run-baseline: --render-only RUN_ID=${RUN_ID} (re-rendering from existing raw data, no live run) =="
  echo
else
  for f in "$RESET_SCRIPT" "$SAMPLER_SCRIPT" "$PGSNAP_SCRIPT" "$K6_SCRIPT" "$K6_SHELL_NIX"; do
    if [[ ! -f "$f" ]]; then
      echo "run-baseline: required file missing: $f" >&2
      exit 1
    fi
  done
  if [[ ! -f "$ENV_SIM_FILE" || ! -f "$SIM_CAST_FILE" ]]; then
    echo "run-baseline: tools/simulation/.env.sim or .sim-cast.json not found — run bootstrap.sh first." >&2
    exit 1
  fi

  command -v nix-shell >/dev/null 2>&1 || { echo "run-baseline: nix-shell not found (needed to run k6)." >&2; exit 1; }
  command -v docker >/dev/null 2>&1 || { echo "run-baseline: docker not found." >&2; exit 1; }

  # Same hard safety gate as reset.sh and the monitor scripts. Every docker
  # command this orchestrator triggers happens inside a child script that
  # already gates itself independently — this is defense in depth, not the
  # only thing standing between an override and the real dev DB volume.
  COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-cluckwork-sim}"
  if [[ "$COMPOSE_PROJECT_NAME" != "cluckwork-sim" ]]; then
    echo "ABORT: resolved compose project is '${COMPOSE_PROJECT_NAME}', not 'cluckwork-sim'." >&2
    echo "Refusing to continue — see reset.sh for why this must never be overridden." >&2
    exit 1
  fi

  # PR #279 review — pinned k6 version: fail loudly on ANY drift before
  # starting rep 1, not partway through a (possibly multi-hour) run. See
  # k6/shell.nix's own header for why this matters (baseline.js's
  # inter-phase drain gap relies on VU-scheduling behavior only ever
  # live-probed against this exact version).
  echo "[preflight] verifying pinned k6 version (expect k6 ${EXPECTED_K6_VERSION})..."
  k6_actual_version_line="$(nix-shell "$K6_SHELL_NIX" --run 'k6 version' 2>/dev/null || true)"
  if [[ "$k6_actual_version_line" != "k6 ${EXPECTED_K6_VERSION}"* ]]; then
    echo "run-baseline: k6 version mismatch — pinned shell.nix resolved to '${k6_actual_version_line:-<none>}', expected 'k6 ${EXPECTED_K6_VERSION}'." >&2
    echo "  baseline.js's VU-scheduling (drain-gap) assumptions were only live-probed against ${EXPECTED_K6_VERSION} — see that file's header. Investigate before trusting this run's per-user capacity coverage." >&2
    echo "  If this is a deliberate, reviewed k6 upgrade: re-verify the drain gap against the new version, then bump EXPECTED_K6_VERSION here AND the pinned revision in k6/shell.nix together." >&2
    exit 1
  fi
  echo "[preflight] ${k6_actual_version_line} confirmed (pinned via ${K6_SHELL_NIX})."

  REPS="${REPS:-3}"
  RUN_ID="${RUN_ID:-run-$(date -u +%Y%m%dT%H%M%SZ)}"
  SAMPLER_INTERVAL="${SAMPLER_INTERVAL:-2}"
  PG_TOP_N="${PG_TOP_N:-20}"

  # PR #279 review: RUN_ID is used verbatim as a directory/filename
  # component below (RUN_DIR, findings/<RUN_ID>-findings.md) — restrict it to
  # a safe charset rather than trusting whatever the caller's env happened
  # to set.
  if [[ ! "$RUN_ID" =~ ^[A-Za-z0-9._-]+$ ]]; then
    echo "run-baseline: RUN_ID '${RUN_ID}' contains characters outside [A-Za-z0-9._-] — refusing." >&2
    exit 1
  fi

  # PR #279 review — per-cast-user capacity coverage (baseline.js's own
  # setup() hard-errors on this too) only holds when CAPACITY_VUS equals the
  # cast size exactly. Preflight it here too so a bad override fails BEFORE
  # burning a whole rep's reset.sh + k6 run, not partway into rep 1.
  if [[ -n "${CAPACITY_VUS:-}" ]]; then
    cast_size_from_file="$(python3 -c "
import json, sys
d = json.load(open(sys.argv[1]))
print(1 + len(d.get('cast', [])))
" "$SIM_CAST_FILE")"
    if [[ "$CAPACITY_VUS" != "$cast_size_from_file" ]]; then
      echo "run-baseline: CAPACITY_VUS (${CAPACITY_VUS}) must equal the cast size in .sim-cast.json (${cast_size_from_file}) — the per-user capacity coverage guarantee only holds at equality (baseline.js's setup() hard-errors on this too). Unset CAPACITY_VUS to use the cast-size default, or regenerate the cast via bootstrap.sh." >&2
      exit 1
    fi
  fi

  RUN_DIR="$MONITOR_OUT_DIR/$RUN_ID"
  # PR #279 review: `mkdir -p` used to silently REUSE an existing (possibly
  # stale/partial) RUN_DIR, letting a reused RUN_ID mix this run's reps with
  # a previous run's. Refuse instead — an empty/nonexistent dir is fine
  # (first use of this RUN_ID), anything already in it is not.
  if [[ -d "$RUN_DIR" ]] && find "$RUN_DIR" -mindepth 1 -print -quit | grep -q .; then
    echo "run-baseline: RUN_DIR '${RUN_DIR}' already exists and is non-empty — refusing to reuse a RUN_ID (would silently mix stale reps with this run's data). Pick a different RUN_ID or remove that directory first." >&2
    exit 1
  fi
  mkdir -p "$RUN_DIR"

  echo "== #243 run-baseline: RUN_ID=${RUN_ID} REPS=${REPS} =="
  echo "   per-rep results -> ${RUN_DIR}/rep-<n>/"
  echo "   findings template -> ${FINDINGS_TEMPLATE}"
  echo
fi

overall_exit=0

# PR #279 review: without this, aborting mid-run (any `set -e` failure, or a
# signal) left that rep's background docker-stats-sampler orphaned — nothing
# ever killed it. `sampler_pid` is a plain (non-local) var, reassigned each
# rep and already explicitly killed at the normal point in the happy path
# below; this trap is the backstop for every OTHER exit path. Single-quoted
# so `$sampler_pid` is read at trap-FIRE time (whichever rep was most
# recently started), not at registration time — one registration here
# correctly covers every rep in the loop.
trap 'kill -TERM "${sampler_pid:-}" 2>/dev/null; wait "${sampler_pid:-}" 2>/dev/null || true' EXIT

if [[ -n "$RENDER_ONLY_RUN_ID" ]]; then
  echo "[render-only] skipping reset/docker/k6/monitoring — re-aggregating existing rep-*/ data under ${RUN_DIR}"
  echo
else

for rep in $(seq 1 "$REPS"); do
  rep_dir="$RUN_DIR/rep-${rep}"
  mkdir -p "$rep_dir"
  echo "--- rep ${rep}/${REPS} --------------------------------------------------"

  echo "[rep ${rep}] reset.sh (fresh stack + seed)..."
  reset_start_epoch=$(date +%s)
  bash "$RESET_SCRIPT"
  reset_end_epoch=$(date +%s)
  reset_wall_clock_seconds=$((reset_end_epoch - reset_start_epoch))

  echo "[rep ${rep}] capturing manifest.json..."
  cp "$SIM_OUT_MANIFEST" "$rep_dir/manifest.json"

  echo "[rep ${rep}] starting docker-stats-sampler (background)..."
  bash "$SAMPLER_SCRIPT" --interval "$SAMPLER_INTERVAL" --out "$rep_dir/docker-stats.csv" \
    >"$rep_dir/docker-stats-sampler.log" 2>&1 &
  sampler_pid=$!

  echo "[rep ${rep}] pg-snapshot start (resets pg_stat_statements; see file header re: scope)..."
  # PR #279 review: wrapped in set +e/set -e the SAME way k6 already was
  # below — previously an unguarded failure here (under the script-wide
  # `set -euo pipefail`) aborted the ENTIRE multi-rep run immediately,
  # leaking the just-started background sampler (now also caught by the
  # EXIT trap above) and losing every later rep. A monitor hiccup now flags
  # this rep instead of killing the run.
  set +e
  bash "$PGSNAP_SCRIPT" start --out-dir "$rep_dir" >"$rep_dir/pg-snapshot-start.log" 2>&1
  pgsnap_start_exit=$?
  set -e
  if [[ "$pgsnap_start_exit" -ne 0 ]]; then
    echo "[rep ${rep}] WARNING: pg-snapshot start exited ${pgsnap_start_exit} — this rep's DB-growth/pg_stat_statements data may be incomplete. See ${rep_dir}/pg-snapshot-start.log." >&2
    overall_exit=1
  fi

  echo "[rep ${rep}] k6 baseline (RUN_ID=${RUN_ID} REP=${rep})..."
  k6_start_epoch=$(date +%s)
  set +e
  # PR #279 review: pinned via k6/shell.nix instead of a bare `nix-shell -p
  # k6` — see that file's header and the pinned-version preflight above.
  SUMMARY_OUT="$rep_dir/summary.json" RUN_ID="$RUN_ID" REP="$rep" \
    nix-shell "$K6_SHELL_NIX" --run "k6 run '$K6_SCRIPT'" >"$rep_dir/k6.log" 2>&1
  k6_exit=$?
  set -e
  k6_end_epoch=$(date +%s)
  k6_wall_clock_seconds=$((k6_end_epoch - k6_start_epoch))

  if [[ "$k6_exit" -ne 0 ]]; then
    echo "[rep ${rep}] k6 exited ${k6_exit} — likely a threshold breach (checks<100%, an" >&2
    echo "[rep ${rep}] unexpected status, or a missing persona/cast-user). FLAGGED but NOT" >&2
    echo "[rep ${rep}] dropped — its data is still collected and published. See ${rep_dir}/k6.log." >&2
    overall_exit=1
  fi

  echo "[rep ${rep}] pg-snapshot end..."
  set +e
  bash "$PGSNAP_SCRIPT" end --out-dir "$rep_dir" --top "$PG_TOP_N" >"$rep_dir/pg-snapshot-end.log" 2>&1
  pgsnap_end_exit=$?
  set -e
  if [[ "$pgsnap_end_exit" -ne 0 ]]; then
    echo "[rep ${rep}] WARNING: pg-snapshot end exited ${pgsnap_end_exit} — this rep's DB-growth/pg_stat_statements data may be incomplete. See ${rep_dir}/pg-snapshot-end.log." >&2
    overall_exit=1
  fi

  echo "[rep ${rep}] stopping docker-stats-sampler..."
  kill -TERM "$sampler_pid" 2>/dev/null || true
  wait "$sampler_pid" 2>/dev/null || true

  rep_end_epoch=$(date +%s)
  rep_wall_clock_seconds=$((rep_end_epoch - reset_start_epoch))

  python3 - "$rep_dir/meta.json" "$rep" "$k6_exit" "$reset_wall_clock_seconds" \
    "$k6_wall_clock_seconds" "$rep_wall_clock_seconds" <<'PY'
import json
import sys

out_path, rep, k6_exit, reset_wc, k6_wc, rep_wc = sys.argv[1:7]
json.dump(
    {
        "rep": int(rep),
        "k6ExitCode": int(k6_exit),
        "resetWallClockSeconds": int(reset_wc),
        "k6WallClockSeconds": int(k6_wc),
        "repWallClockSeconds": int(rep_wc),
    },
    open(out_path, "w"),
    indent=2,
)
PY

  if [[ -f "$rep_dir/summary.json" ]]; then
    echo "[rep ${rep}] collected: summary.json, manifest.json, docker-stats.csv, pg-snapshot-{start,end}-*.txt, k6.log, meta.json"
  else
    echo "[rep ${rep}] WARNING: no summary.json written (k6 likely crashed before handleSummary) — see k6.log." >&2
    overall_exit=1
  fi
  echo
done

fi  # end of the live (non-render-only) per-rep loop

if [[ -n "$RENDER_ONLY_RUN_ID" ]]; then
  echo "== render-only: aggregating + rendering findings doc for RUN_ID=${RUN_ID} =="
else
  echo "== all ${REPS} rep(s) complete — aggregating + rendering findings doc =="
fi

COMMIT_SHA="$(git -C "$REPO_ROOT" rev-parse HEAD)"
COMMIT_SHA_SHORT="$(git -C "$REPO_ROOT" rev-parse --short HEAD)"
GIT_BRANCH="$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD)"
GIT_DIRTY="0"
[[ -n "$(git -C "$REPO_ROOT" status --porcelain)" ]] && GIT_DIRTY="1"
GENERATED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
FINDINGS_OUT="$FINDINGS_DIR/${RUN_ID}-findings.md"
AGGREGATE_OUT="$RUN_DIR/aggregate.json"

set +e
RUN_DIR="$RUN_DIR" RUN_ID="$RUN_ID" REPS="$REPS" REPO_ROOT="$REPO_ROOT" SIM_DIR="$SIM_DIR" \
  COMMIT_SHA="$COMMIT_SHA" COMMIT_SHA_SHORT="$COMMIT_SHA_SHORT" GIT_BRANCH="$GIT_BRANCH" \
  GIT_DIRTY="$GIT_DIRTY" GENERATED_AT="$GENERATED_AT" FINDINGS_TEMPLATE="$FINDINGS_TEMPLATE" \
  FINDINGS_OUT="$FINDINGS_OUT" AGGREGATE_OUT="$AGGREGATE_OUT" \
  python3 - <<'PY'
import csv
import json
import os
import re
import statistics
import sys
from pathlib import Path

RUN_DIR = Path(os.environ["RUN_DIR"])
RUN_ID = os.environ["RUN_ID"]
REPS = int(os.environ["REPS"])
REPO_ROOT = Path(os.environ["REPO_ROOT"])
COMMIT_SHA = os.environ["COMMIT_SHA"]
COMMIT_SHA_SHORT = os.environ["COMMIT_SHA_SHORT"]
GIT_BRANCH = os.environ["GIT_BRANCH"]
GIT_DIRTY = os.environ["GIT_DIRTY"] == "1"
GENERATED_AT = os.environ["GENERATED_AT"]
TEMPLATE_PATH = Path(os.environ["FINDINGS_TEMPLATE"])
OUTPUT_MD_PATH = Path(os.environ["FINDINGS_OUT"])
OUTPUT_JSON_PATH = Path(os.environ["AGGREGATE_OUT"])

# ---- load per-rep artifacts ------------------------------------------------

reps = []
for rep_dir in sorted(RUN_DIR.glob("rep-*"), key=lambda p: int(p.name.split("-")[1])):
    rep_num = int(rep_dir.name.split("-")[1])
    summary_path = rep_dir / "summary.json"
    manifest_path = rep_dir / "manifest.json"
    meta_path = rep_dir / "meta.json"
    entry = {"rep": rep_num, "dir": rep_dir, "missingSummary": not summary_path.exists()}
    if not entry["missingSummary"]:
        entry["summary"] = json.loads(summary_path.read_text())
    if manifest_path.exists():
        entry["manifest"] = json.loads(manifest_path.read_text())
    if meta_path.exists():
        entry["meta"] = json.loads(meta_path.read_text())
    reps.append(entry)

if not reps:
    print(f"run-baseline: no rep-* directories found under {RUN_DIR}", file=sys.stderr)
    sys.exit(1)

ok_reps = [r for r in reps if not r["missingSummary"]]
missing_reps = [r for r in reps if r["missingSummary"]]
if not ok_reps:
    print("run-baseline: every rep is missing summary.json — nothing to aggregate.", file=sys.stderr)
    sys.exit(1)

# ---- helpers ----------------------------------------------------------------

def median(values):
    vals = [v for v in values if isinstance(v, (int, float))]
    return statistics.median(vals) if vals else None


def fmt_num(v, nd=2):
    return f"{v:.{nd}f}" if isinstance(v, (int, float)) else "n/a"


# PR #279 review (Fix 1 — locked "Fork B: no absolute numbers" contract):
# the rendered findings Markdown must carry NO absolute latency (ms) or
# throughput (req/s) figures anywhere — only relative/shape data (ratios,
# per-persona/flow deltas vs the overall, distribution shape, cross-rep
# spread as an observation). Raw absolute numbers stay ONLY in the
# git-ignored raw JSON (summary.json/aggregate.json) this script also
# writes — fmt_ratio/fmt_pct below are the only formatters the Markdown
# tables below are allowed to use for anything latency/throughput-shaped.
def fmt_ratio(v):
    return f"{v:.2f}x" if isinstance(v, (int, float)) else "n/a"


def fmt_pct(v):
    return f"{v:+.1f}%" if isinstance(v, (int, float)) else "n/a"


def ratio_of(v, base):
    if isinstance(v, (int, float)) and isinstance(base, (int, float)) and base:
        return v / base
    return None


def pct_delta_vs_median(values):
    med = median(values)
    out = []
    for v in values:
        if isinstance(v, (int, float)) and med:
            out.append((v - med) / med * 100)
        else:
            out.append(None)
    return out


def parse_duration_seconds(s):
    if not s:
        return None
    total, found = 0, False
    for value, unit in re.findall(r"(\d+)\s*(h|m|s)", s):
        found = True
        v = int(value)
        total += v * 3600 if unit == "h" else v * 60 if unit == "m" else v
    return total if found else None


def parse_size_to_mib(s):
    m = re.match(r"^([\d.]+)\s*([A-Za-z]+)$", s.strip())
    if not m:
        return None
    val = float(m.group(1))
    unit = m.group(2)
    mult = {
        "B": 1 / 1024 / 1024, "KiB": 1 / 1024, "MiB": 1, "GiB": 1024, "TiB": 1024 * 1024,
        "KB": 1 / 1024, "MB": 1, "GB": 1024, "TB": 1024 * 1024,
    }.get(unit)
    return val * mult if mult else None


def read_docker_stats(csv_path):
    by_container = {}
    if not csv_path.exists():
        return by_container
    with open(csv_path, newline="") as f:
        for row in csv.DictReader(f):
            container = row.get("container", "")
            if not container:
                continue
            cpu_raw = (row.get("cpu_pct") or "").strip().rstrip("%")
            try:
                cpu = float(cpu_raw)
            except ValueError:
                cpu = None
            mem_field = row.get("mem", "") or ""
            used = mem_field.split("/")[0].strip() if "/" in mem_field else mem_field.strip()
            mem_mib = parse_size_to_mib(used) if used else None
            bucket = by_container.setdefault(container, {"cpu": [], "mem": []})
            if cpu is not None:
                bucket["cpu"].append(cpu)
            if mem_mib is not None:
                bucket["mem"].append(mem_mib)
    return by_container


def parse_pg_end_file(rep_dir):
    files = sorted(rep_dir.glob("pg-snapshot-end-*.txt"))
    if not files:
        return None
    text = files[-1].read_text()
    before = re.search(r"db_size_before_bytes=(\d+)", text)
    after = re.search(r"db_size_after_bytes=(\d+)", text)
    delta = re.search(r"db_size_delta_bytes=(-?\d+)", text)
    if not (before and after):
        return None
    b, a = int(before.group(1)), int(after.group(1))
    return {"before": b, "after": a, "delta": int(delta.group(1)) if delta else a - b}


def dict_table(d, key_header):
    if not d:
        return "_(no data)_"
    lines = [f"| {key_header} | Value |", "| --- | --- |"]
    for k, v in d.items():
        lines.append(f"| `{k}` | {v} |")
    return "\n".join(lines)


# ---- params (source of truth: k6's own summary.json params block) ----------

params_list = [r["summary"]["params"] for r in ok_reps]
params0 = params_list[0]
params_consistent = all(p == params0 for p in params_list)

# ---- manifest: row counts + lifecycle matrix --------------------------------

manifests = [r["manifest"] for r in ok_reps if r.get("manifest")]
manifest0 = manifests[0] if manifests else {}
manifests_consistent = all(m == manifest0 for m in manifests)

seed = manifest0.get("seed", "n/a")
history_days = manifest0.get("historyDays", "n/a")
manifest_counts_table = dict_table(manifest0.get("counts", {}), "Table/metric")
if not manifests_consistent:
    manifest_counts_table += (
        "\n\n**WARNING:** manifest counts differed across reps despite a fixed seed "
        f"(`{seed}`) — each rep should be byte-identical given deterministic seeding; "
        "investigate before trusting this header."
    )

lifecycle = manifest0.get("lifecycleStates", {})
lifecycle_lines = ["| Entity | State | Count |", "| --- | --- | --- |"]
for entity, states in lifecycle.items():
    for state, count in states.items():
        lifecycle_lines.append(f"| `{entity}` | `{state}` | {count} |")
lifecycle_state_matrix_table = "\n".join(lifecycle_lines) if lifecycle else "_(no data)_"

# ---- compression factor / wall-clock ----------------------------------------

capacity_duration_seconds = parse_duration_seconds(params0.get("capacityDuration"))
if capacity_duration_seconds:
    compression_factor = 86400 / capacity_duration_seconds
    compression_factor_line = (
        f"A nominal 24h day compressed into this run's `{params0.get('capacityDuration')}` "
        f"capacity phase is a **{compression_factor:.1f}x** wall-clock compression — shown for "
        "orientation only. There is no validated \"requests per user per real day\" baseline in "
        "this repo to compute a true production-pacing compression factor against, so treat this "
        "as a raw wall-clock ratio, not a validated load model."
    )
else:
    compression_factor_line = "_(capacityDuration unparseable — no compression factor computed)_"

wall_rows = ["| Rep | reset.sh | k6 (warmup+capacity) | full rep |", "| --- | --- | --- | --- |"]
for r in ok_reps:
    meta = r.get("meta", {})
    wall_rows.append(
        f"| {r['rep']} | {meta.get('resetWallClockSeconds', 'n/a')}s | "
        f"{meta.get('k6WallClockSeconds', 'n/a')}s | {meta.get('repWallClockSeconds', 'n/a')}s |"
    )
wall_clock_table = "\n".join(wall_rows)

# ---- run parameters table ----------------------------------------------------

params_rows = ["| Param | Value |", "| --- | --- |", f"| `REPS` | {REPS} |"]
for k in [
    "warmupVus", "warmupDuration", "warmupGracefulStop", "capacityVus", "capacityDuration",
    "capacityStartTime", "interPhaseDrainBufferSeconds", "capacityLoginJitterSecondsMax", "castSize",
]:
    params_rows.append(f"| `{k}` | {params0.get(k, 'n/a')} |")
params_table = "\n".join(params_rows)
if not params_consistent:
    params_table += (
        "\n\n**WARNING:** run params differed across reps within this one run-baseline.sh "
        "invocation (unexpected) — check each rep's own `summary.json` `params` block directly."
    )

# ---- persona coverage (#243 Task 9 MUST-FIX verification) ------------------

persona_order = list(ok_reps[0]["summary"]["capacity"]["byPersona"].keys())
persona_rows = [
    "| Rep | " + " | ".join(persona_order) + " | All 5 present? |",
    "| --- | " + " | ".join(["---"] * len(persona_order)) + " | --- |",
]
persona_coverage_ok = True
for r in ok_reps:
    by_persona = r["summary"]["capacity"]["byPersona"]
    cells, all_present = [], True
    for p in persona_order:
        trend = by_persona.get(p)
        present = bool(trend) and isinstance(trend.get("p50"), (int, float))
        cells.append("present" if present else "**MISSING**")
        all_present = all_present and present
    persona_coverage_ok = persona_coverage_ok and all_present
    persona_rows.append(f"| {r['rep']} | " + " | ".join(cells) + f" | {'yes' if all_present else 'NO'} |")
persona_coverage_table = "\n".join(persona_rows)
persona_coverage_warning = "" if persona_coverage_ok else (
    "\n**CRITICAL: at least one rep is missing capacity-phase requests for one or more "
    "personas.** This should not happen with the inter-phase drain gap in `baseline.js` — treat "
    "this run's persona-level breakdowns as unreliable and investigate before relying on it (see "
    "`baseline.js`'s file header, \"WHY THE DRAIN GAP IS THE FIX\").\n"
)

# ---- per-cast-user capacity coverage (PR #279 review) -----------------------
# Stricter than the role-level check above: proves every INDIVIDUAL cast
# user (not just each of the 5 role tags) was owned by exactly one capacity
# VU. Tolerates rep summaries captured before this field existed
# (`capacity.byCastUser` missing) by reporting "no data" for that rep rather
# than crashing — needed so `--render-only` can re-render a pre-PR-#279 run.

cast_user_rows = [
    "| Rep | cast users covered | missing | duplicated | OK? |",
    "| --- | --- | --- | --- | --- |",
]
cast_user_coverage_ok = True
any_cast_user_data = False
for r in ok_reps:
    by_cast_user = r["summary"]["capacity"].get("byCastUser")
    if not by_cast_user:
        cast_user_rows.append(f"| {r['rep']} | _(no data — pre-#279 run)_ | — | — | — |")
        continue
    any_cast_user_data = True
    total = len(by_cast_user)
    missing = [v["email"] for v in by_cast_user.values() if v.get("ownerCount", 0) == 0]
    duplicated = [v["email"] for v in by_cast_user.values() if v.get("ownerCount", 0) > 1]
    covered = total - len(missing)
    rep_ok = not missing and not duplicated
    cast_user_coverage_ok = cast_user_coverage_ok and rep_ok
    cast_user_rows.append(
        f"| {r['rep']} | {covered}/{total} | {', '.join(missing) or 'none'} | "
        f"{', '.join(duplicated) or 'none'} | {'yes' if rep_ok else '**NO**'} |"
    )
cast_user_coverage_table = "\n".join(cast_user_rows)
if not any_cast_user_data:
    cast_user_coverage_warning = (
        "\n_No rep in this run has `capacity.byCastUser` data — this run predates the PR #279 "
        "per-user coverage check (only role-level coverage above is available for it). Re-run "
        "with the current harness to get per-user coverage._\n"
    )
elif cast_user_coverage_ok:
    cast_user_coverage_warning = (
        "\nNo reps flagged — every cast user was owned by exactly one capacity VU, every rep.\n"
    )
else:
    cast_user_coverage_warning = (
        "\n**CRITICAL: at least one rep has a cast user missing or claimed by more than one VU "
        "in the capacity phase.** This should not happen with `CAPACITY_VUS == castSize` and the "
        "inter-phase drain gap in place — treat this run's per-user coverage (and by extension its "
        "per-persona/flow breakdowns) as unreliable and investigate before relying on it (see "
        "`baseline.js`'s file header, \"WHY THE DRAIN GAP IS THE FIX\").\n"
    )

# ---- overall / by-persona / by-flow latency SHAPE (relative only) -----------
# PR #279 review (Fix 1): no absolute ms anywhere below — p95/p99 are shown
# only as a ratio to that SAME rep's p50 (tail-heaviness shape), and each
# persona/flow's percentile only as a ratio to that same rep's OVERALL
# capacity percentile (2.2/2.3) — never a standalone millisecond figure.

overall_p50s = [r["summary"]["capacity"].get("httpReqDuration", {}).get("p50") for r in ok_reps]
overall_p95s = [r["summary"]["capacity"].get("httpReqDuration", {}).get("p95") for r in ok_reps]
overall_p99s = [r["summary"]["capacity"].get("httpReqDuration", {}).get("p99") for r in ok_reps]

p95_over_p50 = [ratio_of(overall_p95s[i], overall_p50s[i]) for i in range(len(ok_reps))]
p99_over_p50 = [ratio_of(overall_p99s[i], overall_p50s[i]) for i in range(len(ok_reps))]

overall_rows = ["| Rep | p95 / p50 (tail ratio) | p99 / p50 (tail ratio) |", "| --- | --- | --- |"]
for i, r in enumerate(ok_reps):
    overall_rows.append(f"| {r['rep']} | {fmt_ratio(p95_over_p50[i])} | {fmt_ratio(p99_over_p50[i])} |")
overall_rows.append(
    f"| **median** | **{fmt_ratio(median(p95_over_p50))}** | **{fmt_ratio(median(p99_over_p50))}** |"
)
overall_latency_table = "\n".join(overall_rows)

p95_med = median(overall_p95s)
p95_variance_lines = []
if p95_med:
    for i, r in enumerate(ok_reps):
        v = overall_p95s[i]
        if not isinstance(v, (int, float)):
            continue
        pct = (v - p95_med) / p95_med * 100
        flag = " — **>10% from median (observation, not a gate)**" if abs(pct) > 10 else ""
        p95_variance_lines.append(f"- rep {r['rep']}: {pct:+.1f}% vs median{flag}")
else:
    p95_variance_lines.append("_(not enough data to compute variance)_")
p95_variance_observation = "\n".join(p95_variance_lines)


def build_breakdown_table(order, dict_key, label):
    lines = [
        f"| {label} | rep | p50 vs overall | p95 vs overall | p99 vs overall |",
        "| --- | --- | --- | --- | --- |",
    ]
    for name in order:
        r50s, r95s, r99s = [], [], []
        for i, r in enumerate(ok_reps):
            trend = r["summary"]["capacity"][dict_key].get(name)
            p50 = trend.get("p50") if trend else None
            p95 = trend.get("p95") if trend else None
            p99 = trend.get("p99") if trend else None
            r50 = ratio_of(p50, overall_p50s[i])
            r95 = ratio_of(p95, overall_p95s[i])
            r99 = ratio_of(p99, overall_p99s[i])
            r50s.append(r50)
            r95s.append(r95)
            r99s.append(r99)
            lines.append(f"| {name} | {r['rep']} | {fmt_ratio(r50)} | {fmt_ratio(r95)} | {fmt_ratio(r99)} |")
        lines.append(
            f"| **{name} median** | — | **{fmt_ratio(median(r50s))}** | **{fmt_ratio(median(r95s))}** | "
            f"**{fmt_ratio(median(r99s))}** |"
        )
    return "\n".join(lines)


flow_order = list(ok_reps[0]["summary"]["capacity"]["byFlow"].keys())
by_persona_table = build_breakdown_table(persona_order, "byPersona", "Persona") + (
    "\n\n_Ratio = that persona's percentile ÷ the SAME rep's OVERALL capacity percentile (2.1) — "
    "1.00x means exactly average for this run, not a fixed target. No absolute ms is shown; see "
    "the banner at the top of this document._"
)
by_flow_table = build_breakdown_table(flow_order, "byFlow", "Flow") + (
    "\n\n_Ratio = that flow's percentile ÷ the SAME rep's OVERALL capacity percentile (2.1). No "
    "absolute ms is shown; see the banner at the top of this document._"
)

# ---- request-rate mix (relative only) ----------------------------------------
# PR #279 review (Fix 1): no absolute req/s or request-count figure below —
# only each rep's percentage deviation from THIS run's own median, as a
# cross-rep consistency check (same "shape, not magnitude" treatment as
# 2.1-2.3 above).

rps_values = [r["summary"]["capacity"].get("requestsPerSecond") for r in ok_reps]
req_count_values = [r["summary"]["totals"].get("capacityRequestCount") for r in ok_reps]
iter_values = [r["summary"]["capacity"].get("iterationCount") for r in ok_reps]

rps_deltas = pct_delta_vs_median(rps_values)
req_deltas = pct_delta_vs_median(req_count_values)
iter_deltas = pct_delta_vs_median(iter_values)

rr_rows = [
    "| Rep | req/s (Δ vs median) | capacity requests (Δ vs median) | iterations (Δ vs median) |",
    "| --- | --- | --- | --- |",
]
for i, r in enumerate(ok_reps):
    rr_rows.append(f"| {r['rep']} | {fmt_pct(rps_deltas[i])} | {fmt_pct(req_deltas[i])} | {fmt_pct(iter_deltas[i])} |")
rr_rows.append("| **median** | **0.0% (baseline)** | **0.0% (baseline)** | **0.0% (baseline)** |")
request_rate_table = "\n".join(rr_rows) + (
    "\n\n_Absolute req/s and request counts are intentionally not shown (see banner above) — only "
    "each rep's percentage deviation from this run's own median, as a cross-rep consistency check. "
    "Per-persona/flow request **counts** aren't captured by this harness at all — only latency "
    "shape per persona/flow (2.2/2.3). The persona **mix** is fixed by the seeded cast ratio (1 "
    "Owner / 1 Manager / 1 Sales / 3 Worker / 4 ReadOnly), not something that varies at runtime._"
)

# ---- correctness signals + flagged reps --------------------------------------

status_rows = [
    "| Rep | checks rate | unexpected_status | http_req_failed rate | k6 exit | flagged? |",
    "| --- | --- | --- | --- | --- | --- |",
]
flagged = []
for r in ok_reps:
    cap = r["summary"]["capacity"]
    checks_rate = cap.get("checksRate")
    unexpected = cap.get("unexpectedStatusCount")
    failed_rate = cap.get("httpReqFailedRate")
    k6exit = r.get("meta", {}).get("k6ExitCode")
    is_flagged = (
        (isinstance(checks_rate, (int, float)) and checks_rate < 1.0)
        or (unexpected not in (None, 0))
        or (k6exit not in (None, 0))
    )
    if is_flagged:
        flagged.append(r)
    status_rows.append(
        f"| {r['rep']} | {fmt_num(checks_rate, 4)} | {unexpected if unexpected is not None else 'n/a'} | "
        f"{fmt_num(failed_rate, 4)} | {k6exit if k6exit is not None else 'n/a'} | "
        f"{'**YES**' if is_flagged else 'no'} |"
    )
status_checks_table = "\n".join(status_rows)

flagged_lines = []
if not flagged and not missing_reps:
    flagged_lines.append(
        "No reps flagged — every rep's capacity phase had checks==100%, unexpected_status==0, "
        "and k6 exited 0."
    )
else:
    if flagged:
        flagged_lines.append("**Flagged reps (data still published above, never dropped):**")
        for r in flagged:
            cap = r["summary"]["capacity"]
            flagged_lines.append(
                f"- rep {r['rep']}: checksRate={cap.get('checksRate')}, "
                f"unexpectedStatusCount={cap.get('unexpectedStatusCount')}, "
                f"k6ExitCode={r.get('meta', {}).get('k6ExitCode')} — see `{r['dir'].name}/k6.log`."
            )
    if missing_reps:
        flagged_lines.append("**Reps with NO summary.json at all (k6 crashed before handleSummary ran):**")
        for r in missing_reps:
            flagged_lines.append(f"- rep {r['rep']}: see `{r['dir'].name}/k6.log` for the failure.")
flagged_reps_note = "\n".join(flagged_lines)

# ---- resource utilization (docker stats) -------------------------------------

per_rep_stats, containers_seen = {}, set()
for r in ok_reps:
    stats = read_docker_stats(r["dir"] / "docker-stats.csv")
    for container, vals in stats.items():
        containers_seen.add(container)
        per_rep_stats.setdefault(container, {})[r["rep"]] = {
            "cpuMedian": median(vals["cpu"]),
            "cpuMax": max(vals["cpu"]) if vals["cpu"] else None,
            "memMedian": median(vals["mem"]),
            "memMax": max(vals["mem"]) if vals["mem"] else None,
        }

resource_rows = [
    "| Container | rep | CPU% median | CPU% max | Mem median (MiB) | Mem max (MiB) |",
    "| --- | --- | --- | --- | --- | --- |",
]
for container in sorted(containers_seen):
    for rep_num in sorted(per_rep_stats[container].keys()):
        d = per_rep_stats[container][rep_num]
        resource_rows.append(
            f"| {container} | {rep_num} | {fmt_num(d['cpuMedian'], 1)} | {fmt_num(d['cpuMax'], 1)} | "
            f"{fmt_num(d['memMedian'], 0)} | {fmt_num(d['memMax'], 0)} |"
        )
    cpu_medians = [d["cpuMedian"] for d in per_rep_stats[container].values()]
    mem_medians = [d["memMedian"] for d in per_rep_stats[container].values()]
    resource_rows.append(
        f"| **{container} median-of-medians** | — | **{fmt_num(median(cpu_medians), 1)}** | — | "
        f"**{fmt_num(median(mem_medians), 0)}** | — |"
    )
resource_util_table = "\n".join(resource_rows) if containers_seen else "_(no docker-stats data collected)_"

# ---- DB growth ----------------------------------------------------------------

db_rows = ["| Rep | before | after | delta |", "| --- | --- | --- | --- |"]
deltas = []
for r in ok_reps:
    pg = parse_pg_end_file(r["dir"])
    if pg:
        deltas.append(pg["delta"])
        db_rows.append(
            f"| {r['rep']} | {pg['before'] / 1024 / 1024:.1f} MiB | {pg['after'] / 1024 / 1024:.1f} MiB | "
            f"{pg['delta'] / 1024 / 1024:+.1f} MiB |"
        )
    else:
        db_rows.append(f"| {r['rep']} | n/a | n/a | n/a |")
if deltas:
    db_rows.append(f"| **median delta** | — | — | **{median(deltas) / 1024 / 1024:+.1f} MiB** |")
db_growth_table = "\n".join(db_rows)

# ---- raw artifacts + dirty note -----------------------------------------------

rep_artifacts_note = "\n".join(
    f"- rep {r['rep']}{'  **(no summary.json — k6 crashed)**' if r['missingSummary'] else ''}: "
    f"`{r['dir'].relative_to(REPO_ROOT)}/`"
    for r in reps
)

dirty_note = (
    "| Working tree | **DIRTY** at run time (`git status --porcelain` non-empty) |" if GIT_DIRTY else ""
)

# ---- render -------------------------------------------------------------------

replacements = {
    "{{RUN_ID}}": RUN_ID,
    "{{GENERATED_AT_UTC}}": GENERATED_AT,
    "{{COMMIT_SHA}}": COMMIT_SHA,
    "{{COMMIT_SHA_SHORT}}": COMMIT_SHA_SHORT,
    "{{GIT_BRANCH}}": GIT_BRANCH,
    "{{DIRTY_NOTE}}": dirty_note,
    "{{REPS}}": str(REPS),
    "{{SEED}}": str(seed),
    "{{HISTORY_DAYS}}": str(history_days),
    "{{MANIFEST_ROW_COUNTS_TABLE}}": manifest_counts_table,
    "{{LIFECYCLE_STATE_MATRIX_TABLE}}": lifecycle_state_matrix_table,
    "{{COMPRESSION_FACTOR_LINE}}": compression_factor_line,
    "{{PARAMS_TABLE}}": params_table,
    "{{WALL_CLOCK_TABLE}}": wall_clock_table,
    "{{PERSONA_COVERAGE_TABLE}}": persona_coverage_table,
    "{{PERSONA_COVERAGE_WARNING}}": persona_coverage_warning,
    "{{CAST_USER_COVERAGE_TABLE}}": cast_user_coverage_table,
    "{{CAST_USER_COVERAGE_WARNING}}": cast_user_coverage_warning,
    "{{OVERALL_LATENCY_TABLE}}": overall_latency_table,
    "{{P95_VARIANCE_OBSERVATION}}": p95_variance_observation,
    "{{BY_PERSONA_TABLE}}": by_persona_table,
    "{{BY_FLOW_TABLE}}": by_flow_table,
    "{{REQUEST_RATE_TABLE}}": request_rate_table,
    "{{STATUS_CHECKS_TABLE}}": status_checks_table,
    "{{FLAGGED_REPS_NOTE}}": flagged_reps_note,
    "{{RESOURCE_UTIL_TABLE}}": resource_util_table,
    "{{DB_GROWTH_TABLE}}": db_growth_table,
    "{{REP_ARTIFACTS_NOTE}}": rep_artifacts_note,
}

out_text = TEMPLATE_PATH.read_text()
for token, value in replacements.items():
    out_text = out_text.replace(token, value)

leftover = re.findall(r"\{\{[A-Z0-9_]+\}\}", out_text)
if leftover:
    print(f"run-baseline: WARNING leftover template tokens not substituted: {leftover}", file=sys.stderr)

OUTPUT_MD_PATH.parent.mkdir(parents=True, exist_ok=True)
OUTPUT_MD_PATH.write_text(out_text)

aggregate = {
    "runId": RUN_ID,
    "generatedAt": GENERATED_AT,
    "commitSha": COMMIT_SHA,
    "gitBranch": GIT_BRANCH,
    "gitDirty": GIT_DIRTY,
    "repsRequested": REPS,
    "repsWithSummary": len(ok_reps),
    "personaCoverageOk": persona_coverage_ok,
    "castUserCoverageOk": cast_user_coverage_ok,
    "flaggedReps": [r["rep"] for r in flagged],
    "missingSummaryReps": [r["rep"] for r in missing_reps],
    "findingsDoc": str(OUTPUT_MD_PATH),
}
OUTPUT_JSON_PATH.write_text(json.dumps(aggregate, indent=2))

print(f"Findings doc:   {OUTPUT_MD_PATH}")
print(f"Aggregate JSON: {OUTPUT_JSON_PATH}")
print(f"Persona coverage OK (all 5 roles, every rep): {persona_coverage_ok}")
print(f"Cast-user coverage OK (every distinct user, every rep): {cast_user_coverage_ok}")
if flagged:
    print(f"FLAGGED reps (checks<100% or unexpected_status>0 or k6 exit!=0): {[r['rep'] for r in flagged]}")
if missing_reps:
    print(f"Reps with NO summary.json at all: {[r['rep'] for r in missing_reps]}")

sys.exit(0 if (persona_coverage_ok and cast_user_coverage_ok) else 2)
PY
agg_exit=$?
set -e

if [[ "$agg_exit" -ne 0 ]]; then
  overall_exit=1
fi

echo
echo "== run-baseline complete: RUN_ID=${RUN_ID} =="
echo "   per-rep raw output: ${RUN_DIR}/rep-<n>/"
echo "   aggregate JSON:     ${AGGREGATE_OUT}"
echo "   findings doc:       ${FINDINGS_OUT}"
if [[ "$overall_exit" -ne 0 ]]; then
  echo "   status: completed WITH WARNINGS — see flagged reps / persona coverage above. Data was still published for every rep." >&2
else
  echo "   status: clean — no flagged reps, persona coverage OK on every rep."
fi

exit "$overall_exit"
