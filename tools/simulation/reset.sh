#!/usr/bin/env bash
#
# tools/simulation/reset.sh — destroy and rebuild the #243 sim stack from
# scratch, then verify it booted, migrated, seeded, and is safely reachable.
#
# #283: the default account/roles/egg grades are static reference data baked
# into the EF migrations themselves — the serving `app` container's own boot
# (migrate-on-startup, unchanged) already provisions them, no Seed:* config
# involved. There is still no admin user after that (no credential is ever
# baked in), so once the container is healthy this script runs the one-shot
# `bootstrap-admin` command (always available in Production, same posture as
# `recover-admin` — no environment override needed), captures the printed
# temporary password, and immediately rotates it via the real
# login+change-password API calls to the STABLE password bootstrap.sh already
# generated into .sim-cast.json — so every other script/k6 persona in this
# harness keeps reading a known, unchanging Owner credential exactly as
# before, and the forced-first-change gate (#283's MustChangePassword) is
# cleared before anything else runs.
#
# #279: simulation FIXTURE seeding is a separate, later one-shot step — once
# the app is up and the admin is provisioned, this script runs `seed
# --profile simulation` as an explicit ONE-SHOT `docker compose run` against
# a non-Production environment (the Program.cs prod-guard refuses the command
# in Production) — same command an operator would type by hand, mirroring
# `seed --profile demo` (#280).
#
# HARD SAFETY RULE: every docker command below runs under the dedicated
# `cluckwork-sim` compose project. Before anything destructive, the project
# name that will actually be used is asserted to equal `cluckwork-sim` and
# the script ABORTS otherwise — the real dev stack
# (deploy/docker-compose.yml) owns a named Postgres volume under the DEFAULT
# compose project, and a `down -v` under any other project risks deleting
# the wrong volume, including the developer's real local database. Never run
# a bare `docker compose down -v` against this stack.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$SCRIPT_DIR/.env.sim"
COMPOSE_FILE="$SCRIPT_DIR/docker-compose.sim.yml"
OUT_DIR="$SCRIPT_DIR/out"
MANIFEST_FILE="$OUT_DIR/manifest.json"
APP_PORT=8081
READY_TIMEOUT_SECONDS=300

if [[ ! -f "$ENV_FILE" ]]; then
  echo "tools/simulation/.env.sim not found — run bootstrap.sh first." >&2
  exit 1
fi

# --- HARD SAFETY GATE (must run before ANY docker command) -----------------
# .env.sim itself sets COMPOSE_PROJECT_NAME=cluckwork-sim, but an operator's
# shell can still override it — so this checks the value that will actually
# be used (the shell env wins over anything in the --env-file), not just
# what the file says.
COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-cluckwork-sim}"
if [[ "$COMPOSE_PROJECT_NAME" != "cluckwork-sim" ]]; then
  echo "ABORT: resolved compose project is '${COMPOSE_PROJECT_NAME}', not 'cluckwork-sim'." >&2
  echo "Refusing to continue — this script's 'down -v' must never run against any" >&2
  echo "project other than the dedicated cluckwork-sim one. The real dev stack" >&2
  echo "(deploy/docker-compose.yml) owns a named Postgres volume under the DEFAULT" >&2
  echo "project; running this under the wrong project risks deleting the wrong" >&2
  echo "volume. Do not override COMPOSE_PROJECT_NAME for this script." >&2
  exit 1
fi
echo "Compose project: ${COMPOSE_PROJECT_NAME} (safety gate passed)."

compose() {
  docker compose -p "$COMPOSE_PROJECT_NAME" --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
}

read_env_value() {
  local key="$1"
  grep -E "^${key}=" "$ENV_FILE" | tail -n1 | cut -d'=' -f2-
}

echo "== Sim reset: project ${COMPOSE_PROJECT_NAME} =="

# Self-check FIRST, before the destructive `down -v` and the image rebuild
# below (#370). Every one of the four breakages that left this harness unable
# to boot merged main was a config or logic defect visible without starting
# anything — so catching them here costs ~0.1s and saves wiping a volume and
# rebuilding an image only to fail five minutes later at /health/ready, which
# is exactly how they were actually found.
#
# Resolved from THIS script's own location, never from an ambient SIM_DIR: an
# exported SIM_DIR pointing at another checkout would verify that harness and
# then wipe this one (PR #371 review). Same class of ambient-override bug the
# verifier itself was just fixed for — reintroduced one line into the caller.
bash "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/verify-harness.sh"

echo "-- down -v (cluckwork-sim volumes only) --"
compose down -v --remove-orphans

# The app container bind-mounts this dir (./out:/app/sim-cast) and writes
# manifest.json into it as root. Docker auto-creates a bind-mount target
# that doesn't already exist on the host, and does so as root — which then
# blocks any HOST-side process (canary.spec.ts's `mkdir out/canary-vitals`,
# run outside Docker) from creating anything under it. Creating it here,
# as this script's own (non-root) user, before compose ever touches it,
# means Docker reuses the existing directory instead of auto-vivifying a
# root-owned one — the container still writes manifest.json as root, but
# the directory itself stays owned by whoever ran this script.
mkdir -p "$OUT_DIR"
# codex review, PR #430: `mkdir -p` alone is a no-op on a directory that
# ALREADY exists, regardless of who owns it — so a dev box that hit this
# bug before this fix landed (./out auto-vivified root-owned by an earlier
# run) would stay broken forever, silently. Repair that case too, but only
# when actually needed: `-w` gates the Docker round-trip to the (rare)
# broken case instead of running it on every reset. That gate also matters
# for a second reason (codex review, PR #430 round 2): under rootless
# Docker / userns-remap, the container's UID 0 doing this chown does not
# map straight to the host UID we pass in — it can fail, or worse, leave
# ./out owned by some remapped/subordinate UID instead of actually fixing
# it. Skipping the helper whenever ./out is already host-writable keeps
# that failure mode out of the common path entirely. Residual: if you ARE
# on rootless/userns-remapped Docker and ./out genuinely is broken, this
# repair may not produce a correctly host-owned directory — remove
# tools/simulation/out by hand (so mkdir -p above recreates it fresh, as
# your own user) rather than relying on this chown in that setup.
if [[ ! -w "$OUT_DIR" ]]; then
  echo "-- ./out is not writable by $(id -un) — repairing ownership via Docker --"
  docker run --rm -v "$OUT_DIR:/out" alpine chown -R "$(id -u):$(id -g)" /out
fi

echo "-- config -q (validate before build) --"
compose config -q

echo "-- up -d --build --"
compose up -d --build

# --- Wait for /health/ready (covers boot + migration + the base seed) -----
echo "-- waiting up to ${READY_TIMEOUT_SECONDS}s for /health/ready --"
deadline=$(($(date +%s) + READY_TIMEOUT_SECONDS))
until curl -fsS -o /dev/null "http://127.0.0.1:${APP_PORT}/health/ready"; do
  if (( $(date +%s) >= deadline )); then
    echo "TIMEOUT waiting for /health/ready after ${READY_TIMEOUT_SECONDS}s." >&2
    compose logs app --tail=200 >&2 || true
    exit 1
  fi
  sleep 3
done
echo "/health/ready -> 200."

# --- Preflight 1: plain HTTP root is 200, not a 307 HTTPS redirect ---------
echo "-- preflight: GET / is 200 (no HTTPS redirect on plain HTTP) --"
root_status="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${APP_PORT}/")"
if [[ "$root_status" != "200" ]]; then
  echo "FAILED: GET / returned ${root_status} (expected 200 — a 307 would mean an" >&2
  echo "unwanted HTTPS redirect on plain HTTP)." >&2
  exit 1
fi
echo "GET / -> 200."

# --- Preflight 2: pg_stat_statements is preloaded and queryable ------------
echo "-- preflight: pg_stat_statements --"
PGDB="$(read_env_value POSTGRES_DB)"
PGUSER="$(read_env_value POSTGRES_USER)"
compose exec -T db psql -U "$PGUSER" -d "$PGDB" -v ON_ERROR_STOP=1 \
  -c "CREATE EXTENSION IF NOT EXISTS pg_stat_statements;" \
  -c "SELECT count(*) FROM pg_stat_statements;" >/dev/null
echo "pg_stat_statements OK."

# --- First-run admin (#283): bootstrap-admin, then rotate off the printed --
# one-time password to the STABLE credential .sim-cast.json already carries.
# Always a fresh create here — reset.sh just did `down -v`, so the account
# never has an Owner yet; the "already provisioned" no-op branch never fires
# in this script's flow.
echo "-- bootstrap-admin (one-shot, first-run Owner) --"
SEED_ADMIN_EMAIL="$(read_env_value SIM_ADMIN_EMAIL)"
SEED_ADMIN_PASSWORD="$(read_env_value SIM_ADMIN_PASSWORD)"
BOOTSTRAP_OUTPUT="$(compose run --rm app bootstrap-admin --email "$SEED_ADMIN_EMAIL")"
# REDACTED on the way to the terminal. bootstrap-admin prints
# "Temporary password: <value>" on stdout by design (#283 — stdout only, never
# the logger), and this script needs that value; it does NOT need to re-print it.
# Echoing it raw put a live credential into every CI log of the E2E workflow,
# unmasked and retained (PR #392 review). The value is still captured below and
# rotated off seconds later — but a log is forever and a rotation is not
# retroactive.
# Matched ANYWHERE on the line, not anchored to the start: a log-level prefix or
# any indentation added to bootstrap-admin's output would slip past `^` and leak
# the value this redaction exists to hide.
printf '%s\n' "$BOOTSTRAP_OUTPUT" | sed 's/Temporary password: .*/Temporary password: <redacted>/'
TEMP_PASSWORD="$(printf '%s\n' "$BOOTSTRAP_OUTPUT" | sed -n 's/^Temporary password: //p')"
if [[ -z "$TEMP_PASSWORD" ]]; then
  echo "FAILED: could not find a 'Temporary password: ' line in bootstrap-admin's output." >&2
  exit 1
fi
echo "bootstrap-admin -> Owner created; rotating off the printed temporary password."

SEED_ADMIN_EMAIL="$SEED_ADMIN_EMAIL" TEMP_PASSWORD="$TEMP_PASSWORD" \
  SEED_ADMIN_PASSWORD="$SEED_ADMIN_PASSWORD" APP_PORT="$APP_PORT" python3 - <<'PY'
import json
import os
import sys
import urllib.request
import uuid

base = f"http://127.0.0.1:{os.environ['APP_PORT']}/api/v1/auth"
email = os.environ["SEED_ADMIN_EMAIL"]
temp_password = os.environ["TEMP_PASSWORD"]
final_password = os.environ["SEED_ADMIN_PASSWORD"]


def post(path, body, token=None, idempotency_key=None):
    req = urllib.request.Request(
        f"{base}{path}",
        data=json.dumps(body).encode(),
        method="POST",
        headers={"Content-Type": "application/json"},
    )
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    if idempotency_key:
        req.add_header("Idempotency-Key", idempotency_key)
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read())
    except urllib.error.HTTPError as exc:
        print(f"FAILED: POST {path} -> {exc.code}: {exc.read().decode()}", file=sys.stderr)
        sys.exit(1)


# The printed one-time password IS a real login (bootstrap-admin logs the
# account in with MustChangePassword=true, not merely creates it) — proving
# that also proves the temp password actually reached the account.
login = post("/login", {"email": email, "password": temp_password})
# Clears MustChangePassword server-side and rotates every session — the SAME
# endpoint the SPA's first-login "Set your password" screen uses.
post(
    "/change-password",
    {"currentPassword": temp_password, "newPassword": final_password},
    token=login["accessToken"],
    idempotency_key=str(uuid.uuid4()),
)
print(f"Owner {email} rotated onto the stable .sim-cast.json password.")
PY

# --- Simulation seed: explicit one-shot command (#279) ---------------------
# The serving `app` container (started above) never boot-seeds simulation
# data and stays Production throughout. `seed --profile simulation` runs
# here as a SEPARATE, short-lived container via `compose run --rm` — it
# inherits the `app` service's image, environment, and the `./out:/app/
# sim-cast` volume (so the manifest lands on the host below), but with
# ASPNETCORE_ENVIRONMENT overridden to Development just for this one-shot
# process: Program.cs's prod guard refuses `seed --profile simulation` in
# Production (verified separately — see README), so Production must never
# be the environment this command runs under.
#
# --user 0: the image runs as the non-root `app` user (uid 1654, #267 image
# hardening) so the PRODUCTION serving container drops root. But this throwaway
# one-shot must write its completion manifest to the ./out bind mount, which
# Docker creates root-owned — so uid 1654 gets EACCES and the manifest preflight
# below fails. Overriding just this short-lived seed container back to root
# restores the pre-#267 write (./out was always root-owned; the serving `app`
# above stays non-root). Keep --user 0 here or reset breaks at the manifest step.
echo "-- seed --profile simulation (one-shot, non-Production, root for ./out write) --"
if ! compose run --rm --user 0 -e ASPNETCORE_ENVIRONMENT=Development app \
    seed --profile simulation; then
  echo "FAILED: 'seed --profile simulation' exited non-zero." >&2
  exit 1
fi
echo "seed --profile simulation -> exit 0."

# --- Preflight 3: seeder manifest is complete with the expected user count -
echo "-- preflight: sim manifest (${MANIFEST_FILE}) --"
if [[ ! -f "$MANIFEST_FILE" ]]; then
  echo "FAILED: ${MANIFEST_FILE} does not exist (SimulationDataSeeder should have" >&2
  echo "written it to Simulation__CredentialOutputPath, mounted at ./out on the host)." >&2
  exit 1
fi

EXPECTED_USERS=$(( \
  1 \
  + $(read_env_value Simulation__Managers) \
  + $(read_env_value Simulation__Sales) \
  + $(read_env_value Simulation__Workers) \
  + $(read_env_value Simulation__ReadOnly) \
))

MANIFEST_FILE="$MANIFEST_FILE" EXPECTED_USERS="$EXPECTED_USERS" python3 - <<'PY'
import json
import os
import sys

path = os.environ["MANIFEST_FILE"]
expected_users = int(os.environ["EXPECTED_USERS"])

with open(path) as f:
    manifest = json.load(f)

if manifest.get("complete") is not True:
    print(f"FAILED: manifest 'complete' is {manifest.get('complete')!r}, expected True.", file=sys.stderr)
    sys.exit(1)

actual_users = manifest.get("counts", {}).get("usersTotal")
if actual_users != expected_users:
    print(
        f"FAILED: manifest counts.usersTotal is {actual_users!r}, expected {expected_users}.",
        file=sys.stderr,
    )
    sys.exit(1)

print(f"manifest OK: complete=true, counts.usersTotal={actual_users}.")
PY

echo "== Sim reset complete: cluckwork-sim is up, migrated, seeded, and verified. =="
echo "App:      http://127.0.0.1:${APP_PORT}/"
echo "Manifest: ${MANIFEST_FILE}"
echo "Cast:     $(dirname "$ENV_FILE")/.sim-cast.json"
