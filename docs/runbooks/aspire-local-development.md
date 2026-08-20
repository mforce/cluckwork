# Aspire local development

Aspire is the local orchestration path for the development stack. It starts the
existing PostgreSQL, Redis, API and Vite applications; it does not replace the
production-like Docker Compose workflow or introduce a deployment path.

**When to use:** developing or debugging the complete local stack with dynamic
endpoints and the Aspire dashboard.

**Blast radius:** normal start/stop preserves PostgreSQL data. The reset
procedure deletes only the exact validated AppHost PostgreSQL volume and cannot
be undone.

**Last drilled:** 2026-08-20.

---

## Prerequisites

Install the .NET 10 SDK, Node/npm, `jq`, and a Docker-compatible container
engine. Use Aspire CLI 13.5. The following installs that version only under this
worktree; it does not alter a global Aspire installation:

```bash
curl -fsSL https://aspire.dev/install.sh | bash -s -- \
  --version 13.5.0 \
  --install-path "$PWD/obj/aspire-cli" \
  --skip-path
obj/aspire-cli/aspire --version
```

The single AppHost `http` launch profile runs the AppHost in `Development`, and
the AppHost configures the API resource for `Development`. Consequently Aspire
loads the existing generated Postgres parameter from local user-secrets instead
of replacing it. The API performs its normal development migrations and reads
local credentials from user-secrets. Do not put secrets in a checked-in file. A
fresh database has no administrator; use the
[first-admin runbook](first-admin-provisioning.md) if you need one.

## Start and observe

From the repository root, use the AppHost explicitly on every lifecycle and
query command:

```bash
apphost=./src/Cluckwork.AppHost/Cluckwork.AppHost.csproj
aspire=./obj/aspire-cli/aspire

$aspire run --apphost "$apphost" --detach --non-interactive
$aspire wait postgres --apphost "$apphost" --timeout 180 --non-interactive
$aspire wait redis --apphost "$apphost" --timeout 180 --non-interactive
$aspire wait api --apphost "$apphost" --timeout 180 --non-interactive
$aspire wait web --apphost "$apphost" --timeout 180 --non-interactive
$aspire describe --apphost "$apphost" --format Json --non-interactive
```

Read the advertised `web` HTTP endpoint from the final description rather than
assuming a port. Aspire assigns host ports dynamically. The Vite app receives
the API target from Aspire, and binds its supplied port strictly, so calling
`<web-endpoint>/api/...` exercises the development proxy. The dashboard URL and
access mechanism are shown by the CLI; treat any dashboard token as a secret.

The CLI's resource display names are not log/telemetry query arguments. Use
AppHost-targeted, unfiltered queries, then filter by the exact trace ID:

```bash
$aspire logs --apphost "$apphost" --search <32-hex-trace-id> --format Json --tail 300 --non-interactive
$aspire otel traces --apphost "$apphost" --trace-id <32-hex-trace-id> --format Json --non-interactive
$aspire stop --apphost "$apphost" --non-interactive
```

The dashboard is the local surface for resource status, logs, traces and
metrics. Filter telemetry to the current API resource and use a non-health
request when checking HTTP, database and runtime metrics; startup and readiness
traffic is not evidence of application work.

## Persistence and reset

PostgreSQL data is durable across normal AppHost stop/start. Redis is
intentionally ephemeral. Before stopping or resetting anything, resolve the
current `postgres` resource and its container from the current `describe` output
and Docker metadata; never reuse a remembered container or volume name.

To reset only this AppHost database, resolve the current resource each time;
never use a remembered container or volume name. The resource's configured
image is useful context, but validate Docker's immutable top-level `.Image` ID
and its `RepoDigests`; `.Config.Image` is only the requested/display reference.
Then require exactly one writable Docker `volume` mount at `/var/lib/postgresql`.
Its literal name must be nonempty, contain no `/`, and pass `docker volume
inspect`. Stop the AppHost, wait until the exact container is either
`Running=false` or absent from `docker ps -a --no-trunc`, and wait until no
container references the volume before removing it non-force:

```bash
set -euo pipefail

expected_postgres_digest=3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a
expected_describe_image="docker.io/library/postgres@sha256:$expected_postgres_digest"
expected_repo_digest="postgres@sha256:$expected_postgres_digest"
expected_full_repo_digest="docker.io/library/postgres@sha256:$expected_postgres_digest"

describe=$($aspire describe --apphost "$apphost" --format Json --non-interactive)
postgres_resource=$(printf '%s' "$describe" | jq -cer '
  [.resources[] | select(.displayName == "postgres")]
  | if length == 1 then .[0] else error("expected exactly one postgres resource") end')
postgres_container=$(printf '%s' "$postgres_resource" | jq -er '
  .properties["container.id"]
  | if type == "string" and length > 0 then . else error("missing postgres container ID") end')
configured_image=$(printf '%s' "$postgres_resource" | jq -er '
  .properties["container.image"]
  | if type == "string" and length > 0 then . else error("missing postgres image") end')
[ "$configured_image" = "$expected_describe_image" ]

actual_image_id=$(docker inspect --format '{{.Image}}' "$postgres_container")
case "$actual_image_id" in sha256:*) ;; *) exit 1 ;; esac
repo_digests=$(docker image inspect --format '{{json .RepoDigests}}' "$actual_image_id")
printf '%s' "$repo_digests" | jq -e \
  --arg expected "$expected_repo_digest" \
  --arg expected_full "$expected_full_repo_digest" \
  'any(.[]; . == $expected or . == $expected_full)' >/dev/null

mounts=$(docker inspect --format '{{json .Mounts}}' "$postgres_container")
volume_mount=$(printf '%s' "$mounts" | jq -cer '
  [.[] | select(.Type == "volume" and .RW == true)]
  | if length == 1 and .[0].Destination == "/var/lib/postgresql"
    then .[0]
    else error("expected exactly one writable volume at the PostgreSQL 18 data path")
    end')
volume_name=$(printf '%s' "$volume_mount" | jq -er '.Name')
mount_destination=$(printf '%s' "$volume_mount" | jq -er '.Destination')
case "$volume_name" in ''|*/*) exit 1 ;; esac
docker volume inspect "$volume_name" >/dev/null

printf 'PostgreSQL reset target:\n  container: %s\n  configured image: %s\n  image ID: %s\n  volume: %s\n  mount: %s\n' \
  "$postgres_container" "$configured_image" "$actual_image_id" "$volume_name" "$mount_destination"

$aspire stop --apphost "$apphost" --non-interactive

container_stopped=false
for ((attempt = 0; attempt < 60; attempt++)); do
  if running=$(docker inspect --format '{{.State.Running}}' "$postgres_container" 2>/dev/null); then
    if [ "$running" = false ]; then
      container_stopped=true
      break
    fi
  else
    all_container_ids=$(docker ps -a --no-trunc --format '{{json .ID}}')
    container_present=$(printf '%s\n' "$all_container_ids" \
      | jq -ser --arg expected "$postgres_container" 'index($expected) != null')
    if [ "$container_present" = false ]; then
      container_stopped=true
      break
    fi
  fi
  sleep 1
done
[ "$container_stopped" = true ]

volume_released=false
for ((attempt = 0; attempt < 60; attempt++)); do
  volume_references=$(docker ps -a --filter "volume=$volume_name" --format '{{.ID}}')
  if [ -z "$volume_references" ]; then
    volume_released=true
    break
  fi
  sleep 1
done
[ "$volume_released" = true ]

docker volume inspect "$volume_name" >/dev/null
docker volume rm "$volume_name"
```

Do not use wildcard, recursive, force, or remembered-name deletion. Start the
AppHost again with the same explicit command and wait for `api`; its normal
Development migrations recreate the schema.

## Compose fallback

Docker Compose remains the production-like stack and the dependency-only
fallback for IDE/API work:

```bash
docker compose -f deploy/docker-compose.dev.yml up -d
dotnet run --project src/Cluckwork.Api
```

For the built SPA plus API stack, use:

```bash
docker compose -f deploy/docker-compose.yml up --build
```

Aspire is local orchestration only; it does not change Compose, simulation, or
production operations.

## Drill

Safe only when `aspire ps --format Json --non-interactive` reports no existing
developer-owned run and the AppHost database is disposable.

1. Start from the repository root, wait for `postgres`, `redis`, `api`, and
   `web`, then require exactly one healthy resource with each display name.
2. Send a valid-shaped unknown-user login through the dynamically advertised
   web endpoint. Expect the API's `401 application/problem+json` response, not
   Vite HTML or a proxy error.
3. Correlate that request in `aspire logs` and `aspire otel traces`; require the
   API server span and its PostgreSQL child. Confirm the current API resource
   exposes HTTP, Npgsql, EF Core, and runtime metrics in the dashboard.
4. Confirm the request creates a new namespaced limiter key in the exact Redis
   resource. Redis is ephemeral, so do not treat restart persistence as a pass
   condition for it.
5. Create a unique database marker, stop and restart the AppHost normally, and
   confirm both the marker and generated Postgres parameter persist.
6. Run the reset procedure above. Confirm every identity, image, mount, and
   stopped-container guard passes before the non-force volume removal; restart,
   wait for `api`, and confirm migrations recreate the schema without the
   marker.
7. Stop the AppHost, require `aspire ps --format Json --non-interactive` to be
   empty, and update **Last drilled** above.
