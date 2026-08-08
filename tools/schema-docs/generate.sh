#!/usr/bin/env bash
# #417 — generate (or verify) the committed PostgreSQL schema docs in docs/schema/.
#
#   tools/schema-docs/generate.sh          regenerate docs/schema/ in place
#   tools/schema-docs/generate.sh --check  regenerate into a temp dir and diff
#                                          against the committed docs; exit 1
#                                          on any difference (CI staleness gate)
#
# Mechanism: start an ephemeral digest-pinned Postgres, apply the repository
# migrations with the existing `migrate` CLI verb, run digest-pinned tbls
# against the migrated database, tear everything down. The migrate step runs
# on the HOST (it needs the published localhost port); tbls runs as a
# container on a private docker network (it reaches Postgres by container
# name) — two deliberately different network paths.
#
# Rationale, pins, and maintenance rules: docs/decisions/417-schema-docs.md
set -euo pipefail

cd "$(dirname "$0")/../.."

# Keep in lockstep with every other copy of this pin — enforced by
# SchemaDocsTests.PostgresImagePin_IsOneIdenticalStringAcrossEveryTrackedFile.
POSTGRES_IMAGE="postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a"
# tbls v1.95.0. Digest-pinned (repo convention for third-party images); not
# Dependabot-tracked — bumped manually, and the --check diff catches any
# output change a bump introduces. See the decision doc.
TBLS_IMAGE="ghcr.io/k1low/tbls@sha256:5d194e7baa9d14e740ee41f29985b8cb7601926fca019a4bb0c34b8c937bd8cb"

MODE="generate"
if [ "${1:-}" = "--check" ]; then MODE="check"; fi

NET="schema-docs-net-$$"
PG="schema-docs-pg-$$"
TMP_OUT=""
cleanup() {
  docker rm -f "$PG" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
  if [ -n "$TMP_OUT" ]; then rm -rf "$TMP_OUT"; fi
}
trap cleanup EXIT

# Ephemeral, runtime-generated credentials — never committed, never reused.
PGPW="$(openssl rand -hex 16)"

docker network create "$NET" >/dev/null
docker run -d --name "$PG" --network "$NET" \
  -e POSTGRES_USER=cluckwork -e POSTGRES_PASSWORD="$PGPW" -e POSTGRES_DB=cluckwork \
  -p 127.0.0.1:0:5432 \
  "$POSTGRES_IMAGE" >/dev/null

for i in $(seq 1 30); do
  if docker exec "$PG" pg_isready -h 127.0.0.1 -U cluckwork -d cluckwork >/dev/null 2>&1; then break; fi
  if [ "$i" = 30 ]; then echo "Postgres never became ready" >&2; exit 1; fi
  sleep 1
done

PORT="$(docker inspect -f '{{ (index (index .NetworkSettings.Ports "5432/tcp") 0).HostPort }}' "$PG")"

APP_DLL="src/Cluckwork.Api/bin/Release/net10.0/Cluckwork.Api.dll"
if [ ! -f "$APP_DLL" ]; then
  dotnet build Cluckwork.sln --configuration Release
fi

# Testing environment — the same one the integration-test factory uses, and
# for the same reason: not Development (which would pull the developer's
# user-secrets into the run and break clean-checkout parity), not Production
# (whose serving guards and TLS floor don't apply to a one-off local
# migrate). The host still boots, so it needs a JWT keypair — an ephemeral
# one, mirroring ci.yml's migrate step.
JWT_PRIV="$(openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 2>/dev/null)"
JWT_PUB="$(printf '%s' "$JWT_PRIV" | openssl rsa -pubout 2>/dev/null)"

ASPNETCORE_ENVIRONMENT=Testing \
Database__Provider=Postgres \
ConnectionStrings__Default="Host=127.0.0.1;Port=${PORT};Database=cluckwork;Username=cluckwork;Password=${PGPW}" \
Jwt__PrivateKeyPem="$JWT_PRIV" \
Jwt__PublicKeyPem="$JWT_PUB" \
Jwt__Issuer=schema-docs Jwt__Audience=schema-docs \
dotnet "$APP_DLL" migrate

DSN="postgres://cluckwork:${PGPW}@${PG}:5432/cluckwork?sslmode=disable"

run_tbls() { # $1 = extra volume args (may be empty), $2 = docPath override (may be empty)
  # --rm-dist, never --force: --force overwrites but does NOT delete pages of
  # dropped/renamed tables, leaving a stale file no regeneration can clear.
  # --user: the image runs as root; without this the generated files end up
  # root-owned on Linux hosts.
  # shellcheck disable=SC2086
  docker run --rm --network "$NET" --user "$(id -u):$(id -g)" \
    -v "$PWD:/work" $1 -w /work \
    "$TBLS_IMAGE" doc --rm-dist "$DSN" $2
}

if [ "$MODE" = "generate" ]; then
  run_tbls "" ""
  echo "Generated docs/schema/ — commit it alongside the migration that changed the schema."
else
  TMP_OUT="$(mktemp -d)"
  run_tbls "-v ${TMP_OUT}:/schema-check" "/schema-check"
  if ! diff -r docs/schema "$TMP_OUT" >/dev/null 2>&1; then
    echo "docs/schema/ is STALE — it does not match what the migrations produce." >&2
    echo "Regenerate with: tools/schema-docs/generate.sh" >&2
    diff -r docs/schema "$TMP_OUT" >&2 || true
    exit 1
  fi
  echo "docs/schema/ is up to date."
fi
