# #863: serializing the five full-seed classes against each other

**Base:** `0d97a4d1` (`origin/main`, including the demo seed profile in `03-demo-seed-profile.md`). **Date:** 2026-09-30. All runs used the Release integration project, real Postgres through Testcontainers, `tools/test-timing/measure.py`, and the same 12-core host as the prior two documents in this series.

## T1: the hypothesis and the change

`03-demo-seed-profile.md` found the demo seed CPU-bound: EF change detection is 85-89% of an isolated seed's wall time under suite overlap, against 6-9% for SQL execution. Four in-process test classes each run the full seeder once (`DemoSeedTests`, `DemoSeedBootTests`, `DemoSeedDisabledOwnerTests`, `DemoSeedAttributionTests`); a fifth, `SeedCommandTests`, runs it three more times through real `seed --profile demo` subprocesses. Left to xUnit's default parallelism, these five classes overlap and compete for the same CPU-bound work, stretching a 16.4-second isolated seed to 120-190 seconds. The hypothesis: putting these five classes in one xUnit collection with no shared fixture serializes them against each other while every other class still runs in parallel, so the CPU-bound work no longer contends with itself.

The change is `tests/Cluckwork.Api.IntegrationTests/Infrastructure/DemoSeedCollection.cs`, a bare `[CollectionDefinition(Name)]` with **no** `ICollectionFixture`, plus `[Collection(DemoSeedCollection.Name)]` on the five classes. Each class keeps its own `IClassFixture<T>` factory subclass and therefore its own migrated Postgres database, exactly as `01-measurement.md`'s D2/D6 require. A `[CollectionDefinition]` with no collection fixture only orders execution; it does not merge fixtures. Verified by grep: the five `[Collection(DemoSeedCollection.Name)]` attributes are the only references to the type, `DemoSeedCollection` itself declares no `ICollectionFixture`, and it has no relation to the pre-existing `EnvironmentMutatingCollection` (which disables parallelism assembly-wide and must not be touched or inherited from).

## T2: three configurations, three seeds each

1. **Baseline** — unmodified `origin/main` test code.
2. **Four-class collection** — `DemoSeedTests`, `DemoSeedBootTests`, `DemoSeedDisabledOwnerTests`, `DemoSeedAttributionTests` serialized; `SeedCommandTests` left parallel.
3. **Five-class collection** — the four above plus `SeedCommandTests`.

A local agent unrelated to this measurement (`1e8279c`, working in the shared checkout, not this worktree) intermittently loaded the host between roughly 18:31 and 19:00 during this session, at one point bringing up its own `cluckwork-sim` Docker stack. Runs taken during that window are marked **contaminated** below and excluded from the comparison. From 19:00 onward the other agent was confirmed idle (`paseo get_agent_status`) for every run, and the rotation order was **baseline -> four-class -> five-class -> baseline -> ...** with load average recorded immediately before each run, so any residual host drift would land on all three configurations rather than one. Every configuration below has at least three runs taken while the other agent was confirmed idle; a full-suite run on this host routinely spikes 1-minute load to 9-14 from its own xUnit parallelism and Testcontainers fan-out regardless of configuration, so a mid-run spike was treated as self-generated rather than contamination once the other agent's status confirmed no activity.

| Configuration | Clean wall times, s | Median, s | Spread, s | Contaminated runs excluded, s |
|---|---|---:|---:|---|
| Baseline | 345.6, 343.9, 342.0, 345.0 | 344.5 | 3.6 | 374.5 (other agent active from run start) |
| Four-class collection | 326.2, 322.3, 325.5 | 325.5 | 3.9 | 345.7 (sim stack came up mid-run), 324.8 (no load telemetry captured; plausibly clean but not verified, excluded from the primary comparison on that basis) |
| Five-class collection | 320.7, 321.0, 317.6 | 320.7 | 3.4 | 340.1 (load spiked to 11.95 from the other agent's activity mid-run) |

All thirteen runs (four configurations' worth, clean and contaminated) passed 1,873 tests.

The clean ranges do not overlap. Baseline's lowest (342.0) is 15.8 seconds above the four-class collection's highest (326.2); the four-class collection's lowest (322.3) is 1.3 seconds above the five-class collection's highest (321.0). Going from baseline to the four-class collection saves a median 19.0 seconds, about 5.5%. Adding `SeedCommandTests` to the collection saves a further median 4.8 seconds, about 1.5%, for a combined median saving of 23.8 seconds, about 6.9%, over baseline.

## T3: what changed inside the collection

The prediction in the issue brief was direct arithmetic: four sequential ~16-second isolated seeds should beat four overlapping ~150-second ones. That arithmetic was flagged as suspect going in, because a serialized seed still competes with the rest of the suite, not with nothing. The measured result sits between the two extremes. Representative clean runs:

| Class | Baseline (baseline-4), s | Five-class collection (config3-3), s |
|---|---:|---:|
| `DemoSeedTests` | 146.5 | 30.5 |
| `DemoSeedBootTests` | 89.3 | 26.9 |
| `DemoSeedDisabledOwnerTests` | 137.1 | 17.7 |
| `DemoSeedAttributionTests` | 129.8 | 17.2 |
| `SeedCommandTests` | 119.3 | 121.7 |

Serialized against each other, the four in-process seeds ran in 17-31 seconds each, close to the 16.4-second isolated figure from `03-demo-seed-profile.md`, instead of 89-147 seconds under overlap. `SeedCommandTests` did not shrink: it spends most of its time inside three spawned `dotnet Cluckwork.Api.dll seed --profile demo` **subprocesses**, which do not share this test process's CPU contention with the other four classes' in-process EF work the same way. Serializing it after the other four still removes it from competing with them, and the small additional 4.8-second median saving in T2 is consistent with a class that gains less from serialization but no longer adds its own subprocess-launch CPU cost on top of the others' change-detection work.

The five classes' combined span (first start to last end) shrank from an overlapping window of roughly 60-190 seconds (baseline) to a genuinely serial 11.5-227.8 second span in the collection, matching the sum of their individual durations to within fixture overhead. That confirms real serialization, not a scheduling coincidence.

## Recommendation

**Ship the five-class collection.** It is the config3 form: `DemoSeedTests`, `DemoSeedBootTests`, `DemoSeedDisabledOwnerTests`, `DemoSeedAttributionTests`, and `SeedCommandTests` all carry `[Collection(DemoSeedCollection.Name)]`, with no shared fixture. Three load-verified clean runs beat three load-verified clean baseline runs by a non-overlapping margin, and beat the four-class variant by a smaller but still non-overlapping margin. Each class keeps its own factory and database; correctness is unaffected. Confidence is high in the local wall-clock win and in fixture isolation; CI confirmation is recorded separately in this document once available, since #1000 and #1002 both showed CI wall time swinging by 100+ seconds run to run on shared runners, well beyond the roughly 24-second local win measured here.
