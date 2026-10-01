# #863: demo seed profile

**Base:** `1953cdea0737a6bb382d051680bcbac9bc3af639` (`origin/main`, including #1002). **Date:** 2026-09-30. This is a diagnosis, not an optimization. The measurements used the Release integration project, real Postgres through Testcontainers, and the 12-core local host. Temporary source and test probes were removed before this document was committed.

## P1: method and scope

A temporary one-fact probe created a fresh `CluckworkWebApplicationFactory`, provisioned one Owner, called `DemoDataSeeder.SeedAsync` once, and queried the resulting database. Each of three independent test processes started a fresh container and database. Stopwatch markers bracketed the seeder's preflight, clock and grade lookup, flocks, each house's entry loop, cull, customers, products, and orders. An EF `DiagnosticListener` filtered by the seeder's `DbContext.ContextId` recorded successful commands, command durations, `SaveChangesStarting`, and `DetectChangesStarting`/`Completed`. SQL statements were counted by splitting the generated command text at semicolons; each resulting statement began with `SELECT`, `INSERT`, or `UPDATE`. Process CPU time was sampled around `SeedAsync`.

Three further full integration runs used the same phase markers and a temporary per-seeder EF listener. The filtered probe fact was excluded, leaving the normal 1,873 tests. Earlier full-suite runs on #1002 supply uninstrumented class-duration context. The temporary listener adds work, so the full-suite wall figures are diagnostic samples, not a before/after performance comparison.

## P2: isolated phase times

The three detailed, audited fresh-seed runs took **16.54, 16.36, and 16.41 seconds** inside `SeedAsync` (median 16.41; spread 0.18 seconds). They passed and produced identical row and SQL counts. These are isolated seeds; the much longer full-suite class spans are measured separately below.

| `SeedAsync` phase | Median, seconds | Three-run range, seconds | Work |
|---|---:|---:|---|
| Preflight and Owner/actor lookup | 0.069 | 0.069–0.069 | Base existence, Owner, existing-flock checks |
| Farm clock and grade lookup | 0.042 | 0.042–0.043 | Reads grades; creates none |
| Three flocks and historical depletion | 0.097 | 0.091–0.099 | Three create handlers, one update |
| House 1 entries and submissions | 6.981 | 6.775–7.130 | 241 entries, 240 submissions |
| House 2 entries and submissions | 8.372 | 8.340–8.583 | 240 entries, 240 submissions |
| Cull | 0.038 | 0.037–0.039 | One bird movement |
| Three customers | 0.083 | 0.082–0.084 | Three create handlers |
| Three products | 0.202 | 0.200–0.206 | Three create handlers |
| Two orders, three items, confirmation | 0.501 | 0.495–0.519 | Includes FIFO allocation |

The two daily-entry loops consume **15.32–15.50 seconds, about 93–94%** of an isolated seed. `SeedDemoAsync` creates no houses: it uses the base farm and house identifiers. `RecordDailyEntryHandler` writes entry and grade rows; `SubmitDailyEntryHandler` mints egg lots, production movements, and mortality movements. Those rows are inside the two loop timings, not separate later phases. `ConfirmSaleHandler` creates the allocations inside the order timing.

## P3: statements, rows, and audit

Each audited run executed **4,389 EF database commands** containing **11,784 SQL statements**: 3,408 `SELECT`, 7,887 `INSERT`, and 489 `UPDATE`. A command may contain several batched statements, so 4,389 is the measured EF command count, not a count of wire packets. The two daily loops account for 4,325 commands and 11,689 statements. There were **981 `SaveChanges` calls**: 481 while recording daily entries and 480 while submitting them. The other 20 cover flocks, cull, customers, products, and orders. The seeder therefore pays a save for nearly every record or submit action; it does not bulk-insert the 481 days.

The resulting rows include 3 flocks, 481 daily entries, 1,443 grade lines, 2,400 egg lots, 2,404 inventory movements, 161 bird movements, 3 customers, 3 products, 2 orders, 3 order items, and 4 allocations. The 7,887 insert statements include **977 audit events**. Of those, 481 audit entry creation and 480 audit submission; 16 cover the other actions. `AuditWriter.WriteAsync` adds an event to the same unit of work and does not call `SaveChanges` itself. Thus 977 audit inserts cost almost no extra commands, but do add rows to EF's growing tracked graph.

To bound audit's marginal cost, a temporary probe returned before `AuditWriter` added its event. This deliberately produced zero audit rows and was never a candidate change. Three audited runs on that same temporary binary took **16.93, 16.58, 16.41 seconds**; three interleaved audit-disabled runs took **15.55, 14.94, 15.45 seconds**. The median difference was **1.13 seconds, or 6.8%** of the audited median. Counts changed from 11,784 to 10,807 SQL statements, but only from 4,389 to 4,386 EF commands; the 981 saves remained. This estimates the end-to-end marginal cost of audit rows in an isolated seed, including their effect on tracking. It does not establish a CI percentage, and #500 requires those rows.

## P4: CPU versus database wait

Across the three detailed isolated runs, all `SaveChanges` calls occupied **13.30–13.49 seconds** of the **16.36–16.54-second** seed. EF `DetectChanges` ran **1,962 times**, twice per save, and occupied **10.30–10.51 seconds**. Successful EF command execution occupied only **3.37–3.44 seconds** in total, including queries outside saves. Process CPU time over the seed was **19.18–19.82 seconds**; that process-wide measure includes the test host's background work, but it corroborates the direct in-process change-detection timings.

The second house took longer despite having one fewer entry. Median save duration rose from **9.2 milliseconds in the first 100 daily-loop saves** to **18.6 milliseconds in the last 100**, while the context ended with **7,897 tracked entities**. The mechanism is visible in source: `TenantStampInterceptor.StampTenant` calls `ChangeTracker.Entries()` on every save, which triggers detection, and EF runs another detection during `SaveChanges`. The first and second passes measured about **5.4 and 5.0 seconds** per isolated seed, respectively. Removing a pass is not automatically safe: tenant stamping must still see every pending write, and all the `Version` and tenant race tests must retain their SQL behavior.

The database does execute many statements, but command time is about one fifth of isolated seed wall; the dominant measured cost is client-side change detection over a growing context. Batching only the wire traffic would leave that cost. Generating the deterministic egg counts is a small part of the loops; the handlers and EF save path dominate.

## P5: boot versus seed under suite load

The fresh-process probe took **10.14–12.40 seconds** to start its first factory, including starting the shared Postgres container, migrating its template, cloning one database, and constructing the host. Owner provisioning then took **0.36 seconds**. `SeedAsync` itself took **16.36–16.54 seconds**. In the full suites, warm demo factory database creation took **0.06–0.11 seconds**, and host migration checks took **0.98–1.76 seconds**. One demo factory paid the one-time shared-server startup, **12.18 seconds**, in one run. Repeated host boot is not the source of a 150–190-second class.

Four in-process test classes call the full seeder five times: both `DemoSeedTests` facts, `DemoSeedBootTests`, `DemoSeedDisabledOwnerTests`, and `DemoSeedAttributionTests`. The fifth class, `SeedCommandTests`, also runs three full `seed --profile demo` subprocesses, besides idempotent reruns and failure cases. Its captured child output was outside this in-process trace. Its class took **123.0–136.5 seconds** in the three EF diagnostic full runs. In one phase-marked full suite, the five in-process seed spans were **167.8, 166.2, 145.1, 102.7, and 19.6 seconds**. The four long seeds overlapped, and each spent nearly all its time in the daily loops. The fifth began as that overlap cleared. The suite passed 1,873 tests in 339.8 seconds. This shows how a 16-second isolated seed can occupy more than 150 seconds when several classes and the rest of the suite compete on the same host.

The per-seeder EF diagnostic full-suite runs and their spread are recorded below. `DetectChanges` remains the largest timed component under overlap; its wall time includes CPU scheduling delays, so these are elapsed component times, not exclusive CPU samples.

| Full run | Suite wall, seconds | Overlapping full-seed spans, seconds | `DetectChanges` within those seeds, seconds | EF command execution within those seeds, seconds |
|---|---:|---|---|---|
| 1 | 352.6 | 162.1–174.2 | 138.3–148.6 | 14.4–14.9 |
| 2 | 342.7 | 122.2–153.5 | 107.0–136.0 | 7.8–9.4 |
| 3 | 343.0 | 121.0–153.3 | 103.6–133.6 | 10.0–10.8 |

All three runs passed 1,873 tests. Their suite-wall spread was 9.9 seconds. Each also had a late, lightly contended seed at 15.6–16.2 seconds. Across the twelve overlapping seeds, `DetectChanges` occupied 103.6–148.6 seconds per seed, **85–89%** of its elapsed seed time. EF command execution occupied 7.8–14.9 seconds, **6–9%**. The counts stayed at 4,389 commands, 981 saves, and 1,962 detection passes for every seed. The earlier uninstrumented final #1002 run passed at 336.1 seconds; three earlier one-server runs passed at 343.8–346.7 seconds. Individual demo class spans varied widely. The issue's roughly 190-second figure is a contended class duration, not a cost intrinsic to one isolated `SeedAsync` call. CI's exact slowdown remains unprofiled, so local component ratios are not a measured CI saving.

## P6: reproduction and boundary

Reproduce on `1953cdea` with a temporary fact named `DemoSeedProfileProbe` that initializes one `CluckworkWebApplicationFactory`, provisions an Owner, calls `SeedAsync`, and checks the row counts above. Bracket the named phases in `DemoDataSeeder` with `Stopwatch.GetTimestamp`. Subscribe to EF's `Microsoft.EntityFrameworkCore` `DiagnosticListener`, filter events by that seeder scope's `DbContext.ContextId.InstanceId`, and record `CommandExecutedEventData.Duration`, `SaveChangesStarting`, and `DetectChangesStarting`/`Completed`. Use `Process.TotalProcessorTime` for the process-wide CPU cross-check. Build the Release integration project, then run `sg docker -c 'dotnet test tests/Cluckwork.Api.IntegrationTests --configuration Release --no-build --filter FullyQualifiedName~DemoSeedProfileProbe'` three times. Run the full suite with `tools/test-timing/measure.py` and a new output directory for each run to observe overlap. For the audit counterfactual only, temporarily bypass the event add in `AuditWriter`, run three focused seeds, verify zero audit rows, and restore the writer. The temporary test, markers, listener, and bypass were all removed from the PR. Do not use the bypass for a product change.

Any later fix must preserve the exact demo output: three flocks and statuses, 240 entries for House 2, at least 230 rated days per active flock, more than 50 Large lots, and confirmed and draft orders. It must also preserve all 977 audited actions and their real actor. `seed --profile demo` is a user-facing, authoritative, fail-loud verb under #280; a change to its handlers or transaction boundaries has more risk than a test-only optimization. Producing fewer rows is a separate product decision, not a performance fix for this issue.

## Recommendation

**There is fat in repeated EF change detection, not in the size of the demo farm.** The measured 10.3–10.5 seconds of isolated change-detection work per seed is an upper bound on what eliminating that work entirely could save. Eliminating one of two passes projects roughly five seconds per isolated seed, before correctness and real wall effects are tested. In the contended suite the same component occupied 103.6–148.6 seconds per long seed, but that is component time, not an attainable saving or a CI projection. The next implementation spike should reduce full-context scans or shorten the context's tracked lifetime while keeping every handler result, row, audit event, tenant check, and concurrency assertion intact. Measure wall time in repeated full CI runs before claiming a suite win. Confidence is high in the local bottleneck and low in a numeric CI payoff.
