# Runbook: loading the simulation fixture into a local debug database

**When to use this:** you are debugging locally and the screens are too empty to
be useful — `seed --profile demo` gives you a one-person farm with a handful of
rows, and you want hundreds of flocks and customers, and months or years of
production history, in the database your IDE-run API is already pointed at.

**Not this runbook:**

- [`tools/simulation/README.md`](../../tools/simulation/README.md) — the #243
  load-test harness. Same fixture, but in its own throwaway `cluckwork-sim`
  compose stack on `:8081`, driven by `reset.sh`. If you want a findings doc,
  the `docker stats`/Postgres samplers or a Playwright canary, go there.
  `reset.sh` and `run-baseline.sh` `down -v` their own stack and must never be
  aimed at a debug database. Pointing `k6 run` itself at the database this
  runbook seeds *is* supported — see
  [Preparing this database for k6](#preparing-this-database-for-k6).
- [`first-admin-provisioning.md`](first-admin-provisioning.md) — the database
  has no Owner. Do that first; this runbook needs one.
- `seed --profile demo` ([280](../decisions/280-seed-and-simulation.md)) — the
  small, look-at-it fixture. Prefer it when you do not need volume.

**Blast radius:** the target database's default account, permanently. The seed
writes ~100 flocks, ~100 customers and `2 × HistoryDays` daily entries with
their egg lots, orders and expenses, attributed to a generated cast of
`sim-*` users at `Simulation__EmailDomain`. It runs in **no transaction** and
does **no partial-seed cleanup**, so a run that fails validation still leaves
every row it wrote, and **removing** the fixture means wiping the database.

Note what that does *not* say. A failed run is not automatically a wipe — a
prerequisite failure exits before writing a row at all. The one failure that
does force the destructive path is a **polluted account**,
[step 1](#1-confirm-the-target-database-is-clean-and-is-the-one-you-mean).

**Prerequisites:**

- .NET 10 SDK, and a running local PostgreSQL (Compose dev stack or Aspire).
- An **Owner** in the default account — `bootstrap-admin`, per
  [`first-admin-provisioning.md`](first-admin-provisioning.md). Without one the
  seed exits `1` with `Simulation seed prerequisites missing:` before writing a
  row.
- A database whose default account holds **no other data**. See
  [step 1](#1-confirm-the-target-database-is-clean-and-is-the-one-you-mean).

**Last drilled:** not recorded.

---

## Procedure

### 1. Confirm the target database is clean, and is the one you mean

Two distinct traps, and both are silent.

**Which database.** `seed` is a run-then-exit process, so it resolves
`ConnectionStrings:Default` from the **API's** user-secrets — which name the
Compose dev stack. Under Aspire that is the wrong database, and if Compose is
*also* up you get a successful-looking seed into the stack you were not looking
at. The failure shapes are enumerated in
[`first-admin-provisioning.md` §4](first-admin-provisioning.md#4-aspire-apphost-stack);
they apply verbatim here. Form B below states the target explicitly rather than
inheriting it.

```bash
docker ps --format '{{.Names}}\t{{.Ports}}' | grep -i postgres

# Host/port/database only. Do NOT pipe the raw `user-secrets list` to the
# terminal — the value carries the password, and scrollback keeps it.
dotnet user-secrets --project src/Cluckwork.Api list \
  | sed -n 's/^ConnectionStrings:Default = //p' \
  | tr ';' '\n' | grep -iE '^(host|port|database)='
```

**Whether it is clean.** `SimulationDataSeeder` validates **exact** row counts
across the whole account, not just its own rows. Anything else in the default
account — flocks you created by hand while debugging, a prior `demo` seed, a
prior simulation seed at a *different* `HistoryDays` — makes those counts
overshoot and the seed fails closed
([280](../decisions/280-seed-and-simulation.md)):

```text
Simulation seed failed: Simulation seed completion check failed — the seed is
short/partial and must NOT be marked complete: flocks: expected 102, got 105;
customers: expected 101, got 104; dailyEntries.total: expected 1460, got 1708; …
```

Every `got` above its `expected` is this, and only this — and **re-running does
not clear it.** The seeder keeps a durable date anchor in `SimulationSeedState`,
so a re-run against an *already clean and correctly seeded* database converges
to `AlreadySeeded`; against a polluted one the foreign rows are still there and
the same counts still overshoot. Only the lock-sweep mismatch in step 2 is
recoverable by repetition. This one needs a wipe.

If the account is not clean, wipe.

> **Destructive, and wider than the account you are fixing. This cannot be
> undone.** `down -v` removes the stack's named volumes, so it destroys the
> **entire `cluckwork-dev` PostgreSQL instance** — every farm, every user, every
> row, not merely the default account's fixture. Run it only against a
> throwaway development database. If anything in there matters, take a dump
> first ([`backup-and-restore.md`](backup-and-restore.md)) — there is no
> narrower reset, because the validation counts the whole account and a
> hand-pruned database is exactly the polluted state it rejects.

```bash
# Compose dev stack
docker compose -f deploy/docker-compose.dev.yml down -v
docker compose -f deploy/docker-compose.dev.yml up -d
```

For the Aspire stack, follow that runbook's reset procedure —
[Persistence and reset](aspire-local-development.md#persistence-and-reset) —
which resolves the current container and volume rather than a remembered name.

Either way, re-run `bootstrap-admin`: the wipe took the Owner with it.

### 2. Choose your depth

`Simulation:HistoryDays` is the volume dial. Everything time-series scales off
it; the flock and customer bands do not.

| `HistoryDays` | Daily entries | Egg lots | Orders | Expenses | Flocks | Customers |
|---|---|---|---|---|---|---|
| 12 (test fixture) | 24 | 100 | 6 | 5 | 102 | 101 |
| 90 (default) | 180 | 880 | 17 | 16 | 102 | 101 |
| 365 | 730 | 3630 | 56 | 55 | 102 | 101 |
| 730 | 1460 | 7280 | 109 | 108 | 102 | 101 |

If what you actually need is a long picker list or a paginated table, note the
last two columns: **102 flocks and 101 customers ship at every depth**, so the
default 90 already gives you those. Raise `HistoryDays` only when you need
history — reports, exports, the lot ledger, an old slice of the sales window.

**There is no depth ceiling, and no ramping.** Ask for the depth you want in
one run. Until #638 there was one at `HistoryDays = 107`: the seeder ran the
daily-entry lock sweep once, that sweep locks at most `BatchSize` entries per
pass ([`DailyEntryLockSweep.cs`](../../src/Cluckwork.Infrastructure/Jobs/DailyEntryLockSweep.cs)),
and the completion check expects **every** eligible entry locked — so above
that line the seed could not validate on any database, however clean. The
seeder now drains the sweep instead, and
`SimulationDeepSeedDrainTests` holds it there.

What is left is only cost. The production-history loop is
O(`HistoryDays` × flocks) real handler round-trips, so a 730-day fixture takes
appreciably longer to build than a 90-day one and every row of it lands in your
debug database. Depth is free to ask for, not free to make.

### 3a. Form A — Compose dev database, API run from the IDE / CLI

```bash
docker compose -f deploy/docker-compose.dev.yml up -d

ASPNETCORE_ENVIRONMENT=Development \
Simulation__CastPassword='choose-a-20-char-password' \
Simulation__HistoryDays=90 \
Simulation__TimeZoneId=America/Chicago \
  dotnet run --project src/Cluckwork.Api -- seed --profile simulation
```

Expected, on exit `0`:

```text
Simulation data seeded (fingerprint <hex>).
```

Note the `--` separator: it stops `dotnet run` consuming the arguments.

`ASPNETCORE_ENVIRONMENT=Development` is **required twice over**: unset means
Production, which fails the boot against a plaintext local Postgres (the TLS
floor, #261/#262), and `SimulationDataSeeder` is registered only outside
Production, so a Production process refuses the profile outright with
`Simulation seeding is not available in Production`.

The verb migrates the schema itself before seeding — no separate `migrate` run.

This form reads the connection string from the API's user-secrets. It is the
wrong form for an Aspire stack — see form B.

### 3b. Form B — Aspire AppHost stack

Leave the AppHost **running** — its container *is* the database — and state the
target explicitly:

```bash
# Password: generated once by Aspire and stored in the APPHOST's user-secrets,
# not the API's. Username: postgres. Port: LocalPorts:Postgres (see the
# host-ports table in the Aspire runbook).
pg_password=$(dotnet user-secrets --project src/Cluckwork.AppHost list \
  | sed -n 's/^Parameters:postgres-password = //p')
[ -n "$pg_password" ]

ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__Default="Host=localhost;Port=<LocalPorts:Postgres>;Database=cluckwork;Username=postgres;Password=$pg_password" \
Simulation__CastPassword='choose-a-20-char-password' \
Simulation__HistoryDays=90 \
Simulation__TimeZoneId=America/Chicago \
  dotnet run --project src/Cluckwork.Api -- seed --profile simulation
```

Substitute the port this run is actually using; `LocalPorts:Postgres` is
overridable per machine and an unparseable value returns Aspire to a random host
port. The environment variable outranks user-secrets for this one process, and
nothing is written to either store.

The two stacks are **separate databases** (#565). A fixture seeded through form
A does not exist for form B, and vice versa.

### 4. The knobs

All bind from the `Simulation` section
([`SimulationOptions.cs`](../../src/Cluckwork.Infrastructure/Identity/SimulationOptions.cs));
as environment variables the separator is `__`.

| Key | Default | What it does |
|---|---|---|
| `Simulation__CastPassword` | *(none — required)* | Shared password for every `sim-*` cast member at whatever `Simulation__EmailDomain` is set to. Choose one at run time; never commit it. |
| `Simulation__HistoryDays` | `90` | Depth of production history. See the table and the ceiling above. |
| `Simulation__Managers` | `1` | Cast size, on top of the existing Owner. Managers place flocks and create products/categories/expenses. |
| `Simulation__Sales` | `1` | Books the orders. |
| `Simulation__Workers` | `3` | Records the daily entries. One is deliberately restricted to a single flock (#500). |
| `Simulation__ReadOnly` | `4` | Read-only personas. |
| `Simulation__TimeZoneId` | `America/New_York` | The farm's IANA zone. Pick a non-UTC one so date handling is actually exercised. |
| `Simulation__EmailDomain` | `sim.local` | Cast email domain. |
| `Simulation__Seed` | `243` | Determinism seed; folded into the manifest fingerprint. |
| `Simulation__CredentialOutputPath` | *(unset)* | Where to write the completion manifest (row counts + lifecycle matrix, **not** credentials). Unset skips the file; validation still runs. |

Expected counts are derived from these options, not hardcoded, so a non-default
cast validates. Only `HistoryDays` 12 and 90 are covered by tests, though —
treat anything far from those as untested, not as broken.

**The cast counts must match `tools/simulation/.sim-cast.json` (git-ignored; generated by [`bootstrap.sh`](../../tools/simulation/bootstrap.sh)) if
you later point k6 or Playwright at this database** — and so must
`Simulation__CastPassword` and `Simulation__EmailDomain`. Take all of them from
`.env.sim` rather than choosing them; the full procedure is
[Preparing this database for k6](#preparing-this-database-for-k6). For hand
debugging they can be anything.

## Verify

Not "it exited 0" — log in and look:

1. Sign in as the Owner — the address `bootstrap-admin` printed, which is
   whatever you passed to its `--email`, **not** a `sim.local` one — or as any
   cast member: `sim-worker-1@`, `sim-manager-1@`, `sim-sales-1@` at
   `Simulation__EmailDomain` (default `sim.local`), with your `CastPassword`.
   Either way you also need the farm code.
2. Flocks list paginates — 102 rows, not 2.
3. History shows entries across the whole window, in all three lifecycle states,
   with **two** provenance shapes: "created and last changed by the same person"
   and "created by X, last changed by Y" (#494/#500).
4. A production or sales report over an **old** slice of the window returns rows,
   not an empty set.

## If it fails

| Symptom | Cause |
|---|---|
| `expected N, got M` with every `M > N` | The account is not clean. Wipe — [step 1](#1-confirm-the-target-database-is-clean-and-is-the-one-you-mean). |
| Only `dailyEntries.locked` / `dailyEntries.submitted` mismatch, on a clean database | Was the pre-#638 depth ceiling; it should be unreachable now. If you see it, the seeder's lock-sweep drain has regressed — file it rather than ramping the depth around it, and do not raise `BatchSize`. |
| `Simulation seed prerequisites missing: … no user in the Owner role` | Run `bootstrap-admin` first. |
| `Simulation seed prerequisites missing: … Owner role is held only by DISABLED user(s)` | No in-product repair; the message names the direct database fix. |
| `Simulation seeding is not available in Production` | `ASPNETCORE_ENVIRONMENT` unset or Production. |
| Boot fails on the TLS floor | Same cause — unset `ASPNETCORE_ENVIRONMENT` against a plaintext Postgres (#261/#262). |
| `28P01: password authentication failed for user "cluckwork"` | Form A against the Aspire stack. Use form B. |
| Exit `0`, but the screens you are debugging are still empty | Form A while an AppHost is up: it seeded the Compose database instead. Use form B. |
| The web server starts instead of seeding | The binary was repeated in front of the verb, or `--` was omitted. |

## Preparing this database for k6

The [`tools/simulation/k6/`](../../tools/simulation/k6/) scripts normally run
against the throwaway `cluckwork-sim` stack on `:8081`. They can run against
the database this runbook just seeded instead: every script routes its URLs
through one `BASE_URL`
([`k6/config.js`](../../tools/simulation/k6/config.js)), so re-aiming them is
one environment variable. What is *not* one environment variable is the
fixture underneath — this section is the rest of it.

> **`reset.sh` and `run-baseline.sh` are still off-limits.** Both `down -v`
> the `cluckwork-sim` project — `run-baseline.sh` calls `reset.sh` once per
> rep — and neither has any idea your debug database exists. Against a dev
> database you invoke `k6 run` directly, and you get k6's own numbers only:
> no `docker stats` sampler, no `pg_stat_statements` snapshot, no findings
> doc. For any of those, use the harness stack as designed.

**Blast radius, on top of the seed's own.** The personas write: Manager,
Sales and Worker create daily entries, draft orders and expenses. Those rows
push the account past the exact counts
[step 1](#1-confirm-the-target-database-is-clean-and-is-the-one-you-mean)
validates, so once k6 has run, the next `seed --profile simulation` against
that database fails closed. That is recoverable only by a wipe — the same
trap, reached from the other direction.

### What has to line up

k6 logs in as the ten users in `tools/simulation/.sim-cast.json`: the Owner
`admin@<EmailDomain>` plus the nine cast members. `CAPACITY_VUS` defaults to
10 and assigns one VU per user, so a missing or unusable Owner is a failed
run, not a degraded one.

| Requirement | Where it comes from |
|---|---|
| Cast emails and their shared password | `Simulation__CastPassword`, `Simulation__EmailDomain` and the four cast counts must be **the values `bootstrap.sh` generated**, not the hand-chosen password [step 3a](#3a-form-a--compose-dev-database-api-run-from-the-ide--cli) tells you to pick |
| An Owner at `admin@<EmailDomain>`, usable | `bootstrap-admin` at that exact address, **then rotated off `MustChangePassword`** — [step k2](#k2-owner-create-and-rotate) |
| `farmCode: "default-farm"` | Hardcoded in [`k6/auth.js`](../../tools/simulation/k6/auth.js). It is the default account's slug, set by the `AddAccountSlug` migration, so any base-seeded database already matches — nothing to do |
| Rate limits raised past production values | [step k4](#k4-raise-the-rate-limits) |

### k1. Generate the cast — `bootstrap.sh`

Safe to run from here: it starts no container and names no compose project.
It writes exactly two git-ignored files —
`tools/simulation/.env.sim` and `tools/simulation/.sim-cast.json` — and exits
early if `.env.sim` already exists.

```bash
bash tools/simulation/bootstrap.sh          # no-op if .env.sim is already there
bash tools/simulation/bootstrap.sh --force  # new keypair + new passwords
```

`--force` invalidates the credentials any *existing* fixture was seeded with,
in this database and in the sim stack. Both then need a wipe and a reseed.

**Take only the `Simulation__*` and `SIM_ADMIN_*` lines out of `.env.sim`.**
The rest of that file configures the sim stack's own containers — its
Postgres credentials, its JWT keypair, `AllowedHosts=cluckwork-sim.local`,
`ASPNETCORE_URLS=http://+:8080` — and feeding those to a dev API breaks it.
The two `SIM_ADMIN_*` keys deliberately carry no `__`, so they are script
values rather than app config (see `bootstrap.sh`'s own comment); this
runbook reads them the same way `reset.sh` does.

```bash
env_val() { sed -n "s/^$1=//p" tools/simulation/.env.sim; }
```

### k2. Owner: create and rotate

**This runs before the seed, not after it.** `seed --profile simulation`
refuses to write a row unless the default account already holds an Owner
(#500), and `bootstrap-admin` creates only the *first* one — against an
account that already has an Owner it is a silent no-op (#283), so an Owner
minted at any other address permanently blocks the one k6 needs. Create it
at the address `.sim-cast.json` carries, on a database whose default account
has no Owner yet ([step 1](#1-confirm-the-target-database-is-clean-and-is-the-one-you-mean)
leaves it that way). This is the order `reset.sh` uses.

Then rotate off the printed one-time password — until you do,
`MustChangePasswordMiddleware` 403s every request the Owner VU makes (#283),
and the run fails with the fixture looking fine.

```bash
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/Cluckwork.Api -- bootstrap-admin --email "$(env_val SIM_ADMIN_EMAIL)"
```

Under Aspire, prefix that with the same explicit `ConnectionStrings__Default`
as above — it is a one-shot verb with exactly the #565 problem.

`bootstrap-admin` prints `Temporary password: <value>` on stdout by design.
**Do not echo it anywhere it will be retained** — a shell history or a CI log
outlives the rotation (PR #392 review).

Then, with the API *serving* against the same database, log in with the
temporary password and change it to `SIM_ADMIN_PASSWORD`. That is exactly
what `reset.sh` does after its own `bootstrap-admin` call — reuse the Python
block there (`login` then `change-password`, `farmCode: "default-farm"`,
`Idempotency-Key` on the write) rather than reimplementing it, pointing
`APP_PORT` at whatever your dev API is listening on. Under Aspire the API
`aspire run` launched is already serving against the right database; for the
Compose form, start it yourself with an explicit `ASPNETCORE_URLS`.

### k3. Seed with those values

Run [step 3a](#3a-form-a--compose-dev-database-api-run-from-the-ide--cli) or
[3b](#3b-form-b--aspire-apphost-stack) unchanged, except that every
`Simulation__*` value comes from `.env.sim` instead of being chosen:

```bash
ASPNETCORE_ENVIRONMENT=Development \
Simulation__CastPassword="$(env_val Simulation__CastPassword)" \
Simulation__EmailDomain="$(env_val Simulation__EmailDomain)" \
Simulation__Managers="$(env_val Simulation__Managers)" \
Simulation__Sales="$(env_val Simulation__Sales)" \
Simulation__Workers="$(env_val Simulation__Workers)" \
Simulation__ReadOnly="$(env_val Simulation__ReadOnly)" \
Simulation__HistoryDays="$(env_val Simulation__HistoryDays)" \
Simulation__TimeZoneId="$(env_val Simulation__TimeZoneId)" \
Simulation__Seed="$(env_val Simulation__Seed)" \
  dotnet run --project src/Cluckwork.Api -- seed --profile simulation
```

For the Aspire stack, add form B's explicit
`ConnectionStrings__Default=...` to that same command — a one-shot verb
resolves the API's own user-secrets otherwise and seeds the *Compose*
database (#565).

The cast counts in `.env.sim` are `bootstrap.sh`'s copy of
`SimulationOptions`' defaults, so passing them explicitly is belt and
braces — but `Simulation__CastPassword` genuinely differs, and it is the one
that decides whether k6 can log in.

### k4. Raise the rate limits

Production defaults are `Login` 10 per 900s, `Refresh` 60 per 900s,
`ClientErrors` 10 per 300s
([`RateLimitingOptions.cs`](../../src/Cluckwork.Api/RateLimiting/RateLimitingOptions.cs)).
A baseline rep logs in 12 times before it starts, so it hits the login wall
during warmup and the run is measuring the limiter. `.env.sim` raises all
three to 1,000,000 for exactly this reason.

Set them where **both** launch paths pick them up — the API's user-secrets.
The AppHost injects only the connection string and the Redis key (#565), so
these still bind under `aspire run`:

```bash
dotnet user-secrets --project src/Cluckwork.Api set "RateLimiting:Login:PermitLimit" "1000000"
dotnet user-secrets --project src/Cluckwork.Api set "RateLimiting:Refresh:PermitLimit" "1000000"
dotnet user-secrets --project src/Cluckwork.Api set "RateLimiting:ClientErrors:PermitLimit" "1000000"
```

Restart the API afterwards, and **remove them when you are done** — a debug
box with no login limiter is not what you want to keep testing against:

```bash
dotnet user-secrets --project src/Cluckwork.Api remove "RateLimiting:Login:PermitLimit"
dotnet user-secrets --project src/Cluckwork.Api remove "RateLimiting:Refresh:PermitLimit"
dotnet user-secrets --project src/Cluckwork.Api remove "RateLimiting:ClientErrors:PermitLimit"
```

### k5. Run k6

`BASE_URL` is the only thing that moves. Start with the smoke script, which
fails loudly on anything the fixture got wrong, before spending a baseline
on it:

```bash
export BASE_URL=http://127.0.0.1:8080     # Aspire's LocalPorts:Api default

k6 run tools/simulation/k6/auth-smoke.js
k6 run --vus 5 --duration 20s tools/simulation/k6/persona-smoke.js
CAPACITY_DURATION=2m k6 run tools/simulation/k6/baseline.js
```

`8080` is the Aspire `api` resource's committed `LocalPorts:Api` default, and
it is overridable per machine — take the port the run actually used from the
dashboard ([Aspire runbook](aspire-local-development.md)). For the Compose
form it is whatever `ASPNETCORE_URLS` you started the API with. Either way it
is the **API**, not Vite on `5173`: the SPA dev server proxies `/api` but
serves none of the endpoints k6 measures.

### Known gap: the `staticAssets` flow 404s

`app.MapFallbackToFile("index.html", …)` is a **no-op in dev** — there is no
`wwwroot`, because a dev SPA is served by Vite
([`Program.cs`](../../src/Cluckwork.Api/Program.cs), the comment above the
fallback). k6's `staticAssets` flow fetches `/` and a hashed asset off it, so
against a dev-run API it fails two checks per iteration and bumps
`unexpected_status` — a signal `persona-smoke.js` asserts must be zero.

Two honest options, and neither is "ignore it":

- **Read the numbers per flow.** Every metric is tagged `flow:staticAssets`,
  so the API percentiles are still usable with that flow excluded. The
  smoke scripts' own pass/fail is not — treat them as red for a reason.
- **Give the API a `wwwroot`,** which is what the container does
  ([`Dockerfile`](../../src/Cluckwork.Api/Dockerfile) copies `web/dist` into
  it):

  ```bash
  (cd web && npm run build)
  mkdir -p src/Cluckwork.Api/wwwroot && cp -r web/dist/. src/Cluckwork.Api/wwwroot/
  ```

  `src/Cluckwork.Api/wwwroot/` is **not** git-ignored and does not exist on a
  clean checkout, so delete it when you are finished rather than leaving a
  built SPA loose in the working tree.

## Drill

Safe on a scratch database only.

1. `docker compose -f deploy/docker-compose.dev.yml down -v && docker compose -f deploy/docker-compose.dev.yml up -d`
2. `bootstrap-admin` (form 3 of [`first-admin-provisioning.md`](first-admin-provisioning.md)),
   and set a password.
3. Run form A with `Simulation__HistoryDays=90`. Expected: `Simulation data
   seeded (fingerprint …)`, exit `0`.
4. Run the identical command again. Expected: `Simulation seed already present;
   converged (fingerprint …)`, exit `0`, the same fingerprint.
5. Run it with `Simulation__HistoryDays=400` — a 310-day jump, far past the
   old ceiling, and past one sweep batch several times over. Expected: exit
   `0`, a different fingerprint from step 4 (it covers the counts, which the
   depth moves), and about `2 × (400 − 7) = 786` daily entries `Locked` rather
   than 200 — a day either way, depending on how farm-local "today" skews
   against the UTC seed anchor. This is the step that would have gone red
   before #638.
6. Sign in and walk the four **Verify** checks.
7. Update **Last drilled** above.
