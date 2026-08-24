# Aspire local development

Aspire is the local orchestration path for the development stack. It starts the
existing PostgreSQL, Redis, API and Vite applications; it does not replace the
production-like Docker Compose workflow or introduce a deployment path.

**When to use:** developing or debugging the complete local stack with dynamic
endpoints and the Aspire dashboard.

**Blast radius:** normal start/stop preserves PostgreSQL data. The reset
procedure deletes only the exact validated AppHost PostgreSQL volume and cannot
be undone.

**Last drilled:** not recorded. Run the complete procedure below before adding a
date; a partial observability or persistence check is not a drill pass.

---

## Prerequisites

Install the .NET 10 SDK, Node/npm, `curl`, `jq`, and a Docker-compatible
container engine. Use Aspire CLI 13.5. The following installs that version only
under this worktree; it does not alter a global Aspire installation:

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
assuming a port. The repository pins the four host ports below, but any of them
can be overridden or returned to Aspire's random assignment, so the description
is the authority for what this run is actually using. The Vite app receives the
API target from Aspire, and binds its supplied port strictly, so calling
`<web-endpoint>/api/...` exercises the development proxy. The dashboard URL and
access mechanism are shown by the CLI; treat any dashboard token as a secret.

### Host ports

`src/Cluckwork.AppHost/appsettings.json` pins the host ports, so a `psql`,
`redis-cli` or browser URL stays valid across restarts:

| Key | Resource | Default |
|---|---|---|
| `LocalPorts:Postgres` | PostgreSQL host port | `5433` |
| `LocalPorts:Redis` | Redis host port | `6380` |
| `LocalPorts:Api` | API HTTP endpoint | `8080` |
| `LocalPorts:Web` | Vite dev server | `5173` |

`5432` and `6379` are deliberately avoided: `deploy/docker-compose.dev.yml`
publishes those and keeps a separate data volume, so reusing one would either
fail the launch or point this stack at the other stack's database.

A pinned port fails the launch when something else already holds it. Override
per machine with user-secrets, per shell with an environment variable, or per
run with an argument:

```bash
dotnet user-secrets --project src/Cluckwork.AppHost set "LocalPorts:Api" "8081"
LocalPorts__Api=8081 aspire run --apphost "$apphost"
dotnet run --project src/Cluckwork.AppHost -- --LocalPorts:Api=8081
```

An empty value restores Aspire's original behaviour of assigning a random free
port, which is also what an unparseable value falls back to rather than
throwing:

```bash
dotnet run --project src/Cluckwork.AppHost -- --LocalPorts:Api=
```

Redis is served over TLS on its advertised endpoint, so a plaintext `redis-cli`
against the pinned port gets no reply; use the TLS options as below.

The dashboard and OTLP endpoints are not covered by these keys and still move
between runs; take them from the CLI each time.

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

This drill is destructive to the AppHost PostgreSQL volume. Use only a
disposable, fresh database with no Owner: the exact login assertion below is the
fresh-database contract. Run every block from one Bash shell at the repository
root. If the volume is not already fresh, start the stack, run the guarded reset
procedure above, leave it stopped, and then begin.

Start exactly one run, wait for every orchestrated resource, resolve the dynamic
web endpoint and exact current containers, and take the Redis before-snapshot.
The Redis password expands only inside its container and is never printed:

```bash
set -euo pipefail

apphost=./src/Cluckwork.AppHost/Cluckwork.AppHost.csproj
aspire=./obj/aspire-cli/aspire
drill_tmp=$(mktemp -d)
run_owned=false

cleanup_drill() {
  if [ "$run_owned" = true ]; then
    "$aspire" stop --apphost "$apphost" --non-interactive >/dev/null 2>&1 || true
  fi
  rm -f -- "$drill_tmp/headers" "$drill_tmp/body" \
    "$drill_tmp/redis-before" "$drill_tmp/redis-after" \
    "$drill_tmp/logs" "$drill_tmp/trace"
  rmdir "$drill_tmp" 2>/dev/null || true
}
trap cleanup_drill EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

[ "$($aspire ps --format Json --non-interactive | jq -c .)" = '[]' ]

$aspire run --apphost "$apphost" --detach --non-interactive
run_owned=true
for resource in postgres redis api web; do
  $aspire wait "$resource" --apphost "$apphost" --timeout 180 --non-interactive
done

describe=$($aspire describe --apphost "$apphost" --format Json --non-interactive)
for resource in postgres redis api web; do
  count=$(printf '%s' "$describe" | jq -er --arg name "$resource" \
    '[.resources[] | select(.displayName == $name)] | length')
  [ "$count" -eq 1 ]
done

web_url=$(printf '%s' "$describe" | jq -er '
  [.resources[] | select(.displayName == "web")][0]
  | [.urls[] | select(.name == "http" and .isInternal != true)]
  | if length == 1 then .[0].url
    else error("expected exactly one external web HTTP endpoint")
    end')

container_for() {
  printf '%s' "$describe" | jq -er --arg name "$1" '
    [.resources[] | select(.displayName == $name)]
    | if length == 1 then .[0].properties["container.id"]
      else error("expected exactly one resource")
      end
    | if type == "string" and length > 0 then .
      else error("missing container ID")
      end'
}

redis_container=$(container_for redis)
postgres_container=$(container_for postgres)

scan_limiter_keys() {
  docker exec "$redis_container" sh -ceu '
    [ -n "${REDIS_PASSWORD:-}" ]
    export REDISCLI_AUTH="$REDIS_PASSWORD"
    # Aspire 13.5 run mode makes 6379 the TLS listener and adds the
    # container-internal 6380 secondary listener for plaintext Redis CLI use.
    exec redis-cli -h 127.0.0.1 -p 6380 \
      --scan --pattern "{cluckwork:win:*}*"
  ' | LC_ALL=C sort -u
}

scan_limiter_keys >"$drill_tmp/redis-before"
```

Before the application request, open the dashboard shown by the current run,
select the current `Cluckwork.Api` resource, switch histogram rows to **Show
count**, and record the timestamp and numeric count for these exact rows:

- `http.server.request.duration`
- `db.client.operation.duration` with Npgsql/PostgreSQL dimensions
- `microsoft.entityframeworkcore.queries` under the
  `Microsoft.EntityFrameworkCore` meter

Also require the separate `dotnet.process.cpu.time` runtime row to be present.
Do not record the dashboard URL or token.

Generate a unique request identity and W3C trace context in memory, then send
exactly one login through the resolved Vite proxy. The generated password and
email are removed from the shell immediately after the request:

```bash
trace_id=$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')
span_id=$(od -An -N8 -tx1 /dev/urandom | tr -d ' \n')
login_email="drill-${trace_id}@example.invalid"
login_password="Drill-${trace_id:0:12}!aA1"
login_json=$(jq -cn \
  --arg email "$login_email" \
  --arg password "$login_password" \
  '{email: $email, password: $password}')

status=$(printf '%s' "$login_json" | curl --silent --show-error \
  --dump-header "$drill_tmp/headers" \
  --output "$drill_tmp/body" \
  --write-out '%{http_code}' \
  --header "traceparent: 00-${trace_id}-${span_id}-01" \
  --header 'content-type: application/json' \
  --data-binary @- \
  "$web_url/api/v1/auth/login")
unset login_json login_password login_email

[ "$status" = 401 ]
tr -d '\r' <"$drill_tmp/headers" | grep -Eiq \
  '^content-type:[[:space:]]*application/problem\+json([;[:space:]]|$)'
jq -e '.title == "Auth.NoOwnerProvisioned"' "$drill_tmp/body" >/dev/null
```

Require exactly one new namespaced limiter key, then poll the AppHost-targeted
console and OTLP queries for the same trace:

```bash
scan_limiter_keys >"$drill_tmp/redis-after"
new_limiter_count=$(comm -13 \
  "$drill_tmp/redis-before" "$drill_tmp/redis-after" | wc -l)
[ "$new_limiter_count" -eq 1 ]

telemetry_ready=false
for ((attempt = 0; attempt < 60; attempt++)); do
  $aspire logs --apphost "$apphost" --search "$trace_id" \
    --format Json --tail 300 --non-interactive >"$drill_tmp/logs" || true
  $aspire otel traces --apphost "$apphost" --trace-id "$trace_id" \
    --format Json --non-interactive >"$drill_tmp/trace" || true

  if grep -Fq "$trace_id" "$drill_tmp/logs" \
      && grep -Fq '/api/v1/auth/login' "$drill_tmp/logs" \
      && ! grep -Fiq 'x-otlp-api-key' "$drill_tmp/logs" \
      && grep -Fq "$trace_id" "$drill_tmp/trace" \
      && grep -Eiq 'server' "$drill_tmp/trace" \
      && grep -Eiq 'Npgsql|PostgreSQL|postgres' "$drill_tmp/trace" \
      && ! grep -Fiq 'x-otlp-api-key' "$drill_tmp/trace"; then
    telemetry_ready=true
    break
  fi
  sleep 1
done
[ "$telemetry_ready" = true ]
```

Open that exact trace in the dashboard and require the API server span to be the
parent of an Npgsql/PostgreSQL client span. The bounded string checks above are
only the query-readiness gate; matching two unrelated spans is not a hierarchy
pass.

In the dashboard, revisit the same current-resource rows. Require a newer
timestamp and a strictly larger numeric count for each of the three application
instruments; mere row presence or startup traffic does not pass. Require the
HTTP row to show the login route and `401`, and the database row to show
Npgsql/PostgreSQL.

Create a safe unique PostgreSQL marker and record only the generated parameter
file's metadata. The `jq` assertion checks that the parameter is nonblank but
does not print it; PostgreSQL and Redis passwords remain inside their
containers:

```bash
marker="aspire_drill_${trace_id:0:16}"
case "$marker" in *[!a-z0-9_]*) exit 1 ;; esac

postgres_sql() {
  docker exec "$postgres_container" sh -ceu '
    [ -n "${POSTGRES_PASSWORD:-}" ]
    export PGPASSWORD="$POSTGRES_PASSWORD"
    exec psql -h 127.0.0.1 -U "${POSTGRES_USER:-postgres}" \
      -d cluckwork -v ON_ERROR_STOP=1 -Atc "$1"
  ' sh "$1"
}

postgres_sql \
  "CREATE TABLE $marker (id integer PRIMARY KEY); INSERT INTO $marker VALUES (1);" \
  >/dev/null
[ "$(postgres_sql "SELECT to_regclass('public.$marker') IS NOT NULL;")" = t ]

secrets_id=$(dotnet msbuild "$apphost" -nologo -getProperty:UserSecretsId)
[ "$secrets_id" = cluckwork-apphost-local ]
secrets_file="$HOME/.microsoft/usersecrets/$secrets_id/secrets.json"
jq -e '
  .["Parameters:postgres-password"]
  | type == "string" and length > 0' "$secrets_file" >/dev/null

file_signature() {
  if stat -c '%Y:%y:%s' "$1" >/dev/null 2>&1; then
    stat -c '%Y:%y:%s' "$1"
  else
    stat -f '%m:%c:%z' "$1"
  fi
}
secrets_signature=$(file_signature "$secrets_file")
```

Stop and restart normally, re-resolve the new container, and require both the
marker and the parameter-file metadata to be unchanged:

```bash
$aspire stop --apphost "$apphost" --non-interactive
run_owned=false

stopped=false
for ((attempt = 0; attempt < 60; attempt++)); do
  if [ "$($aspire ps --format Json --non-interactive | jq -c .)" = '[]' ]; then
    stopped=true
    break
  fi
  sleep 1
done
[ "$stopped" = true ]

$aspire run --apphost "$apphost" --detach --non-interactive
run_owned=true
for resource in postgres redis api web; do
  $aspire wait "$resource" --apphost "$apphost" --timeout 180 --non-interactive
done

describe=$($aspire describe --apphost "$apphost" --format Json --non-interactive)
postgres_container=$(container_for postgres)
[ "$(file_signature "$secrets_file")" = "$secrets_signature" ]
[ "$(postgres_sql "SELECT to_regclass('public.$marker') IS NOT NULL;")" = t ]
```

With that restarted stack still running, execute the complete guarded reset
block in **Persistence and reset** above. It must print the exact container,
configured image, immutable image ID, literal volume, and mount before stopping;
all identity and release assertions must pass before the single non-force
`docker volume rm`.

Restart once more and require Development migrations to recover the API while
the marker is absent from the new database:

```bash
run_owned=false
$aspire run --apphost "$apphost" --detach --non-interactive
run_owned=true
$aspire wait api --apphost "$apphost" --timeout 180 --non-interactive

describe=$($aspire describe --apphost "$apphost" --format Json --non-interactive)
postgres_container=$(container_for postgres)
[ "$(postgres_sql "SELECT to_regclass('public.$marker') IS NULL;")" = t ]

$aspire stop --apphost "$apphost" --non-interactive
run_owned=false
final_ps=
for ((attempt = 0; attempt < 60; attempt++)); do
  final_ps=$($aspire ps --format Json --non-interactive | jq -c .)
  [ "$final_ps" = '[]' ] && break
  sleep 1
done
[ "$final_ps" = '[]' ]

trap - EXIT INT TERM
cleanup_drill
```

Only after every assertion passes may **Last drilled** be changed from `not
recorded` to the current date.
