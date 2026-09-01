# Runbook: loading the simulation fixture into a local debug database

**When to use this:** you are debugging locally and the screens are too empty to
be useful — `seed --profile demo` gives you a one-person farm with a handful of
rows, and you want hundreds of flocks and customers, and months or years of
production history, in the database your IDE-run API is already pointed at.

**Not this runbook:**

- [`tools/simulation/README.md`](../../tools/simulation/README.md) — the #243
  load-test harness. Same fixture, but in its own throwaway `cluckwork-sim`
  compose stack on `:8081`, driven by `reset.sh`. If you want k6, Playwright or
  a findings doc, go there and **do not** use this runbook. `reset.sh` and
  `run-baseline.sh` `down -v` their own stack and must never be aimed at a
  debug database.
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

Note what that does *not* say. A failed run is not automatically a wipe: the
`HistoryDays` ceiling in [step 2](#2-choose-your-depth) fails with the rows
already correct and is cleared by re-running. Only a **polluted account** —
[step 1](#1-confirm-the-target-database-is-clean-and-is-the-one-you-mean) —
forces the destructive path.

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
| **107 (see the ceiling below)** | 214 | 1050 | 20 | 19 | 102 | 101 |
| 365 | 730 | 3630 | 56 | 55 | 102 | 101 |
| 730 | 1460 | 7280 | 109 | 108 | 102 | 101 |

If what you actually need is a long picker list or a paginated table, note the
last two columns: **102 flocks and 101 customers ship at every depth**, so the
default 90 already gives you those. Raise `HistoryDays` only when you need
history — reports, exports, the lot ledger, an old slice of the sales window.

> **Known ceiling: one seed cannot go past `HistoryDays = 107` from a clean
> database.** `SeedAsync` calls the daily-entry lock sweep once, and that sweep
> locks at most `BatchSize = 200` entries per run
> ([`DailyEntryLockSweep.cs`](../../src/Cluckwork.Infrastructure/Jobs/DailyEntryLockSweep.cs)),
> while `ExpectedLockedEntryCount` expects **every** eligible entry locked —
> `2 flocks × (HistoryDays − 7)`. Those agree only up to
> `HistoryDays = 107`. Above it the run ends with
> `dailyEntries.locked`/`dailyEntries.submitted` mismatched and exit `1`.
>
> This is a seeder defect, not a design limit — tracked in
> [#638](https://github.com/mforce/cluckwork/issues/638). Do not work around it
> by editing `BatchSize`; that is product code the background worker's behaviour
> depends on.

**Getting past it: ramp the depth.** The ceiling is per *run*, not per database.
Each run locks up to 200 entries, so a re-run at a deeper `HistoryDays` only has
to lock what the increase newly made eligible — `2 × (new − old)`. Keep each
step at **100 days or fewer** and every run converges green, on the same durable
anchor, to the same history you would have wanted in one shot:

```bash
for days in 90 190 290 390 490 590 690 730; do
  ASPNETCORE_ENVIRONMENT=Development \
  Simulation__CastPassword='choose-a-20-char-password' \
  Simulation__HistoryDays=$days \
  Simulation__TimeZoneId=America/Chicago \
    dotnet run --project src/Cluckwork.Api -- seed --profile simulation || break
done
```

Start from a clean database, and do not skip a step — a jump wider than 100 days
fails, and leaves its rows behind (see below).

**If you do overshoot, the rows are still written.** The check runs in
`EmitManifestAsync`, after everything is committed, and there is no transaction.
So a failed run gives you the volume anyway, at the cost of a red exit code, no
`manifest.json`, and entries past the first 200 left `Submitted` instead of
`Locked`. Harmless for hand debugging. Re-running the *identical* command locks
the next batch, so repeating it also walks the database green.

Count the passes from what is **still unlocked**, not from the depth:
`ceil((eligible − alreadyLocked) ÷ 200)`, where `eligible = 2 × (HistoryDays − 7)`.
Every pass locks 200 except the last, which locks the remainder. Seeding 400 days
into an empty database needs `ceil(786 ÷ 200)` = 4 passes (200, 200, 200, 186);
reaching the same depth from an existing 90-day fixture does not, because those
166 entries are already locked — see the drill.

> Both recovery paths above are read off the code (the seeder's per-entity
> idempotency, the durable anchor, and one sweep pass per invocation), not yet
> observed on a real run — **Last drilled** is `not recorded`. The drill below
> exercises them; update that field once you have.

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
you later point k6 or Playwright at this database.** For hand debugging they can
be anything.

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
| Only `dailyEntries.locked` / `dailyEntries.submitted` mismatch, on a clean database | The `HistoryDays > 107` ceiling. The rows are written anyway; see the box in step 2. |
| `Simulation seed prerequisites missing: … no user in the Owner role` | Run `bootstrap-admin` first. |
| `Simulation seed prerequisites missing: … Owner role is held only by DISABLED user(s)` | No in-product repair; the message names the direct database fix. |
| `Simulation seeding is not available in Production` | `ASPNETCORE_ENVIRONMENT` unset or Production. |
| Boot fails on the TLS floor | Same cause — unset `ASPNETCORE_ENVIRONMENT` against a plaintext Postgres (#261/#262). |
| `28P01: password authentication failed for user "cluckwork"` | Form A against the Aspire stack. Use form B. |
| Exit `0`, but the screens you are debugging are still empty | Form A while an AppHost is up: it seeded the Compose database instead. Use form B. |
| The web server starts instead of seeding | The binary was repeated in front of the verb, or `--` was omitted. |

## Drill

Safe on a scratch database only.

1. `docker compose -f deploy/docker-compose.dev.yml down -v && docker compose -f deploy/docker-compose.dev.yml up -d`
2. `bootstrap-admin` (form 3 of [`first-admin-provisioning.md`](first-admin-provisioning.md)),
   and set a password.
3. Run form A with `Simulation__HistoryDays=90`. Expected: `Simulation data
   seeded (fingerprint …)`, exit `0`.
4. Run the identical command again. Expected: `Simulation seed already present;
   converged (fingerprint …)`, exit `0`, the same fingerprint.
5. Run it with `Simulation__HistoryDays=400` — a 310-day jump, well over the
   100-day step. Expected: exit `1`, and the *only* mismatched lines are
   `dailyEntries.locked` / `dailyEntries.submitted`. Anything else mismatching
   means the account was not clean and this drill proved nothing.
6. Run the identical `HistoryDays=400` command again, repeatedly. Expected: the
   `locked` figure climbs by **up to** 200 a run until it exits `0` — the
   recovery path in step 2's box. Count from where step 5 left it, **not** from
   the depth: step 3 already locked `2 × (90 − 7)` = 166, step 5's own sweep
   locked 200 more, so of the `2 × (400 − 7)` = 786 eligible, 420 remain and
   the reruns go **200, 200, 20**. The 186-entry final pass in step 2's box is
   the *from-empty* case and does not apply here.
7. Sign in and walk the four **Verify** checks.
8. Update **Last drilled** above.
