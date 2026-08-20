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
# because every path into this harness is human-started: reset.sh directly, or
# run-baseline.sh, which calls reset.sh once per rep. No schedule, no pipeline.
# (Both therefore run this check, since reset.sh invokes it.)
#
# ================== READ THIS BEFORE ADDING A CHECK ==================
#
# This script GATES A DESTRUCTIVE OPERATION. reset.sh runs it and then
# `down -v`. So a false GREEN here is worse than no check at all: it wipes a
# volume and rebuilds an image on the strength of a guarantee that was never
# actually verified.
#
# The first version of this script had four of them (PR #371 review), all the
# same mistake — asserting a PROXY for the guarantee instead of the guarantee:
#
#   * it read `.env.sim`, but Compose gives the AMBIENT SHELL precedence over
#     `--env-file`. `AllowedHosts='*' bash verify-harness.sh` printed
#     "AllowedHosts OK (cluckwork-sim.local)" while the app would receive `*`
#     and fail its boot. reset.sh documents that exact precedence 40 lines up,
#     for COMPOSE_PROJECT_NAME. Reproduced, not theorised.
#   * it checked `AllowedHosts` was non-empty and had no `*` — but the app
#     splits on ';', trims and drops empties, so `; ;` passed here and failed
#     the boot.
#   * it grepped the compose FILE for Database__AllowInsecureConnection, so the
#     line matching anywhere — another service, a comment-adjacent duplicate —
#     satisfied a check about what the `app` service actually gets.
#   * it accepted any non-blank endpoint once the insecure flag was true, while
#     OtlpOptions rejects a relative URI, a non-http(s) scheme, and any
#     userinfo/query/fragment.
#
# The rule that prevents all four: **assert against the value the app will
# actually receive, parsed the way the app parses it.** Everything below reads
# the RESOLVED compose environment (`docker compose config`, which applies
# ambient-shell precedence for us) — never the raw file.
#
# ================== WHAT THIS IS NOT ==================
#
# It is NOT a boot simulator, and must not grow into one. The app raises
# InvalidOperationException from ~15 sites across 7 files (ServingBootGuards,
# RateLimitingOptions, OtlpOptions, PostgresConnectionString,
# DatabaseResilienceOptions, the two Hosting extensions). Mirroring all of them
# here means reimplementing the app's config validation in Python and keeping
# two languages in sync forever — a race with no finish line, where every
# imperfect mirror is itself a potential false green in front of `down -v`.
#
# So the scope is deliberately bounded to **known drift**: the specific guards
# that have ALREADY broken this harness (#319, #261/#262, #316), checked as
# simple value assertions, plus the generic unresolved-interpolation check
# which needs no per-guard knowledge and covers everything else by construction.
#
# Guards NOT mirrored here, on purpose — a malformed RateLimiting__TrustedProxies
# CIDR (#260), an unsupported Otlp__Protocol, a bad ConnectionStrings__Default
# sslmode (#261/#262's parser), and the rest. The APP is the authority on those,
# and the cost of learning it at boot instead of here is one wasted reset of a
# database that is throwaway by design — annoying, not lossy. That is a better
# trade than a second, drifting copy of the rules.
#
# When a NEW guard breaks this harness, add it here (that is the AGENTS.md
# rule). Do not add guards speculatively.
#
# Seconds to run. No image build, no stack boot.

set -euo pipefail

SIM_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$SIM_DIR/.env.sim"
COMPOSE_FILE="$SIM_DIR/docker-compose.sim.yml"
BOOTSTRAP="$SIM_DIR/bootstrap.sh"

echo "== #243 harness self-check =="

# Tooling is a HARD requirement, never a skip. A skipped check in front of
# `down -v` is the same false green as a wrong one (PR #371 review).
for tool in docker python3 node; do
  command -v "$tool" >/dev/null 2>&1 \
    || { echo "FAIL: '$tool' is required by this self-check but is not on PATH" >&2; exit 1; }
done

if [[ ! -f "$ENV_FILE" ]]; then
  echo "FAIL: $ENV_FILE does not exist — run: bash tools/simulation/bootstrap.sh" >&2
  exit 1
fi

# --- 1. Pure logic -------------------------------------------------------
#
# The k6 date helpers, unit-tested with an INJECTED clock. This is the one
# check that cannot be replaced by "just run the harness": the report-window
# bug it pins fails only while UTC and the farm's date disagree, so a run at
# the wrong hour is green with the defect fully present (both runs recorded in
# findings/ were exactly that).
if node --test "$SIM_DIR/k6/dates.test.mjs" >/dev/null 2>&1; then
  echo "  k6 date helpers OK (report window never ends in the farm's future)"
else
  echo "FAIL: k6 date-helper tests failed — run: node --test tools/simulation/k6/dates.test.mjs" >&2
  exit 1
fi

# --- 2. bootstrap.sh's heredoc is inert ----------------------------------
#
# The .env.sim heredoc is UNQUOTED, so a backticked word or $(...) inside it is
# COMMAND SUBSTITUTION, not prose — a `bootstrap-admin` in a comment really did
# execute on every run. The original check here was "bootstrap.sh writes nothing
# to stderr", which is a proxy that only catches a command that does not exist:
# a backticked `date` runs silently and successfully, substituting its stdout
# into the generated file, with empty stderr (PR #371 review). Assert the SOURCE
# instead — the property is "nothing in there can execute", which is decidable
# by reading it.
python3 - "$BOOTSTRAP" <<'PY' || exit 1
import re, sys
src = open(sys.argv[1], encoding="utf-8").read().splitlines()
try:
    start = next(i for i, l in enumerate(src) if re.match(r'^cat >"\$ENV_FILE" <<EOF$', l))
except StopIteration:
    print("FAIL: could not locate the .env.sim heredoc in bootstrap.sh — this check "
          "must be updated alongside it, not silently skipped", file=sys.stderr)
    sys.exit(1)
end = next(i for i, l in enumerate(src[start + 1:], start + 1) if l == "EOF")
bad = [(n + 1, l) for n, l in enumerate(src[start + 1:end], start + 1)
       if "`" in l or "$(" in l]
if bad:
    print("FAIL: command substitution inside the UNQUOTED .env.sim heredoc — it will "
          "EXECUTE, not print:", file=sys.stderr)
    for n, l in bad:
        print(f"  bootstrap.sh:{n}: {l.strip()}", file=sys.stderr)
    sys.exit(1)
print(f"  bootstrap.sh heredoc inert OK ({end - start - 1} lines, no ` or $( )")
PY

# --- 3. The RESOLVED app environment -------------------------------------
#
# `docker compose config` is the whole point: it applies the same precedence the
# real `up` will (ambient shell over --env-file), so what comes out is what the
# container actually receives. Reading .env.sim here instead is the bug this
# section exists to not have.
# Via a FILE, not a pipe: `python3 - <<'PY'` already uses stdin for the script
# itself, so a piped payload would arrive as an empty read. (It did, and the
# resulting traceback failed every case including the baseline — which is the
# only reason it was caught: a mutation run where the BASELINE also fails proves
# nothing about the mutants.)
resolved_json="$(mktemp)"
compose_err="$(mktemp)"
trap 'rm -f "$resolved_json" "$compose_err"' EXIT
if ! docker compose -p cluckwork-sim --env-file "$ENV_FILE" -f "$COMPOSE_FILE" \
     config --format json >"$resolved_json" 2>"$compose_err"; then
  echo "FAIL: 'docker compose config' failed — the harness config does not parse" >&2
  cat "$compose_err" >&2
  exit 1
fi

# EVERY unresolved interpolation is fatal, not just the few named below.
# Compose substitutes a BLANK for an unset variable and merely WARNS, so a
# stale .env.sim missing POSTGRES_PASSWORD or Jwt__PrivateKeyPem sails past
# the value checks and dies after `down -v`. This generic check is what
# actually caught the original #316 breakage; an earlier revision of this
# script had it and the rewrite dropped it behind a 2>/dev/null, which is the
# regression this restores (PR #371 review). It also covers the interpolated
# keys nothing here validates by name — which is most of them.
if grep -qi 'variable is not set' "$compose_err"; then
  echo "FAIL: compose references variables the harness no longer generates —" >&2
  echo "      regenerate with: bash tools/simulation/bootstrap.sh --force" >&2
  grep -i 'variable is not set' "$compose_err" | sed 's/^/      /' >&2
  exit 1
fi

python3 - "$resolved_json" <<'PY' || exit 1
import json, re, sys
from urllib.parse import urlsplit

cfg = json.load(open(sys.argv[1], encoding="utf-8"))
try:
    env = cfg["services"]["app"]["environment"]
except KeyError:
    print("FAIL: resolved compose config has no services.app.environment", file=sys.stderr)
    sys.exit(1)

fail = []
ok = []

# --- #123 farm logo upload limit ------------------------------------------
# Mirror FarmLogoOptionsValidator so a stale or ambient override is caught
# before the Production-mode app is started.
logo_limit_raw = env.get("FarmLogo__MaxUploadBytes")
try:
    logo_limit = int(str(logo_limit_raw))
except (TypeError, ValueError):
    logo_limit = None

if logo_limit is None:
    fail.append(f"FarmLogo__MaxUploadBytes={logo_limit_raw!r} is not an integer")
elif logo_limit <= 0:
    fail.append(f"FarmLogo__MaxUploadBytes={logo_limit} must be greater than zero")
elif logo_limit > 5 * 1024 * 1024:
    fail.append(f"FarmLogo__MaxUploadBytes={logo_limit} exceeds the 5242880-byte ceiling")
else:
    ok.append(f"FarmLogo upload limit OK ({logo_limit} bytes)")

# --- #496 farm banner upload limit ---------------------------------------
# Mirror FarmBannerOptionsValidator so a stale or ambient override is caught
# before the Production-mode app is started.
banner_limit_raw = env.get("FarmBanner__MaxUploadBytes")
try:
    banner_limit = int(str(banner_limit_raw))
except (TypeError, ValueError):
    banner_limit = None

if banner_limit is None:
    fail.append(f"FarmBanner__MaxUploadBytes={banner_limit_raw!r} is not an integer")
elif banner_limit <= 0:
    fail.append(f"FarmBanner__MaxUploadBytes={banner_limit} must be greater than zero")
elif banner_limit > 15 * 1024 * 1024:
    fail.append(f"FarmBanner__MaxUploadBytes={banner_limit} exceeds the 15728640-byte ceiling")
else:
    ok.append(f"FarmBanner upload limit OK ({banner_limit} bytes)")

# --- #319 AllowedHosts ---------------------------------------------------
# Parsed EXACTLY as ServingBootGuards.EnsureAllowedHostsPinned does: split on
# ';', trim, drop empties, then
# require at least one entry and none equal to '*'. A raw non-empty/no-'*'
# check passes "; ;" and fails the boot.
raw = env.get("AllowedHosts")
if raw is None:
    fail.append("AllowedHosts is not set on the app service — #319 fails the Production boot")
else:
    hosts = [h.strip() for h in str(raw).split(";")]
    hosts = [h for h in hosts if h]
    if not hosts:
        fail.append(f"AllowedHosts={raw!r} contains no host after split/trim — "
                    "#319 fails the Production boot")
    elif any(h == "*" for h in hosts):
        fail.append(f"AllowedHosts={raw!r} contains a wildcard — #319 fails the Production boot")
    else:
        ok.append(f"AllowedHosts OK ({'; '.join(hosts)})")

# --- #261/#262 TLS opt-out ----------------------------------------------
# The EFFECTIVE value on the app service, not a grep of the file: a matching
# line elsewhere in the YAML says nothing about what this container receives.
db = env.get("Database__AllowInsecureConnection")
if db is None:
    fail.append("Database__AllowInsecureConnection is not set on the app service — "
                "#261/#262 fails the Production boot against the plaintext sidecar")
elif str(db).strip().lower() != "true":
    fail.append(f"Database__AllowInsecureConnection={db!r} is not 'true' — "
                "#261/#262 fails the Production boot against the plaintext sidecar")
else:
    ok.append("Database__AllowInsecureConnection OK")

# --- #316 OTLP: the endpoint and the flag are ONE guard ------------------
# Mirrors OtlpOptions.ResolveSignalEndpoint: absolute http(s) URI, and no
# userinfo / query / fragment. The insecure flag is an acknowledgement of
# PLAINTEXT, not permission for an arbitrary string.
endpoint = env.get("Otlp__Endpoint")
flag_raw = env.get("Otlp__AllowInsecureEndpoint")
flag = str(flag_raw).strip().lower() if flag_raw is not None else ""

if flag not in ("true", "false"):
    fail.append(
        "Otlp__AllowInsecureEndpoint is unset — Production boot fails binding '' to Boolean"
        if flag == "" else
        f"Otlp__AllowInsecureEndpoint={flag_raw!r} does not bind to Boolean — Production boot fails")

if endpoint is None or not str(endpoint).strip():
    fail.append("Otlp__Endpoint is not set on the app service")
else:
    endpoint = str(endpoint)
    parts = urlsplit(endpoint)
    # hostname/port, not raw netloc: urlsplit happily returns a non-empty
    # netloc for 'http://:4317' (no host) and 'http://collector:bad'
    # (non-numeric port), both of which .NET's Uri.TryCreate rejects at
    # startup — so netloc alone was a false pass (PR #371 review). Reading
    # .port raises ValueError on a non-numeric one, which is the check.
    try:
        bad_authority = not parts.hostname or (parts.port is None and ":" in parts.netloc)
    except ValueError:
        bad_authority = True
    if parts.scheme not in ("http", "https") or bad_authority:
        fail.append(f"Otlp__Endpoint={endpoint!r} is not an absolute http(s) URI with a "
                    "valid authority — OtlpOptions rejects it at Production startup")
    elif parts.username or parts.password:
        fail.append("Otlp__Endpoint contains userinfo — OtlpOptions rejects it "
                    "(value not echoed: it may carry a credential)")
    elif parts.query:
        fail.append("Otlp__Endpoint contains a query string — OtlpOptions rejects it "
                    "(value not echoed: it may carry a credential)")
    elif parts.fragment:
        fail.append(f"Otlp__Endpoint={endpoint!r} contains a fragment — OtlpOptions rejects it")
    elif parts.scheme != "https" and flag != "true":
        fail.append(f"Otlp__Endpoint={endpoint!r} is not https and "
                    f"Otlp__AllowInsecureEndpoint is {flag_raw!r} — #316 fails the Production boot")
    else:
        ok.append(f"Otlp endpoint OK ({parts.scheme}"
                  f"{', plaintext explicitly acknowledged' if parts.scheme != 'https' else ''})")

# #565 — a configured canonical profile must not inherit any of the SDK's
# standard transport leaves. The simulation deliberately supplies all three as
# explicit blanks so the resolved app environment exercises that authority.
if endpoint is not None and str(endpoint).strip():
    for standard_key in ("OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_EXPORTER_OTLP_HEADERS"):
        value = env.get(standard_key)
        if value is None:
            fail.append(f"{standard_key} is not set on the app service while Otlp__Endpoint is configured")
        elif str(value) != "":
            fail.append(f"{standard_key} must be empty while Otlp__Endpoint is configured (received a nonempty value)")
    if all(env.get(key) == "" for key in ("OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_EXPORTER_OTLP_HEADERS")):
        ok.append("canonical OTLP profile masks all standard OTLP transport variables")

# --- #543 shared-state Redis connection string ----------------------------
# Mirrors SharedStateRegistration.IsWellFormedConnectionString at a BOUNDED
# level (the #510 precedent below): the app's serving boot guard fails on a
# SET-BUT-MALFORMED value, so assert the RESOLVED value names an endpoint.
# Bounded, NOT a StackExchange.Redis parser reimplementation (that is how a
# checker drifts): a connection string is comma-separated tokens where an option
# is always key=value and an endpoint never contains '=', so ">=1 token without
# '='" is the well-formedness invariant without a full parser.
#
# BLANK IS REJECTED HERE, even though blank is LEGAL for an ordinary serving
# deploy (the app degrades to in-process, single instance). This is a
# harness-specific policy, stronger than the app guard on purpose: the sim wires
# a Redis sidecar and exercises the Redis-backed path deliberately, so a blank
# value (e.g. an ambient override `SharedState__Redis__ConnectionString=`, which
# Compose resolves with no unset-variable warning) would silently drop that path
# with no other signal — exactly the kind of drift this destructive-gate check
# exists to catch. The app allows blank; the harness requires Redis.
#
# SCOPE: this checks endpoint PRESENCE only, not option VALIDITY. A string like
# "redis:6379,bogusoption=x" has an endpoint and passes here, but the app's
# ConfigurationOptions.Parse rejects the unknown option and fails the boot. That
# is the deliberate bounded trade the header describes: the app is the authority
# on option names, and the cost of learning a bad option at boot instead of here
# is one wasted reset of a throwaway DB — not a false green that ships anything.
shared = env.get("SharedState__Redis__ConnectionString")
if shared is None or not str(shared).strip():
    fail.append("SharedState__Redis__ConnectionString is unset or blank — the sim harness "
                "wires Redis on purpose; a blank silently drops the Redis-backed path this "
                "change added (the app allows blank, this harness does not)")
else:
    tokens = [t.strip() for t in str(shared).split(",")]
    endpoints = [t for t in tokens if t and "=" not in t]
    if not endpoints:
        fail.append(f"SharedState__Redis__ConnectionString={shared!r} names no endpoint "
                    "(only options) — #543 fails the Production serving boot")
    else:
        ok.append(f"SharedState Redis connection OK ({'; '.join(endpoints)})")

# --- #510 JWT signing keys ------------------------------------------------
# The serving boot now refuses to start unless BOTH PEMs are present and
# actually import. The generic "variable is not set" check above cannot see
# this: a variable set to a BLANK, or to deploy/.env.example's `replace-me`
# armor, is set as far as compose is concerned and dies at boot instead.
#
# On the header's "do not add guards speculatively" rule: this one has NOT
# broken the harness — bootstrap.sh generates a real keypair, so a freshly
# bootstrapped .env.sim satisfies it. It is here because the AGENTS.md #370 rule
# requires a new boot guard to reach all three harness files, and because the
# thing it catches is one this repo actively SHIPS: deploy/.env.example's
# `replace-me` body is a plausible copy-paste source for a hand-edited .env.sim.
# It also cannot produce the failure mode the header is really about — it only
# ever appends to `fail`, so it can turn a green red, never a red green.
#
# Deliberately NOT a reimplementation of ImportFromPem — the claim is bounded to
# "this is a real key rather than a placeholder": armor present, and a body that
# is substantial base64. Chasing app parity in a checker is how a checker drifts
# from the thing it mirrors.
for key in ("Jwt__PublicKeyPem", "Jwt__PrivateKeyPem"):
    raw = env.get(key)
    if raw is None or not str(raw).strip():
        fail.append(f"{key} is missing or blank on the app service — "
                    "#510 fails the Production boot")
        continue
    pem = str(raw).replace("\\n", "\n")
    body = "".join(
        line for line in pem.splitlines()
        if line.strip() and not line.startswith("-----"))
    if "-----BEGIN" not in pem or "-----END" not in pem:
        fail.append(f"{key} has no PEM armor — #510 fails the Production boot")
    elif len(body) < 64 or not re.fullmatch(r"[A-Za-z0-9+/=]+", body):
        fail.append(f"{key} carries a placeholder body, not a key "
                    "(deploy/.env.example ships `replace-me`) — "
                    "#510 fails the Production boot; regenerate with bootstrap.sh")
    else:
        ok.append(f"{key} OK (PEM armor, {len(body)}-char base64 body)")

for line in ok:
    print(f"  {line}")
for line in fail:
    print(f"  FAIL: {line}", file=sys.stderr)
sys.exit(1 if fail else 0)
PY

# --- 4. Script-level vars, read the way reset.sh reads them --------------
#
# SIM_ADMIN_* are deliberately NOT app config (#283: no credential is boot
# config), so they never appear in the resolved compose environment — reset.sh
# greps them straight out of .env.sim, which is why this one section reads the
# file. Presence alone is not enough: a blank value passes a `grep -q` and then
# reset.sh wipes the volume before failing on the empty credential downstream
# (PR #371 review), which is precisely the destructive false green this gate
# exists to prevent.
env_fail=0
for required in SIM_ADMIN_EMAIL SIM_ADMIN_PASSWORD; do
  line="$(grep -E "^${required}=" "$ENV_FILE" | tail -n1 || true)"
  value="${line#*=}"
  if [[ -z "$line" ]]; then
    echo "  FAIL: $required missing — regenerate: bash tools/simulation/bootstrap.sh --force" >&2
    env_fail=1
  elif [[ -z "${value//[[:space:]]/}" ]]; then
    echo "  FAIL: $required is blank — reset.sh needs it to provision the first Owner" >&2
    env_fail=1
  fi
done

# Retired keys. .env.sim is git-ignored, so it outlives the schema that
# generated it — the file found in 2026-08 still carried pre-#283 runtime-seeder
# config while MISSING the vars reset.sh now needs.
for retired in Seed__AdminEmail Seed__AdminPassword Seed__Demo Seed__Enabled; do
  if grep -qE "^${retired}=" "$ENV_FILE"; then
    echo "  FAIL: $retired is retired (#283) but present — regenerate: bash tools/simulation/bootstrap.sh --force" >&2
    env_fail=1
  fi
done
(( env_fail )) || echo "  .env.sim script-level vars OK (SIM_ADMIN_* present and non-blank, no retired keys)"

if (( env_fail )); then
  echo "== harness self-check FAILED — fix the above before booting the stack ==" >&2
  exit 1
fi

echo "== harness self-check OK =="
