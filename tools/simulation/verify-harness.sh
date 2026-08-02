#!/usr/bin/env bash
#
# tools/simulation/verify-harness.sh — cheap self-check for the #243 harness.
#
# DELIBERATELY LOCAL, not a CI job (owner call, 2026-08-02): this harness is
# dev tooling that a human runs on demand, and a whole GitHub job per push is
# out of proportion to that. So the checks live here and run at the only moment
# they matter — `reset.sh` calls this before it spends five minutes building an
# image and booting a stack that a one-line config drift would fail anyway.
#
# Run it directly any time:  bash tools/simulation/verify-harness.sh
#
# WHY IT EXISTS. tools/simulation/ is the one part of this repo nothing
# automated ever executes, and it rotted exactly as you would expect: by
# 2026-08 it could not boot merged main at all (#370). Four breakages had piled
# up, three of them because an app-side Production boot guard landed and the
# harness config was never updated to satisfy it. Nothing reported that,
# because the only thing that runs the harness is a person typing reset.sh.
#
# Seconds to run. No image build, no stack boot, no Docker daemon needed except
# for the compose-config parse.

set -euo pipefail

SIM_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$SIM_DIR/.env.sim"
COMPOSE_FILE="$SIM_DIR/docker-compose.sim.yml"
fail=0

note() { printf '  %s\n' "$*"; }
bad()  { printf '  FAIL: %s\n' "$*" >&2; fail=1; }

echo "== #243 harness self-check =="

# --- 1. Pure logic -------------------------------------------------------
#
# The k6 date helpers, unit-tested with an INJECTED clock. This is the one
# check that cannot be replaced by "just run the harness": the report-window
# bug it pins fails only while UTC and the farm's date disagree, so a run at
# the wrong hour is green with the defect fully present (both runs recorded in
# findings/ were exactly that).
if command -v node >/dev/null 2>&1; then
  if node --test "$SIM_DIR/k6/dates.test.mjs" >/dev/null 2>&1; then
    note "k6 date helpers OK (report window never ends in the farm's future)"
  else
    bad "k6 date-helper tests failed — run: node --test tools/simulation/k6/dates.test.mjs"
  fi
else
  note "SKIP: node not on PATH, cannot run the date-helper tests"
fi

# --- 2. Generated env ----------------------------------------------------
if [[ ! -f "$ENV_FILE" ]]; then
  note "SKIP: .env.sim not generated yet (run bootstrap.sh) — config checks skipped"
else
  get() { grep -E "^$1=" "$ENV_FILE" | head -1 | cut -d= -f2-; }

  # #319 — a missing, blank or wildcard AllowedHosts FAILS the Production boot.
  # Checked as the app checks it (non-empty, no '*'), not against a literal
  # hostname, so renaming the placeholder is fine and dropping it is not.
  hosts="$(get AllowedHosts || true)"
  case "${hosts:-}" in
    "")    bad "AllowedHosts is unset/blank — #319 fails the Production boot" ;;
    *"*"*) bad "AllowedHosts contains a wildcard ('$hosts') — #319 fails the Production boot" ;;
    *)     note "AllowedHosts OK ($hosts)" ;;
  esac

  # #316 — the ENDPOINT and the FLAG are ONE guard, so check them as a pair.
  # OtlpOptions.ResolveSignalEndpoint throws in Production when the scheme is
  # not https AND AllowInsecureEndpoint is not true; a blank value additionally
  # fails to bind to Boolean before that. Presence alone would pass a plaintext
  # endpoint paired with `false`, which is a failed boot (PR #371 review).
  endpoint="$(get Otlp__Endpoint || true)"
  insecure="$(printf '%s' "$(get Otlp__AllowInsecureEndpoint || true)" \
    | tr '[:upper:]' '[:lower:]' | tr -d '[:space:]')"
  case "$insecure" in
    true|false) ;;
    "") bad "Otlp__AllowInsecureEndpoint is unset — Production boot fails binding '' to Boolean" ;;
    *)  bad "Otlp__AllowInsecureEndpoint='$insecure' does not bind to Boolean — Production boot fails" ;;
  esac
  case "$endpoint" in
    https://*) note "Otlp endpoint OK (https)" ;;
    "")        bad "Otlp__Endpoint is unset" ;;
    *)         if [[ "$insecure" == "true" ]]; then
                 note "Otlp endpoint OK (plaintext, explicitly acknowledged)"
               else
                 bad "Otlp__Endpoint '$endpoint' is not https and Otlp__AllowInsecureEndpoint is '${insecure:-unset}' — #316 fails the Production boot"
               fi ;;
  esac

  # Retired keys. .env.sim outlives the schema that generated it, and a stale
  # one silently carries pre-#283 runtime-seeder config while MISSING the vars
  # reset.sh now needs — which is the state this harness was actually found in.
  for retired in Seed__AdminEmail Seed__AdminPassword Seed__Demo Seed__Enabled; do
    if grep -qE "^$retired=" "$ENV_FILE"; then
      bad "$retired is retired (#283) but present — regenerate: bash tools/simulation/bootstrap.sh --force"
    fi
  done
  for required in SIM_ADMIN_EMAIL SIM_ADMIN_PASSWORD; do
    grep -qE "^$required=" "$ENV_FILE" \
      || bad "$required missing — regenerate: bash tools/simulation/bootstrap.sh --force"
  done
fi

# --- 3. Compose ----------------------------------------------------------

# #261/#262 — the co-located plaintext Postgres needs the explicit opt-out, or
# the TLS floor rejects sslmode=Prefer (Npgsql's default when unset).
if grep -qE '^\s*Database__AllowInsecureConnection:\s*"true"' "$COMPOSE_FILE"; then
  note "Database__AllowInsecureConnection OK"
else
  bad "compose does not set Database__AllowInsecureConnection — #261/#262 fails the Production boot against the plaintext sidecar"
fi

# Every ${VAR} the compose file references must actually resolve. This is the
# drift check: when bootstrap.sh stops emitting a var the compose file still
# uses, Compose substitutes a BLANK and only WARNS — which is precisely how an
# empty Otlp__AllowInsecureEndpoint reached the app and crashed the boot.
if [[ -f "$ENV_FILE" ]] && command -v docker >/dev/null 2>&1; then
  compose_err="$(mktemp)"
  if docker compose -p cluckwork-sim --env-file "$ENV_FILE" -f "$COMPOSE_FILE" \
       config >/dev/null 2>"$compose_err"; then
    if grep -qi 'variable is not set' "$compose_err"; then
      bad "compose references variables the harness no longer generates:"
      grep -i 'variable is not set' "$compose_err" >&2 || true
    else
      note "every compose variable resolves"
    fi
  else
    bad "docker compose config failed:"
    cat "$compose_err" >&2
  fi
  rm -f "$compose_err"
fi

if (( fail )); then
  echo "== harness self-check FAILED — fix the above before booting the stack ==" >&2
  exit 1
fi
echo "== harness self-check OK =="
