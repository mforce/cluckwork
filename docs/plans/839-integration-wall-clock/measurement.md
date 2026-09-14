# Integration suite wall clock, issue #839

## Checkout and method

Measured on 2026-09-14 at base commit `c1cc58c089eea1be3a0d0886064db0488bf8e73c`,
branch `perf/ci-integration-wall-clock-839`. This checkout predates the CI matrix
mentioned in the issue; its workflow still tests the solution in one job.
No rebase or CI run was performed.

The machine has an AMD Ryzen 5 6600H, 12 logical CPUs, and about 18.8 GiB RAM.
Docker is 29.8.0 and the .NET SDK is 10.0.112. These are local Debug measurements
with cached container images, not measurements of a Release build on a CI runner.
No other benchmark or build ran concurrently with these test runs. External
machine contention was not controlled.

The initial full solution build, including restore, took **18.57 seconds** and
reported zero warnings and errors. The baseline then ran only
`tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj` with
`--no-build`, detailed console logging, and TRX output. A Python wrapper recorded
elapsed timestamps and Docker lifecycle events. The committed replacement is
[`tools/test-timing/measure.py`](../../../tools/test-timing/measure.py); its
[README](../../../tools/test-timing/README.md) gives the commands and metric limits.
Raw artifacts are under `/tmp/cluckwork-839-measurements/` on the measurement host.

## Baseline findings

The unmodified suite passed **1,801 tests in 434.51 seconds**. Together with the
separate initial solution build, that is **453.08 seconds**. TRX reported
**1,718.94 seconds of summed test duration**, spread across concurrent collections.
That sum is not elapsed time.

Three costs stand out:

1. The shared `integration` collection contains **93 classes and 1,022 tests**.
   Its first test started at **190.76 seconds**, and its last ended at
   **433.78 seconds**. Its tests accounted for **242.54 seconds** of duration
   in a **243.02-second serial span**. Starting this collection late left a
   long tail after smaller collections finished.
2. **117 Postgres containers** averaged **3.69 seconds** from creation to
   readiness, totaling **432.03 container-seconds**. Across all container types,
   the readiness intervals occupied **177.60 distinct wall-clock seconds**,
   overlapping test execution. This is not 432 seconds that can simply be
   subtracted from the run.
3. Subprocess-heavy classes were expensive: `SeedCommandTests` totaled
   **131.14 seconds**, `ProcessRoleGuardTests` **127.26 seconds**, and
   `OneShotVerbMinimalConfigTests` **119.05 seconds**. These totals include
   repeated process startup and real application work, not just SQL assertions.

Docker events and Testcontainers console IDs agreed on **125 containers**:
117 Postgres, seven Redis, and one Ryuk. The shared collection already uses one
`ICollectionFixture<CluckworkWebApplicationFactory>`. Five other collection
fixtures share specialized factories. Other classes use isolated class fixtures,
per-test lifetimes, or explicit containers inside methods. Migration and seeding
tests often require their own database state.

## Diagnostic repeat before optimization

A second run enabled the base-factory timers but still used the original
scheduler. It finished in **327.06 seconds**. The shared collection began at
**24.03 seconds** and ended at **326.05 seconds**, with **301.17 seconds** of
summed test duration. Its earlier start overlapped more concurrent work, which
also increased its execution duration. The 107.45-second difference between
these unoptimized runs shows why one before/after percentage is not a stable
performance estimate.

This diagnostic run passed 1,800 tests and failed one source guard. The initial
container timing phase label matched the image-pin guard's forbidden bare-image
literal. Renaming the label to `container` fixed the instrumentation mistake;
the guard itself was not changed. This run is not a clean validation baseline.

The timers recorded **74 base-factory container starts totaling 297.69 seconds**
and **73 host/migration phases totaling 160.99 seconds**. The extra container
start belongs to initialization that deliberately skips migration. These counts
cover the base factory, not all 117 Postgres containers, and exclude subsequent
derived-factory seeding. Across all 117 Postgres containers, console readiness
averaged **3.25 seconds**, totaling **379.93 seconds**. There were again seven
Redis containers and one Ryuk.

## Change

`IntegrationCollectionOrderer` places the shared collection first. It delegates
ordering of the other collections to xUnit's existing default orderer. Tests
within collections retain their existing order and serialization. Container
ownership, concurrency limits, race assertions, and product code are unchanged.
In particular, `StealLossConnectionReleaseTests` keeps its dedicated factory,
one-slot connection pool, and timing assertions.

The base factory also has opt-in timing for container startup and host
initialization plus migrations. The measurement command enables it. Derived
factory seeding is outside these timers; factories created inside test methods
also appear in test durations. Phase sums overlap and are not additive parts of
wall clock.

The installed xUnit 2.9.3 XML documentation describes its default collection
order as unstable between runs. The scheduling change addresses the observed
late start without increasing parallelism. The runner's collection behavior is
documented in [xUnit's parallel execution guide](https://xunit.net/docs/running-tests-in-parallel).

## Optimized diagnostic run

The first run with the orderer took **327.89 seconds**, with the shared
collection starting at **10.74 seconds** and ending at **327.08 seconds**.
The test-name multiset exactly matched the baseline. All 125 containers were
still created and reached readiness.

This run passed 1,799 tests and failed the two image-pin source guards. After
staging the Python reporter, the guards interpreted its dictionary key variable
as YAML image configuration. Renaming that variable to `reference` fixed the
reporter; both unchanged guards then passed in a targeted run. This diagnostic
is not a successful validation run or evidence of a further speedup over the
earlier-starting diagnostic baseline.

## Final validation and comparison

The final run passed **all 1,801 tests in 322.46 seconds**.
Its test-name multiset exactly matched the clean baseline, with no skipped tests.
The same 117 Postgres, seven Redis, and one Ryuk containers all reached readiness.
The steal-loss race test and both image-pin guards passed.

| Run | Test wall seconds | Shared collection first test | Outcome |
| --- | ---: | ---: | --- |
| Clean baseline | 434.51 | 190.76 | 1,801 passed |
| Diagnostic baseline, original scheduler | 327.06 | 24.03 | 1,800 passed, one instrumentation guard failure |
| Optimized diagnostic | 327.89 | 10.74 | 1,799 passed, two reporter guard failures |
| Final optimized | 322.46 | 10.35 | 1,801 passed |

The clean before/after difference is **112.05 seconds,
or 25.8%**. That percentage is one observed comparison, not a
repeatable throughput estimate. The diagnostic baseline already achieved
327.06 seconds by drawing an earlier collection slot. The change removes that
variation in when the large collection starts; it does not make its tests cheaper.
In the final run, the shared collection's test durations totaled
**310.45 seconds**, while overlapping more work from other collections.

Final Postgres creation-to-readiness time averaged **2.92 seconds** across
117 containers, totaling **341.37 container-seconds**. The base-factory timers
recorded **74 container starts totaling 254.91 seconds**
and **73 host/migration phases totaling 144.86 seconds**.
These phase sums overlap test execution and each other.

The full solution build after the C# changes passed with zero warnings and
errors in **9.76 seconds**. This was an incremental build, so its difference
from the initial 18.57-second build is not attributed to the optimization.
The Python interval-union calculation was also checked against overlapping
and nested intervals with literal expected totals.

The pre-commit checks also passed when run directly: **491 Domain tests and
290 Application tests**. Invoking them inside `git commit` first caused
18 documentation-test failures because the hook exported `GIT_DIR` and the
tests invoked Git from the test binary directory. Reproducing that environment
made `git rev-parse --show-toplevel` return the binary directory instead of the
worktree root. The same hook and commit-message validation passed outside that
environment. No hook or unrelated test was changed.

## Correction: the 25.8% figure is not a speedup

The section above reports a clean before/after difference of 112.05 seconds and
already calls it one observed comparison rather than a throughput estimate. A
paired re-run on the same machine, minutes apart, at the same configuration,
removes even that reading:

| Run | Config | Wall seconds | Outcome |
| --- | --- | ---: | --- |
| `main` (`18b45dc`, no orderer) | Debug | 320 | 1,801 passed |
| This branch | Debug | 326 | 1,815 passed |
| This branch | Release | 341 | 1,815 passed |
| This branch, via `measure.py` | Debug | 331.5 | 1,815 passed |

The orderer and the unmodified scheduler land inside each other's spread. The
comparison that produced 25.8% was against a single 434.51-second run, and this
report's own diagnostic section shows the **unmodified** scheduler reaching
327.06 seconds on its second attempt. The slow run was an unlucky collection
schedule, not the normal cost of the suite.

So the change is a **variance reduction, not a throughput win**. xUnit 2.9.3
documents its default collection order as unstable between runs; pinning the
long serialized collection to the front removes the schedule that puts it last
and leaves a long tail behind it. It does not make any test cheaper, and no
percentage should be claimed from it.

Two premises in the issue also did not survive measurement.

**Container reuse is already done.** The shared collection holds one
`ICollectionFixture<CluckworkWebApplicationFactory>` covering 1,024 of 1,815
tests. The 119 Postgres containers in a run are overwhelmingly the specialized
factories that need their own database, migration state, or advisory-lock
behavior, not accidental per-class startup.

**The remaining cost is not SQL.** Container readiness occupies 156.6 distinct
wall-clock seconds out of 331.5, and it overlaps test execution. What does not
overlap is repeated one-shot process startup: `SeedCommandTests` 131.14s,
`ProcessRoleGuardTests` 127.26s, `OneShotVerbMinimalConfigTests` 119.05s. Those
three classes are the largest single lever left and the issue never names them.

## Files changed

- `tests/Cluckwork.Api.IntegrationTests/Infrastructure/IntegrationCollectionOrderer.cs`
- `tests/Cluckwork.Api.IntegrationTests/Infrastructure/CluckworkWebApplicationFactory.cs`
- `tools/test-timing/measure.py`
- `tools/test-timing/summarize.py`
- `tools/test-timing/README.md`
- `tools/test-timing/.gitignore`
- `docs/plans/839-integration-wall-clock/measurement.md`

## Remaining options

- Share more containers only after preserving separate database, migration, and
  advisory-lock state. The existing shared collection already removes per-class
  container startup for most tests.
- Split the shared collection into independently isolated collections after
  auditing its default-account and shared-state assumptions.
- Profile repeated one-shot process startup and simulation seeding separately.
- Measure Release on CI and compare repeated runs there. Local results do not
  establish a CI improvement or an optimal worker count for hosted runners.

Higher parallelism, test deletion, SQLite, and product changes were not used.
