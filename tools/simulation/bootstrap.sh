#!/usr/bin/env bash
#
# tools/simulation/bootstrap.sh — generate the #243 sim-harness secrets.
#
# Produces two git-ignored artifacts (see tools/simulation/.gitignore):
#   - tools/simulation/.env.sim   — everything docker-compose.sim.yml's `app`
#     service needs. #283: the default account/roles/egg grades ship as part
#     of the EF migrations (the serving container's normal migrate-on-startup
#     boot provisions them — no Seed:* config); reset.sh separately runs the
#     one-shot `bootstrap-admin` command (the Owner) and then `seed --profile
#     simulation` (SimulationDataSeeder, the fixture cast/history), both as
#     `docker compose run` against this same env file (#279).
#   - tools/simulation/.sim-cast.json — the k6/Playwright LOGIN SOURCE: the
#     Owner (reused seeded admin) + every deterministic cast member this
#     script's counts imply. This is NOT SimulationDataSeeder's own
#     completion manifest (that lands at tools/simulation/out/manifest.json,
#     written by the app itself via Simulation__CredentialOutputPath) — this
#     file is the credential list a test driver logs in with.
#
# Idempotent: exits early once .env.sim already exists. Pass --force to
# regenerate everything (new RSA keypair, new passwords) — any running sim
# stack then needs `reset.sh` to pick the new credentials up.
#
# Never touches the default docker compose project, deploy/.env, or any real
# secret — see reset.sh for the destructive half of this workflow and its own
# `cluckwork-sim` project guard.

set -euo pipefail

# PR #279 review: .env.sim/.sim-cast.json (and the transient *.pem below)
# hold the JWT private key and every generated password — restrict every
# file this script creates to owner-only from the moment it's created, not
# just after the fact. umask alone covers new files created by openssl/the
# heredocs below; the explicit chmod 0600 calls near the end are belt and
# suspenders for anything created before this line ran, or if the caller's
# environment already had a laxer umask baked into a wrapper.
umask 077

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$SCRIPT_DIR/.env.sim"
CAST_FILE="$SCRIPT_DIR/.sim-cast.json"

FORCE=0
for arg in "$@"; do
  case "$arg" in
    --force)
      FORCE=1
      ;;
    -h | --help)
      echo "Usage: $(basename "$0") [--force]"
      echo "  --force  Regenerate .env.sim/.sim-cast.json even if they already exist."
      exit 0
      ;;
    *)
      echo "Unknown argument: $arg (only --force is supported)" >&2
      exit 1
      ;;
  esac
done

if [[ -f "$ENV_FILE" && "$FORCE" -ne 1 ]]; then
  echo "tools/simulation/.env.sim already exists — leaving it in place."
  echo "Pass --force to regenerate (new keypair + new passwords; re-run reset.sh after)."
  exit 0
fi

command -v openssl >/dev/null 2>&1 || { echo "bootstrap.sh requires openssl on PATH." >&2; exit 1; }
command -v python3 >/dev/null 2>&1 || { echo "bootstrap.sh requires python3 on PATH." >&2; exit 1; }

# --- Cast shape ------------------------------------------------------------
# Mirrors SimulationOptions' own defaults (Managers=1, Sales=1, Workers=3,
# ReadOnly=4 — src/Cluckwork.Infrastructure/Identity/SimulationOptions.cs).
# Written explicitly into .env.sim below (Simulation__Managers etc.) so this
# script's cast list can never silently drift from what the app actually
# seeds — the counts here are the ONE source of truth for both files.
MANAGERS=1
SALES=1
WORKERS=3
READONLY=4
EMAIL_DOMAIN="sim.local"
SEED_ADMIN_EMAIL="admin@${EMAIL_DOMAIN}"

echo "== Generating #243 sim-harness secrets =="

# --- RSA keypair (Jwt__PublicKeyPem/PrivateKeyPem) -------------------------
# Written briefly to disk (needed for `openssl rsa -pubout`, which has no
# stdin-only mode for deriving the public key) then removed on exit — never
# left lying around, even though *.pem is also git/docker-ignored.
PRIVATE_KEY_FILE="$SCRIPT_DIR/.bootstrap-private.pem"
PUBLIC_KEY_FILE="$SCRIPT_DIR/.bootstrap-public.pem"
cleanup() { rm -f "$PRIVATE_KEY_FILE" "$PUBLIC_KEY_FILE"; }
trap cleanup EXIT

openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$PRIVATE_KEY_FILE" >/dev/null 2>&1
openssl rsa -pubout -in "$PRIVATE_KEY_FILE" -out "$PUBLIC_KEY_FILE" >/dev/null 2>&1
chmod 0600 "$PRIVATE_KEY_FILE" "$PUBLIC_KEY_FILE"

# Join PEM lines with a literal "\n" and no trailing escape — the exact
# format deploy/.env.example uses and PemKey.Normalize expects (env files
# can't hold real line breaks).
pem_to_escaped() {
  awk 'ORS="\\n"' "$1" | sed 's/\\n$//'
}
JWT_PUBLIC_PEM="$(pem_to_escaped "$PUBLIC_KEY_FILE")"
JWT_PRIVATE_PEM="$(pem_to_escaped "$PRIVATE_KEY_FILE")"
echo "RSA keypair generated (2048-bit, PKCS8)."

# --- Policy-compliant runtime passwords -------------------------------------
# Identity policy (Program.cs AddIdentityCore): RequiredLength=12 plus the
# framework defaults RequireUppercase/RequireLowercase/RequireDigit/
# RequireNonAlphanumeric all true. rand_chars avoids '$', backticks, and
# quote characters so the generated value is always safe to drop straight
# into a docker compose env file and a heredoc without further escaping.
rand_chars() {
  local pool="$1" count="$2" result=""
  while [[ ${#result} -lt "$count" ]]; do
    result+="$(head -c 64 /dev/urandom | LC_ALL=C tr -dc "$pool" | head -c "$((count - ${#result}))")"
  done
  echo "$result"
}

gen_password() {
  local pool_upper='ABCDEFGHJKLMNPQRSTUVWXYZ'
  local pool_lower='abcdefghijkmnpqrstuvwxyz'
  local pool_digit='23456789'
  # '-' is LAST on purpose: inside a tr SET, an unescaped '-' between two
  # other characters is a RANGE operator (e.g. earlier "()-_" silently
  # expanded to the whole )*+,-./0-9:;<=>?@A-Z[\]^_ range, including
  # backslash) — keeping it as the final character keeps it literal.
  local pool_symbol='!@%^*()_=+.,-'
  local pool_all="${pool_upper}${pool_lower}${pool_digit}${pool_symbol}"
  local raw=""
  raw+="$(rand_chars "$pool_upper" 3)"
  raw+="$(rand_chars "$pool_lower" 3)"
  raw+="$(rand_chars "$pool_digit" 3)"
  raw+="$(rand_chars "$pool_symbol" 3)"
  raw+="$(rand_chars "$pool_all" 8)"
  # Shuffle so the character classes aren't positionally predictable.
  echo "$raw" | fold -w1 | shuf | tr -d '\n'
}

# The DB password rides inside a key-value Npgsql connection string —
# compose interpolates Password=${POSTGRES_PASSWORD} verbatim — where '='
# and ';' are structural: an unquoted value containing '=' fails
# NpgsqlConnectionStringBuilder at boot with "Format of the initialization
# string does not conform to specification" (CI hit this on an unlucky
# draw after five green runs; pool_symbol contains '='). Identity's
# complexity policy applies to Identity USERS, not the Postgres role, so
# this password is alphanumeric-only — entropy comes from length.
gen_db_password() {
  rand_chars "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789" 32
}

SEED_ADMIN_PASSWORD="$(gen_password)"
SIM_CAST_PASSWORD="$(gen_password)"
POSTGRES_PASSWORD="$(gen_db_password)"
echo "Passwords generated (Owner, shared cast, sim Postgres)."

POSTGRES_DB="cluckwork_sim"
POSTGRES_USER="cluckwork_sim"

# --- Write .env.sim ----------------------------------------------------
# NOTE: quoting matches deploy/.env.example's Jwt__*Pem convention exactly —
# docker compose's env-file parser strips the wrapping double quotes, so the
# container sees the raw \n-escaped PEM text PemKey.Normalize expects.
cat >"$ENV_FILE" <<EOF
# Generated by tools/simulation/bootstrap.sh — DO NOT COMMIT (git-ignored).
# Regenerate: bash tools/simulation/bootstrap.sh --force
# Apply:      bash tools/simulation/reset.sh
#
# Deviations from deploy/.env.example (all sanctioned — see README.md):
#   - all three rate-limit buckets below are raised far past production
#     values so a load test isn't throttled as if it were an attack.
#   - Otlp__Endpoint points at the sim-only otel-collector service (#243
#     Task 8, tools/simulation/otel/collector.yaml) — never a developer's
#     real deploy/.env collector endpoint.
#   - AllowedHosts is a concrete placeholder host (cluckwork-sim.local), not
#     "*": there is no traefik/public hostname in front of the sim stack, but
#     the serving container runs Production config and #319 fails that boot on
#     a missing/blank/wildcard value. Loopback is force-added by
#     AddCluckworkEdgeSecurity, so the stack stays reachable on 127.0.0.1.

# --- Sim compose project safety (see reset.sh) ------------------------------
COMPOSE_PROJECT_NAME=cluckwork-sim

# --- App runtime -------------------------------------------------------
ASPNETCORE_URLS=http://+:8080
Database__Provider=Postgres
Database__MigrateOnStartup=true

# --- Sim Postgres (isolated named volume under cluckwork-sim only) --------
POSTGRES_DB=${POSTGRES_DB}
POSTGRES_USER=${POSTGRES_USER}
POSTGRES_PASSWORD=${POSTGRES_PASSWORD}

# --- JWT signing (RSA keypair generated above) ------------------------------
Jwt__Issuer=cluckwork
Jwt__Audience=cluckwork-api
Jwt__PublicKeyPem="${JWT_PUBLIC_PEM}"
Jwt__PrivateKeyPem="${JWT_PRIVATE_PEM}"

# --- First-run admin (#283) — SCRIPT-LEVEL values, not app config (no
# double-underscore key, so docker-compose's env-file parser does NOT expose
# these to the app as ASP.NET config; the app never reads a "seed" credential
# from anywhere). reset.sh reads these back to call bootstrap-admin, then
# (NOTE: no backticks and no dollar-parens anywhere in this heredoc — it is
# UNQUOTED, so such a phrase is COMMAND SUBSTITUTION, not prose. The word
# bootstrap-admin was backticked here and actually tried to EXECUTE, printing
# "command not found" on every bootstrap run and emitting a mangled comment.)
# rotates the printed one-time password to SIM_ADMIN_PASSWORD via the real
# login+change-password API — every other script/persona in this harness
# keeps a stable, known Owner credential exactly as before #283. ---
SIM_ADMIN_EMAIL=${SEED_ADMIN_EMAIL}
SIM_ADMIN_PASSWORD=${SEED_ADMIN_PASSWORD}

# --- #243 simulation cast/fixture (SimulationOptions) -----------------
Simulation__CastPassword=${SIM_CAST_PASSWORD}
Simulation__HistoryDays=90
Simulation__TimeZoneId=America/Chicago
Simulation__EmailDomain=${EMAIL_DOMAIN}
Simulation__Managers=${MANAGERS}
Simulation__Sales=${SALES}
Simulation__Workers=${WORKERS}
Simulation__ReadOnly=${READONLY}
Simulation__Seed=243
Simulation__CredentialOutputPath=/app/sim-cast/manifest.json

# --- Rate limiting (#143) — all three buckets raised so k6 traffic never
# looks like a credential-spraying/log-flooding attack. Window seconds are
# unchanged from production; only PermitLimit is raised. ---
RateLimiting__Login__PermitLimit=1000000
RateLimiting__Login__WindowSeconds=900
RateLimiting__Refresh__PermitLimit=1000000
RateLimiting__Refresh__WindowSeconds=900
RateLimiting__ClientErrors__PermitLimit=1000000
RateLimiting__ClientErrors__WindowSeconds=300

# --- Host/proxy interpolation vars (parity with deploy/.env.example) ---
#
# AllowedHosts is a CONCRETE host, not "*". The serving container runs
# Production config, and #319 fails a Production boot when AllowedHosts is
# missing, blank, or wildcard — so a "*" here does not merely weaken Host
# filtering, it stops the stack from booting at all. Loopback
# (localhost/127.0.0.1/[::1]) is force-added by AddCluckworkEdgeSecurity
# whenever the list is not wildcard, so k6 and the browser still reach the
# app on http://127.0.0.1:8081/ without listing it here.
#
# Written as a LITERAL, deliberately: this heredoc is unquoted (line 154), so
# a \${CLUCKWORK_HOST} here would expand at generation time against the
# generating shell — where it is unset — and emit a BLANK AllowedHosts, which
# fails the #319 guard exactly as the wildcard does. Keep the two in sync by
# hand; they are two lines apart.
AllowedHosts=cluckwork-sim.local
CLUCKWORK_HOST=cluckwork-sim.local
TRUSTED_PROXY_CIDR=127.0.0.1/32

# --- Telemetry: local OTLP sink (#243 Task 8) — the otel-collector service
# in docker-compose.sim.yml, sim-only. Never a developer's real deploy/.env
# OTLP endpoint. ---
Otlp__Endpoint=http://otel-collector:4317
Otlp__Protocol=grpc
# The serving app runs Production config, which requires an https collector
# (#316). This stack's collector is a sidecar on the stack's own private
# compose network, so the traffic never leaves it — acknowledge that plaintext
# explicitly, exactly as the co-located Postgres does with
# Database__AllowInsecureConnection. A real deploy sets neither.
Otlp__AllowInsecureEndpoint=true
EOF
chmod 0600 "$ENV_FILE"
echo "Wrote $(basename "$ENV_FILE")."

# --- Write .sim-cast.json (the k6/Playwright login source) -----------------
MANAGERS="$MANAGERS" SALES="$SALES" WORKERS="$WORKERS" READONLY="$READONLY" \
  EMAIL_DOMAIN="$EMAIL_DOMAIN" SEED_ADMIN_EMAIL="$SEED_ADMIN_EMAIL" \
  SEED_ADMIN_PASSWORD="$SEED_ADMIN_PASSWORD" SIM_CAST_PASSWORD="$SIM_CAST_PASSWORD" \
  CAST_FILE="$CAST_FILE" \
  python3 - <<'PY'
import datetime
import json
import os

managers = int(os.environ["MANAGERS"])
sales = int(os.environ["SALES"])
workers = int(os.environ["WORKERS"])
readonly = int(os.environ["READONLY"])
domain = os.environ["EMAIL_DOMAIN"]
admin_email = os.environ["SEED_ADMIN_EMAIL"]
admin_password = os.environ["SEED_ADMIN_PASSWORD"]
cast_password = os.environ["SIM_CAST_PASSWORD"]
out_path = os.environ["CAST_FILE"]

cast = []
for i in range(1, managers + 1):
    cast.append({"email": f"sim-manager-{i}@{domain}", "password": cast_password, "role": "Manager"})
for i in range(1, sales + 1):
    cast.append({"email": f"sim-sales-{i}@{domain}", "password": cast_password, "role": "Sales"})
for i in range(1, workers + 1):
    cast.append({"email": f"sim-worker-{i}@{domain}", "password": cast_password, "role": "Worker"})
for i in range(1, readonly + 1):
    cast.append({"email": f"sim-readonly-{i}@{domain}", "password": cast_password, "role": "ReadOnly"})

doc = {
    "generatedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "emailDomain": domain,
    "notes": [
        "Login source for k6/Playwright — NOT SimulationDataSeeder's own row-count "
        "completion manifest (that's tools/simulation/out/manifest.json, written by the "
        "app via Simulation__CredentialOutputPath).",
        "Worker entries carry no Identity role row in the app (CreateUserValidator.WorkerRole "
        "maps to a null role) — 'role': 'Worker' here is descriptive only.",
    ],
    "owner": {"email": admin_email, "password": admin_password, "role": "Owner"},
    "cast": cast,
}

with open(out_path, "w") as f:
    json.dump(doc, f, indent=2)
    f.write("\n")
PY
chmod 0600 "$CAST_FILE"
echo "Wrote $(basename "$CAST_FILE") ($((1 + MANAGERS + SALES + WORKERS + READONLY)) users: 1 Owner + ${MANAGERS} Manager + ${SALES} Sales + ${WORKERS} Worker + ${READONLY} ReadOnly)."

echo "== Done. Both files are git-ignored (tools/simulation/.gitignore) — never commit them. =="
echo "Next: bash tools/simulation/reset.sh"
