# #243 Release-Rehearsal Sim Harness

A dedicated, throwaway Cluckwork stack for load-testing: a Production-config
app + Postgres running under its own docker compose project
(`cluckwork-sim`), seeded by `SimulationDataSeeder` with a large,
deterministic fixture (a role-weighted user pool, a minimal flock topology,
90 days of production history, and sales/inventory/expense lifecycle data).
This directory owns the compose overlay, the secret bootstrap, the
reset/verify script, the k6 load-test scripts, monitoring (a local OTLP
metrics sink + `docker stats`/Postgres snapshotters — see "Monitoring"
below), and the multi-rep baseline orchestrator (`run-baseline.sh` — see
"Baseline orchestrator" below) that produces an honest findings doc under
`tools/simulation/findings/`.

#283: the default account/roles/egg grades ship as static reference data in
the EF migrations themselves — the serving `app` container's ordinary
migrate-on-startup boot provisions them, no `Seed:*` config involved. There
is still no Owner after that (no credential is ever baked into a migration),
so `reset.sh` runs the one-shot `bootstrap-admin` command once the container
is healthy, captures its printed one-time password, and rotates it via the
real login+change-password API calls to the stable password already in
`.sim-cast.json` — every persona/script in this harness keeps a known,
unchanging Owner credential.

#279: `SimulationDataSeeder` (the cast/fixture) is not a boot-time side
effect either. `reset.sh` seeds it by running `dotnet Cluckwork.Api.dll seed
--profile simulation` as an explicit **one-shot** `docker compose run`
against a non-Production environment (Program.cs's prod guard refuses the
command in Production),
the same command an operator would type by hand — mirroring `seed --profile
demo` (#280) for the dev/demo profile.

## Quickstart

```bash
bash tools/simulation/bootstrap.sh   # generate .env.sim + .sim-cast.json (idempotent)
bash tools/simulation/reset.sh       # wipe + rebuild + base-seed + `seed --profile simulation` + verify
bash tools/simulation/run-baseline.sh   # N reps of the k6 baseline -> findings doc

bash tools/simulation/verify-harness.sh # ~0.1s self-check; reset.sh runs it for you
```

## KEEPING THIS HARNESS ALIVE (read before changing a boot guard) — #370

**Nothing automated runs this harness.** It is deliberately not in CI — it is
dev tooling, and a GitHub job on every push is out of proportion to five
seconds of work. The consequence is the thing to internalise: **when you break
it, nothing tells you.** Every path in is human-started — `reset.sh` directly,
or `run-baseline.sh`, which invokes `reset.sh` once per rep. No schedule, no
hook, no pipeline. (Both paths do get the `verify-harness.sh` self-check, since
`reset.sh` runs it — so a baseline run fails fast on config drift too.)

That is not hypothetical. By 2026-08 this harness could not boot `main` at
all — four breakages had piled up, **three of them app-side Production boot
guards that landed while the harness config stayed put**:

| Landed | Broke the harness because |
| --- | --- |
| #319 `AllowedHosts` | a wildcard now **fails the boot**; the stack did not start |
| #261/#262 TLS floor | unset `sslmode` means Npgsql `Prefer` — the rejected case |
| #316 OTLP | a blank `Otlp__AllowInsecureEndpoint` fails to bind to `Boolean` |

The `app` container here runs **Production config on purpose** (that is the
point — prod-config fidelity), so **every** such guard applies to it.

**So: if your PR adds or changes a boot guard, or adds/renames/retires a config
key, update `bootstrap.sh` + `docker-compose.sim.yml` in the same PR**, and add
an assertion to `verify-harness.sh`. `reset.sh` runs that self-check *before*
the destructive `down -v`, so a config defect costs 0.1s instead of a wiped
volume, an image rebuild, and a five-minute wait to fail at `/health/ready` —
which is exactly how these were actually found.

Satisfy a guard **properly**, never by switching it off: a concrete
`AllowedHosts` (not `*`), and the *documented* plaintext opt-outs for this
stack's co-located sidecar — the same ones `deploy/docker-compose.yml` uses.

**One trap worth knowing.** `.env.sim` is git-ignored, so it outlives the
schema that generated it. The one found in 2026-08 was pre-#283: it still
carried retired `Seed__Admin*`/`Seed__Demo` keys **and lacked** the
`SIM_ADMIN_*` that `reset.sh` now needs. When a key changes, regenerate rather
than hand-patch: `bash tools/simulation/bootstrap.sh --force` (new secrets are
free — `reset.sh` wipes the database anyway). `verify-harness.sh` fails on
those retired keys by name so a stale file says so instead of failing obscurely
mid-boot.

`reset.sh` leaves the app reachable at `http://127.0.0.1:8081/`. Log in with
any credential from `tools/simulation/.sim-cast.json` (the Owner, or any of
the deterministic `sim-manager-N@sim.local` / `sim-sales-N@…` /
`sim-worker-N@…` / `sim-readonly-N@…` cast members — see
`SimulationDataSeeder.SeedCastAsync`). The seeder's own completion manifest
(row counts + lifecycle-state matrix, not credentials), written by the
`seed --profile simulation` one-shot run above, lands at
`tools/simulation/out/manifest.json`.

## SAFETY: the `cluckwork-sim` project

The real local dev stack (`deploy/docker-compose.yml`) owns a **named**
Postgres volume under the **default** compose project. Every command in this
directory — bootstrap, reset, and any ad hoc `docker compose`/`docker stats`/
DB command you run by hand — **must** stay under the dedicated
`cluckwork-sim` project:

- `docker-compose.sim.yml` sets `name: cluckwork-sim` at the top level, so
  even a bare `docker compose -f tools/simulation/docker-compose.sim.yml up`
  resolves to `cluckwork-sim`, never a directory-derived name.
- `.env.sim` also sets `COMPOSE_PROJECT_NAME=cluckwork-sim`.
- `reset.sh` **asserts** the project name it is about to use equals
  `cluckwork-sim` and **aborts before running `down -v`** if it doesn't —
  this is the one hard gate that stands between this script and deleting the
  wrong Postgres volume. **Never** override `COMPOSE_PROJECT_NAME` when
  running `reset.sh`, and never run a bare `docker compose down -v` against
  this stack.

If you need to run compose commands by hand, always pass
`-p cluckwork-sim --env-file tools/simulation/.env.sim -f
tools/simulation/docker-compose.sim.yml`.

## Why a self-contained compose file, not a `deploy/docker-compose.yml` overlay

`docker-compose.sim.yml` defines its own `app` and `db` — it does not layer
on top of `deploy/docker-compose.yml` with `-f deploy/docker-compose.yml -f
tools/simulation/docker-compose.sim.yml`. The base file's services declare
`env_file: - .env` (loading the developer's **real** `deploy/.env`) *and* a
base `environment:` block that outranks any `env_file`. Fighting that
precedence to keep `deploy/.env`'s real secrets — Postgres credentials, JWT
keys, and especially a real `Otlp__Endpoint` — from leaking into the sim run
is exactly the trap the #243 plan review flagged. A fully independent `app`/
`db` pair removes the leak path entirely: this file never references
`deploy/.env` or `deploy/docker-compose.yml` at all. The only thing tying the
two together is that `app` builds the **same** `src/Cluckwork.Api/Dockerfile`
the real stack uses, for prod-config fidelity.

## Load-bearing parameters

Everything below lives in `.env.sim`, generated by `bootstrap.sh` (see that
script for the exact values/algorithm) — this table is what to know when
reading or changing it:

| Area | Value | Why |
| --- | --- | --- |
| `Jwt__PublicKeyPem` / `Jwt__PrivateKeyPem` | Freshly generated 2048-bit RSA keypair, `\n`-escaped single-line PEM (same format as `deploy/.env.example`) | Never reuse the real deploy keypair in a throwaway stack. `PemKey.Normalize` accepts this format regardless of whether docker compose's env-file interpolation later expands the `\n` escapes to real newlines itself (`Replace("\\n","\n")` is a no-op once they're already real) — verified both ways. |
| `SIM_ADMIN_EMAIL` / `SIM_ADMIN_PASSWORD` | `admin@sim.local` + a generated 20-char password (upper/lower/digit/symbol) | **Script-level only** (no `__`, so it never reaches the app as config — #283, no credential is boot-config-driven anymore). `reset.sh` reads these to call `bootstrap-admin --email "$SIM_ADMIN_EMAIL"`, then rotates the printed one-time password to `SIM_ADMIN_PASSWORD`; reused as the Owner by `SimulationDataSeeder` (`seed --profile simulation`) — it never creates a second Owner. |
| `Simulation__CastPassword` | One generated 20-char password shared by the whole cast | Every `sim-*@sim.local` login in `.sim-cast.json` uses this. |
| `Simulation__Managers/Sales/Workers/ReadOnly` | `1/1/3/4` | Mirrors `SimulationOptions`' own C# defaults — written explicitly so `bootstrap.sh`'s `.sim-cast.json` can never silently drift from what the seeder actually creates. |
| `Simulation__HistoryDays` | `90` | #243 Task 3d's chosen depth — enough for the production report and sales/expense/profit summaries to scan a meaningful volume. |
| `Simulation__TimeZoneId` | `America/Chicago` | Non-UTC, so the primary account's timezone handling is actually exercised. |
| `Simulation__CredentialOutputPath` | `/app/sim-cast/manifest.json` | Mounted to the host at `tools/simulation/out/manifest.json` via the `app` service's `./out:/app/sim-cast` volume. |
| `RateLimiting__Login__PermitLimit` / `…Refresh…` / `…ClientErrors…` | `1000000` (windows unchanged) | All three per-IP buckets raised — see deviation list below. |
| `Otlp__Endpoint` / `Otlp__Protocol` | `http://otel-collector:4317` / `grpc` | See "Monitoring" and deviation list below. |
| `AllowedHosts` | `cluckwork-sim.local` | A concrete placeholder host, **not** `*`. There is no traefik/public hostname in front of the sim stack, but the serving container runs Production config and #319 **fails that boot** on a missing, blank, or wildcard value — a `*` here does not merely weaken Host filtering, it stops the stack booting. Loopback is force-added by `AddCluckworkEdgeSecurity` whenever the list is not wildcard, so k6 and the browser still reach the app on `http://127.0.0.1:8081/`. |
| `POSTGRES_DB` / `POSTGRES_USER` / `POSTGRES_PASSWORD` | `cluckwork_sim` / `cluckwork_sim` / generated | Isolated database, isolated named volume (`cluckwork-sim-postgres`, namespaced by the compose project to `cluckwork-sim_cluckwork-sim-postgres`) — never the real dev DB. |
| App port | `127.0.0.1:8081` | Different from the real dev stack's `127.0.0.1:8080` (`deploy/docker-compose.yml`), so both can run at once without colliding. |
| `db` port | *(not published)* | `reset.sh`'s `pg_stat_statements` preflight runs in-container (`docker compose exec db psql`), never over a host connection — one less thing to collide with a real local Postgres. |

## Sanctioned deviations from Production config

The **serving** app container runs Production config (`ASPNETCORE_ENVIRONMENT`
is not overridden to Development, and stays that way for the whole life of
the stack) with these deliberate, documented deviations, all isolated to
`.env.sim`. The ONE exception is the `seed --profile simulation` one-shot
`reset.sh` runs after the serving container is up: that specific,
short-lived `docker compose run` overrides `ASPNETCORE_ENVIRONMENT=Development`
for itself only, because `SimulationDataSeeder` is deliberately unavailable
in Production (Program.cs's DI registration + the `seed` command's own
guard both refuse it there) — see "Why `seed --profile simulation` needs a
non-Production environment" below.

1. **All three per-IP rate-limit buckets raised** (Login, Refresh,
   ClientErrors) to `1000000`. Production keeps these tight to stop
   credential spraying / log flooding from one address; a load test from a
   single IP would trip the real limits almost immediately and the results
   would measure the limiter, not the app. **Invalidates:** any claim about
   real-world per-IP throttling behavior under load.
2. **A local Postgres sidecar** — plaintext connection, uncapped host
   resources, no managed-PG connection limits or TLS. **Invalidates:** any
   absolute throughput/latency number as representative of a networked,
   TLS, resource-capped production database.
3. **`Otlp__Endpoint` points at the sim-only `otel-collector` service**
   (`http://otel-collector:4317`, `Otlp__Protocol=grpc`) — see "Monitoring"
   below. Never a developer's real deploy/.env collector endpoint.
   **Invalidates:** nothing — this is strictly additive observability, not a
   config deviation with a throughput/latency implication.
4. **No traefik / TLS front door**, a placeholder `AllowedHosts`
   (`cluckwork-sim.local` — concrete, never `*`, since #319 fails a
   Production boot on a wildcard), loopback-only port publish. The real stack
   fronts the app with traefik + TLS (`deploy/docker-compose.yml`'s `prod`
   profile); this stack is reached directly on `http://127.0.0.1:8081/`,
   which works because loopback is force-added to the host-filter list.
   **Invalidates:** anything about TLS-termination overhead or traefik
   routing.

Every consumer of this harness's output (k6 results, findings docs, #277's
Playwright suite) must carry this list forward rather than presenting
sim-stack numbers as production-equivalent.

## Why `seed --profile simulation` needs a non-Production environment

`SimulationDataSeeder` is registered in DI only when
`!builder.Environment.IsProduction()` (mirrors `DemoDataSeeder`) — a
defense-in-depth guard so a Production process can never run the sim seed
even if `seed --profile simulation` is invoked against it by mistake. Since
the serving `app` container stays Production for its whole life (see
"Sanctioned deviations" above), `reset.sh` cannot run the seed command
against that long-running container — it runs it as a **separate,
short-lived** `docker compose run --rm -e ASPNETCORE_ENVIRONMENT=Development
app seed --profile simulation` instead. That override applies only to the
one-shot container; the serving `app` service's own environment is
untouched. Running the same command WITHOUT the override (i.e. against
Production) is expected to print an operator-facing "not available in
Production" message on stderr and exit 1 — this is the prod-guard working as
intended, not a bug.

## `pg_stat_statements`

`db` starts with `command: postgres -c
shared_preload_libraries=pg_stat_statements` — the extension needs the
library **preloaded** at server start; `CREATE EXTENSION` alone is not
enough. `reset.sh`'s preflight runs `CREATE EXTENSION IF NOT EXISTS
pg_stat_statements;` (idempotent — a no-op if it already exists) and then a
`SELECT` from it, in-container via `docker compose exec db psql`.

## Monitoring (#243 Task 8)

Why: under this lean stack, `pg_stat_activity` only shows connections that
**reached** Postgres — it can't see a request queued in the app waiting for a
free pooled connection (Npgsql's max pool is 100, exactly matching stock
Postgres's `max_connections` of 100 — zero headroom). A local OTLP collector
capturing the app's Npgsql/runtime meters makes that saturation visible
while keeping the app in Production config, alongside container resource
usage and Postgres-side snapshots.

### Local OTLP metrics sink

`otel-collector` (`otel/opentelemetry-collector-contrib`, config at
`otel/collector.yaml`) receives the `app` container's OTLP push — enabled by
`bootstrap.sh` pointing `Otlp__Endpoint` at `http://otel-collector:4317`
(`Otlp__Protocol=grpc`; see `Program.cs`'s `OtlpOptions` — export is
config-gated and turns on only when `Otlp:Endpoint` is set). Confirm export
is live via the app's boot log:

```bash
docker compose -p cluckwork-sim --env-file tools/simulation/.env.sim \
  -f tools/simulation/docker-compose.sim.yml logs app | grep "OTLP export"
# app-1  | OTLP export enabled: traces -> http://otel-collector:4317/, metrics -> http://otel-collector:4317/ (Grpc)
```

The collector exposes two sinks, neither backed by a host volume (see "Why
no host-writable path for the collector" below):

- **Prometheus exporter**, host-curlable on loopback:
  `curl -s http://127.0.0.1:8889/metrics`. This is the primary sink — grep it
  for `db_client_.*npgsql` (Npgsql connection/operation metrics, e.g.
  `db_client_operation_npgsql_executing`, `db_client_connection_npgsql_create_time_seconds`)
  and `dotnet_gc_`/`dotnet_thread_pool_` (runtime GC/threadpool metrics).
- **Debug exporter** — full per-datapoint detail on container stdout:
  `docker compose -p cluckwork-sim ... logs otel-collector`.

The collector also runs a `traces` pipeline (debug exporter only) purely so
the OTLP receiver's trace gRPC service is registered — the app's exporter is
configured for both signals off the one `Otlp:Endpoint`, and an
unregistered trace service would make every span export fail loudly.

### `monitor/docker-stats-sampler.sh`

Samples `docker stats --no-stream` for every container in the
`cluckwork-sim` project (app, db, otel-collector) on an interval, appending
CSV rows (`timestamp,container,cpu_pct,mem,net_io,block_io`). Runs under the
same `-p cluckwork-sim` safety gate as `reset.sh`. Stops on Ctrl-C/SIGTERM or
after `--duration SECONDS`:

```bash
bash tools/simulation/monitor/docker-stats-sampler.sh --interval 2 --duration 60
```

### `monitor/pg-snapshot.sh`

Three subcommands around a load-test run, all via `docker compose -p
cluckwork-sim exec -T db psql` (never a host Postgres connection — `db` has
no published port):

```bash
bash tools/simulation/monitor/pg-snapshot.sh start    # resets pg_stat_statements, records pg_database_size "before"
# ... run k6 ...
bash tools/simulation/monitor/pg-snapshot.sh sample    # pg_stat_activity (state/wait_event) + pg_blocking_pids + connections vs max_connections
bash tools/simulation/monitor/pg-snapshot.sh end       # pg_database_size "after" + delta, top pg_stat_statements
```

Docker Block I/O (from the stats sampler above) is network/disk **traffic**,
not size — `pg_database_size(current_database())` is the only correct way to
see the database actually grow; `end` reports the before/after delta.

### Why no host-writable path for the collector, and where output lands

`tools/simulation/out/` is written into by the one-shot `seed` container as
**root** — the image is non-root (uid 1654, #267), so reset.sh runs that
container with `--user 0` to write the manifest (`Simulation__CredentialOutputPath`)
— so anything landing there is root-owned and often not writable by the host
user. Rather than
`chown`/`sudo` around that, none of the Task 8 monitoring pieces touch it:
the collector's two sinks (Prometheus scrape, debug-exporter stdout) need no
file at all, and both `monitor/*.sh` scripts write to
`tools/simulation/monitor/out/` — a sibling directory they create themselves
as the host user (already covered by the existing `out/` entry in
`.gitignore`, which matches at any depth under `tools/simulation/`).

## Baseline orchestrator (`run-baseline.sh`, #243 Task 9)

Runs `REPS` (default 3) independent reps of the k6 baseline against a
**fresh** `cluckwork-sim` stack each time, with monitoring wrapped around
every rep, then aggregates across reps and renders an honest findings doc.

```bash
bash tools/simulation/run-baseline.sh
```

**Pinned k6 version:** `run-baseline.sh` runs k6 through `tools/simulation/k6/shell.nix` (a pinned nixpkgs revision), never a bare `nix-shell -p k6` — and additionally asserts `k6 version` matches the `EXPECTED_K6_VERSION` recorded at the top of `run-baseline.sh` before starting any rep, failing loudly on a mismatch. This is currently **`k6 v2.0.0`**: `baseline.js`'s inter-phase drain gap relies on VU pool-reuse scheduling behavior that was only ever confirmed live against that exact version (see that file's header) — an unpinned/silently-upgraded k6 could change that behavior without anything else in this harness noticing. See `k6/shell.nix` for the pinned revision and how to deliberately bump it.

**Re-rendering a findings doc without a live rerun:** `--render-only RUN_ID` skips reset/docker/k6 entirely and re-runs just the aggregation + findings-doc render step against an existing `tools/simulation/monitor/out/<RUN_ID>/rep-*/` tree (e.g. after changing `TEMPLATE.md` or the render logic itself, or to verify a rendering fix without re-running a multi-hour baseline):

```bash
bash tools/simulation/run-baseline.sh --render-only run-20260729T170130Z
```

Per rep: `reset.sh` (fresh stack + seed) → start `docker-stats-sampler.sh`
in the background + `pg-snapshot.sh start` → `k6 run baseline.js` (own
`RUN_ID`/`REP`/`SUMMARY_OUT`) → `pg-snapshot.sh end` + stop the sampler →
collect everything into `tools/simulation/monitor/out/<run-id>/rep-<n>/`
(`summary.json`, `manifest.json`, `docker-stats.csv`,
`pg-snapshot-{start,end}-*.txt`, `k6.log`, `meta.json`). After all reps: an
`aggregate.json` lands next to the rep dirs, and a findings doc renders from
`tools/simulation/findings/TEMPLATE.md` to
`tools/simulation/findings/<run-id>-findings.md`.

**Dev (default) vs. real (#243 Task 10) params:** this script does not
redeclare or override any `WARMUP_*`/`CAPACITY_*`/`BASE_URL` default — those
belong to `k6/baseline.js` alone (20s warmup + 2m capacity by default), and
are simply inherited by the k6 subprocess like any other exported env var,
so there is exactly one place that owns them:

```bash
# Fast dev/verification cycle (short durations):
REPS=2 WARMUP_DURATION=10s CAPACITY_DURATION=25s bash tools/simulation/run-baseline.sh

# Real #243 Task 10 run (capacity duration >= 2x the 15-min access-token
# life, so the refresh path is actually exercised under load):
REPS=3 CAPACITY_DURATION=40m bash tools/simulation/run-baseline.sh
```

Other env vars the orchestrator itself reads directly: `RUN_ID` (default
`run-<UTC timestamp>`), `SAMPLER_INTERVAL` (`docker-stats-sampler.sh`
`--interval`, default 2s), `PG_TOP_N` (`pg-snapshot.sh end --top`, default
20).

**#243 Task 9 MUST-FIX — all 10 cast users / all 5 personas in capacity:**
`baseline.js` now defaults `CAPACITY_VUS` to the full cast size (10 with the
default 1 Owner / 1 Manager / 1 Sales / 3 Worker / 4 ReadOnly split — derived
from the cast, not a hardcoded literal, so it can never drift from whatever
`bootstrap.sh` actually generated), made collision-free by an
**inter-phase drain gap** — `capacity.startTime` is pushed past warmup's
entire window (duration + `gracefulStop` + a buffer), which was empirically
confirmed (live, repeated probes against this k6 version) to make k6 reuse
one VU pool sized to `max(WARMUP_VUS, CAPACITY_VUS)` instead of needing
extra concurrent slots that could hand capacity two VU ids aliased to the
same cast user. See `k6/baseline.js`'s file header for the full mechanics.
`run-baseline.sh` verifies this on every run: it parses each rep's
`summary.json` `capacity.byPersona` and reports (both on stdout and in the
findings doc's "Persona coverage" section) whether all 5 roles produced
capacity-phase requests — a missing persona in any rep exits non-zero and is
flagged prominently, never silently dropped. `CAPACITY_VUS` must equal the
cast size exactly (`baseline.js`'s `setup()` hard-errors otherwise, and
`run-baseline.sh` preflights the same check before starting any rep) — that
equality is what the per-role check above relies on, and role-level coverage
alone can pass while still missing (or double-covering) an individual user,
so PR #279 added a **per-cast-user** check one level stricter than
per-role: `capacity.byCastUser` (also in `summary.json`) records, per
distinct cast user, exactly how many capacity VUs claimed them — the
healthy value is 1 for every one of the `castSize` users; 0 means that user
never got a capacity VU, >1 means two different VUs collided on the same
user (the exact failure the drain gap exists to prevent). Surfaced the same
way as persona coverage: on stdout, in `summary.json`
(`capacity.castUserCoverageOk`), and in the findings doc's "Cast-user
coverage" section.

**Honesty rules baked into the findings doc:** ±10% p95 variance across reps
is reported as an **observation**, never a pass/fail gate — this box is a
noisy, co-located, uncapped shared machine. A rep with `unexpected_status >
0` or `checks < 100%` is flagged but its data is always published, never
silently dropped. The doc states plainly, at the top, that this is an
**uncapped, co-located, non-sizing shakeout**, carries forward the **full
deviation list**, and has a prominent **"Could NOT measure"** section
(absolute prod throughput/latency, #269 DB-retry need, pool sizing vs prod,
#273 telemetry volume) — it never presents a liftable absolute
throughput/latency figure as production capacity.

## Files

- `bootstrap.sh` — idempotent secret generator. `--force` regenerates the RSA
  keypair and all passwords (then re-run `reset.sh` so the app picks them
  up). Owns two git-ignored artifacts:
  - `.env.sim` — everything the `app` service in `docker-compose.sim.yml`
    needs, via `--env-file` interpolation.
  - `.sim-cast.json` — the k6/Playwright **login source**: the Owner plus
    every deterministic cast member, with role and password. This is **not**
    the same thing as `out/manifest.json` (the seeder's own row-count/
    completion manifest, written by the app itself).
- `docker-compose.sim.yml` — the self-contained sim stack (see above; also
  defines `otel-collector`).
- `reset.sh` — `down -v` (guarded to the `cluckwork-sim` project) → `config
  -q` → `up -d --build` → poll `/health/ready` → preflight (plain HTTP `/` is
  200 not 307, `pg_stat_statements` query works) → one-shot `docker compose
  run --rm -e ASPNETCORE_ENVIRONMENT=Development app seed --profile
  simulation` (#279 — the serving `app` stays Production the whole time) →
  preflight (`out/manifest.json` is `complete` with the expected user
  count).
- `otel/collector.yaml` — the local OTLP collector's config (see
  "Monitoring").
- `monitor/docker-stats-sampler.sh`, `monitor/pg-snapshot.sh` — the
  container-resource and Postgres snapshotters (see "Monitoring").
- `run-baseline.sh` — the multi-rep baseline orchestrator (see "Baseline
  orchestrator" above).
- `findings/TEMPLATE.md` — the checked-in findings-doc template
  `run-baseline.sh` renders every run's data into. Generated docs land at
  `findings/<run-id>-findings.md` — git-ignored (see `.gitignore` below);
  only the template itself is tracked.
- `.gitignore` — `.env.sim`, `.sim-cast.json`, `out/`, `*.pem`, and
  generated findings docs (`findings/*.md`, except `findings/TEMPLATE.md`)
  never get committed. `out/` matches at any depth (git's unanchored
  `.gitignore` semantics), so `monitor/out/` (the monitoring scripts' own
  output directory, including every `run-baseline.sh` run) is covered too.
  The secrets/output half is **separately** mirrored in the root
  `.dockerignore` — Docker's ignore-pattern matching is NOT the same as
  git's: a bare `tools/simulation/out/` there only anchors that exact path,
  not `tools/simulation/monitor/out/` (a sibling dir), so `.dockerignore`
  lists both explicitly (plus the findings glob, same `*.md`/`!TEMPLATE.md`
  pair as here) rather than relying on one pattern to cover both trees.

## Verifying without a full bring-up

A full `up --build` is slow and this environment can be flaky, so day-to-day
changes to the compose file or `.env.sim` should be checked with:

```bash
docker compose -p cluckwork-sim --env-file tools/simulation/.env.sim \
  -f tools/simulation/docker-compose.sim.yml config -q
```

This validates the fully merged/interpolated config (catches YAML errors,
undefined `${VAR}` references, and bad values) without pulling images or
starting anything. Only run `reset.sh` (the real bring-up) when you actually
need a live sim stack.
