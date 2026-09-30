# #863: shared Postgres server spike

**Base:** `af3eb471b336071ccea904f6e2d8d6b3f48eeaf4` (the #1000 merge). The worktree HEAD equaled `origin/main` before measurement. **Date:** 2026-09-30. All local runs used the Release integration project, real Postgres through Testcontainers, the same 12-core host, and `tools/test-timing/measure.py`. Raw timing logs and TRX files are under `/tmp/cluckwork-863-*` on that host.

## S1: advisory-lock gate

The app's worker takes the two-integer `(271, 1)` advisory lock. Against the pinned real Postgres image, a holder in database A blocked another session in A, while a session in database B acquired the same two-integer key. Reversing A and B gave the same result. Advisory-lock contention is scoped to a database within one server, so hosts with distinct databases do not compete for the worker's lease.

The first-run provisioning lock uses `(283, 1)`. With that lock held in A, the scoped `pg_locks` query returned 1 in A. An unscoped query made from B also saw 1; the query scoped to B's database OID returned 0. This is why `HappyPathStillReleasesTheLockTests` now limits its assertion to its own database. The old query counted every database's `(283, 1)` lock in the server; that was precise only while each fixture owned a server. The new query counts granted two-integer `(283, 1)` locks in the test's database. It cannot be confused by another database's lock.

I checked that the narrower query is not vacuous. In a temporary mutation, a second connection held `(283, 1)` in the same database after provisioning. The test failed `Expected: 0; Actual: 1`. After removing the mutation, the focused test passed. The committed test also acquires the key on another backend as a positive control, requires the query to return 1, unlocks it, then requires 0. No other changed test assertion depends on server-wide `pg_locks` state. `PostgresLeaderLeaseTests` still puts both competing leases in one dedicated database; its focused tests passed.

## S2: what the baseline actually starts

The earlier 138-container count was a count of **all** containers: 128 Postgres, nine Redis, and one Ryuk. Fixture timing lines identify 82 factory database starts and 81 subsequent host migration calls. Other tests create fresh Postgres instances directly, often to test an intermediate migration or a one-shot command. The migrated template removes 18-migration replay for factory databases. Direct migration tests still receive virgin databases and run their intended migration paths.

| Configuration | Local wall 1 | Local wall 2 | Local wall 3 | Spread | Postgres / all containers |
|---|---:|---:|---:|---:|---:|
| Base, before this spike | 388.7 s | 388.6 s | 384.4 s | 4.4 s | 128 / 138 |
| One server, database per fixture | 344.1 s | 343.8 s | 346.7 s | 3.0 s | 1 / 11 |

All six runs passed 1,873 tests. The base shared-collection spans were 371.1, 376.3, and 372.0 seconds; the one-server spans were 333.0, 333.9, and 332.9 seconds. In both configurations the shared collection finished within a second of the process end. The local wall ranges do not overlap: the one-server path saved 37.7–45.0 seconds across the observed runs, roughly 10–12%. This is a wall-clock result, not the sum of container startup intervals, which overlap other work.

The final staged A-only source, including the lock query's positive control and the image-pin fix, passed all 1,873 tests in a further 337.6-second full run with 11 containers. It is a validation run, kept separate from the three-run configuration comparison above.

The earlier 390–606 second spread in `01-measurement.md` came from CI, not this local host. The stable local baseline makes this host a useful controlled comparison; it does not establish the same saving on CI. CI evidence for this branch belongs alongside, not inside, the local comparison.

## S3: database boundaries and resource checks

The test process starts one pinned Postgres Testcontainer. It migrates a template database once and closes that migration connection. Each factory then clones a GUID-named database from the template. A `MigrateSchemaOnInitialize == false` factory and direct migration tests instead get a virgin database copied from the built-in empty template; the #263 focused test passed. Each factory still has its own database and connection string. `DetachedTenantWriteTests` keeps both farms in its one database, and each `Version` race keeps its contenders together. `StealLossConnectionReleaseTests` retains its dedicated factory and one-slot app pool; its focused tests passed. The backing server is shared, but every test still executes real Postgres SQL through Testcontainers.

The server reported `max_connections = 100`. During the first full one-server run, a five-second sampler observed at most 39 client sessions across 13 simultaneously active databases. That is a sampled peak, not a hard upper bound; all three full runs passed without connection exhaustion at the default limit. No worker count or connection limit was raised.

The remaining server-wide test state was checked separately. `DmlOnlyRole` creates roles in the cluster, but each name has a new GUID; it grants privileges only in its own database. The other `pg_locks` checks filter by backend PID, and the `pg_stat_activity` blocking probes follow a specific holder PID. Those PIDs are unique within the server, so another database cannot satisfy their predicates. No test changes the server configuration.

A separate clone probe copied a 100,000-row template twenty times. Twenty sequential clones took 3.17 seconds; twenty concurrent clones took 1.08 seconds, with no failures. The clones therefore did not serialize end to end in this probe. Holding one session in the template caused a clone to fail with a source-database-in-use error. The harness closes its non-pooled template migration connection before any clone starts; all three full runs had no clone error. One server also shares CPU and buffers between fixtures, so the local wall and CI comparison, rather than clone microtiming, decides whether consolidation helps.

The staged-file image-pin guard initially rejected a plain default-database literal in the new helper. The helper now takes its admin connection string from Testcontainers, and the guard passes without an allow-list entry.

## S4: DemoSeed class split

The separate spike moved the farm-clock fact into its own class and factory. Both facts kept their assertions. This adds one database, and with the old infrastructure adds one Postgres container. The full-suite B-only runs passed all 1,873 tests at 393.5, 368.6, and 387.3 seconds (24.9-second spread; 139 containers). Their median, 387.3 seconds, is only 1.3 seconds below the unsplit base median of 388.6 seconds and lies inside the run-to-run variation. The shared collection finished last in all three B-only runs. The split overlaps the two seed facts, but does not reliably shorten the local suite wall.

| Configuration | Local wall times, s | Median, s | Spread, s | Shared spans, s |
|---|---|---:|---:|---|
| Base | 388.7, 388.6, 384.4 | 388.6 | 4.4 | 371.1, 376.3, 372.0 |
| A: one server | 344.1, 343.8, 346.7 | 344.1 | 3.0 | 333.0, 333.9, 332.9 |
| B: class split only | 393.5, 368.6, 387.3 | 387.3 | 24.9 | 374.5, 352.4, 374.5 |
| A+B | 352.7, 347.5, 350.9 | 350.9 | 5.2 | 338.3, 336.3, 336.5 |

All twelve full runs passed 1,873 tests. In every run the shared collection was the last substantive test work; its end was within a second of the process wall. With A, the unsplit `DemoSeedTests` finished at 204.7–206.7 seconds. With A+B, the two split classes both finished by 204 seconds. The split shortened that class queue a little, but the shared collection still ran until 347–352 seconds. A+B's median was 6.8 seconds **slower** than A's. The B-only median was 1.3 seconds faster than base, far inside its 24.9-second spread. These observations do not support a wall-clock saving from B on this host.

## S5: CI replication

The earlier [#1000 measurement](01-measurement.md#d7-repair-and-post-repair-measurement) recorded four integration `Test` steps on unchanged test code. I ran the integration job three times on this PR's code commit, [run 36683785345](https://github.com/mforce/cluckwork/actions/runs/36683785345), attempts 1–3. Every attempt passed 1,873 tests and created 11 containers. The jobs used the current Ubuntu 26.04 runner image. Step timestamps are rounded to seconds; VSTest reports its own interval.

| Code | CI `Test` step times, s | Median, s | Range, s | VSTest times, s |
|---|---|---:|---:|---|
| #1000, before A | 605, 606, 390, 596 | 600.5 | 390–606 | 559.8, 559.0, 363.9, 552.9 |
| This PR, A only | 535, 487, 424 | 487 | 424–535 | 483.3, 442.4, 385.7 |

The observed CI median is about 114 seconds lower, but the ranges overlap. These are small, unpaired samples on shared runners, so they support a likely improvement rather than a precise CI saving. They do establish that the one-server topology passed repeatedly on CI. The unsplit `DemoSeedTests` result appeared well before the shared collection's last result in the first two attempts, consistent with the local finding that B would not move the wall.

## Recommendation

**Ship A; leave `DemoSeedTests` together.** One Postgres server with a migrated template and a distinct database per fixture cut the local median wall by 44.5 seconds, or 11.5%, with non-overlapping three-run ranges and all tests green. Three CI passes show a lower median, with overlapping ranges and therefore less certainty about the amount saved there. The class split had no reliable wall benefit by itself and made the combined median slower, so its extra fixture does not belong in the change. Confidence is high in the local win and correctness boundary, moderate in a CI win, and low in any exact CI saving. The code does not squash migrations, delete tests, raise worker counts, or alter the single-database concurrency races described in [the prior measurement](01-measurement.md#d6-correctness-boundary-for-a-future-split).
