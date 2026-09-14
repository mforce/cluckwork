# #840 findings — four remaining integration flakes

Four read-only investigation lanes over the census on #775. Two lanes returned
reports, one timed out and its slice was completed by hand, one mapped the
harness. Every claim below was re-verified against the code at the commit this
branch was cut from; where a lane's conclusion did not survive that check it is
recorded as refuted, with the reason.

## Lane A — `DurableJobWorkerLeaderGateTests` (Leader_Polls :77, Follower_NeverPolls :67)

**The lane's headline mechanism is refuted.** It claimed thread-pool starvation:
that `StartAsync` only queues `ExecuteAsync`, so on a loaded runner the loop
never starts inside the 120 ms window. That is not what `BackgroundService`
does — `StartAsync` invokes `ExecuteAsync` synchronously on the calling thread
and returns at its first genuinely incomplete await. The census's own refutation
therefore stands, and the lane's proposed remedies (`[Collection(..., DisableParallelization = true)]`,
waiting on `ExecuteTask`) have no mechanism behind them. Do not apply either.

What the code actually shows:

- Leader path (`src/Cluckwork.Infrastructure/Jobs/DurableJobWorker.cs:69-76`):
  `TryAcquireLeadershipAsync` → `StubLease.TryAcquireAsync` returns
  `Task.FromResult` → `TryProcessPendingJobsAsync` → `CreateScope()`. The test's
  `CountingScopeFactory.CreateScope` increments **before** it throws
  (`tests/Cluckwork.Api.IntegrationTests/DurableJobWorkerLeaderGateTests.cs:22-25`),
  and the throw is swallowed (`DurableJobWorker.cs:128-132`). All of that runs
  inline before `StartAsync` returns, so `Assert.True(Creates > 0)` at `:77`
  cannot fail by timing — it fails only if the first iteration never reached
  `CreateScope`.
- Follower path (`DurableJobWorker.cs:71-73`): stamps the heartbeat with no
  external call, then awaits `Task.Delay(pollInterval)`. Same shape: the stamp
  happens before `StartAsync` returns, so `Assert.NotNull` at `:67` fails only
  if the first iteration never reached the stamp.
- The only branch between loop entry and either of those points that can skip
  both is `TryAcquireLeadershipAsync`'s catch turning a throw into
  `LeaseStatus.Faulted` (`DurableJobWorker.cs:104-113`). With `StubLease` that
  is unreachable in-process.
- No shared mutable state: `StubLease` is immutable, `CountingScopeFactory` and
  `DurableJobWorkerHeartbeat` use `Interlocked`, and the class holds no statics.

**Conclusion: nothing in the test or the worker explains either failure.** The
next step is an instrumented CI run — log first-iteration entry, the leadership
value observed, and any `Faulted` transition — not a local re-run. Twenty-five
local runs under load already produced no failure, so local load is not the
variable.

## Lane B — `MultiInstanceRateLimitTests` / `DistributedRateLimiterWiringTests` (expected 429, got 401)

**The lane's mechanism is real but does not fit the evidence.** Verified chain:
`ResilientFixedWindowCounter.IncrementAsync` catches `RedisException` and
`RedisTimeoutException` and falls back to a per-process
`InProcessFixedWindowCounter` (`src/Cluckwork.Infrastructure/SharedState/ResilientFixedWindowCounter.cs:33-45`);
`AbortOnConnectFail=false` (`SharedStateRegistration.cs:48`). A Redis hiccup
mid-test therefore does produce "local count under limit → request reaches the
handler → 401".

Two facts argue it is not the mechanism here:

1. The test's Redis is a private Testcontainer started seconds earlier
   (`tests/Cluckwork.Api.IntegrationTests/MultiInstanceRateLimitTests.cs:36-45`).
2. The census's local reproduction is
   `DistributedRateLimiterWiringTests.Primary_down_fallback_serves_and_enforces_and_alarms`,
   which uses **no Redis at all** — a `ToggleablePrimary` double
   (`tests/Cluckwork.Api.IntegrationTests/DistributedRateLimiterWiringTests.cs:78-105`).
   A Redis-outage mechanism cannot explain a failure with no Redis in the path.

### New finding: the login bucket key is shared across concurrently running test classes

The counter key is `auth-login:<client-ip>` and nothing else
(`src/Cluckwork.Infrastructure/RateLimiting/DistributedIpFixedWindowPolicy.cs:44`,
`RateLimitKey.cs:21-38`) — no account, no email, no process.

- `MultiInstanceRateLimitTests` runs its two serving instances on 127.0.0.1 with
  `PermitLimit=5` (`:108`) and asserts the 6th request 429s (`:155-158`). It is a
  bare `IAsyncLifetime` class (`:24`) — **no `[Collection]`** — so xUnit runs it
  concurrently with other collections.
- Roughly seventy test classes log in through `TestHarness.LoginAsync`
  (`tests/Cluckwork.Api.IntegrationTests/Infrastructure/TestHarness.cs:410-419`),
  which posts to `/api/v1/auth/login` and therefore spends the same policy.
- Classes in their own collections run in parallel with the multi-instance test:
  `RateLimitingTests` (`IClassFixture`, `RateLimitingTests.cs:37`),
  `AuthBodyLimitTests` (`:64-65`), and the three
  `ForwardedHeaderTrustKestrel` classes (`:119-120`, `:150-151`, `:183-184`).
- Those classes fake the peer with `FakeRemoteIpStartupFilter`, which rewrites
  `Connection.RemoteIpAddress` **only when the `X-Test-Remote` header is present**
  (`tests/Cluckwork.Api.IntegrationTests/Infrastructure/FakeRemoteIpStartupFilter.cs:17-19`).
  `AuthBodyLimitTests` never sets it (`AuthBodyLimitTests.cs:73-78`), so its
  logins keep the real socket peer — **127.0.0.1** — the exact key the
  multi-instance test counts on. Its own limiter is effectively disabled
  (`PermitLimit=3` against the app's 1 000 000 override), so it spends the shared
  key freely.

The census ruled out mid-test `_down` flips because the class is one collection.
That reasoning never considered a **cross-collection key collision on the
loopback address**, which is the shape this finding has.

Confirming experiment: read the shared key's count around a controlled burst and
report what was already spent when the burst started. Instrumentation that
records only the counter's own backend/key/count/window sees the foreign
increments and attributes them to nothing, which is why the census's 80 clean
instrumented runs proved nothing.

### The probe ran, and it refuted part of this hypothesis

`tests/Cluckwork.Api.IntegrationTests/LoginCounterKeyProbeTests.cs` boots a real
serving child against a private Postgres and Redis container, waits for the
login policy by polling `/api/v1/auth/login` rather than `/health/ready`, sends
fourteen logins, and reads the key before and after.

    CLUCKWORK_840_PROBE {"permitBurst":14,
      "observedStatuses":[401,401,401,401,401,401,401,401,401,429,429,429,429,429],
      "keysBefore":{"{cluckwork:win:auth-login:127.0.0.1}:1988220":1},
      "keysAfter":{"{cluckwork:win:auth-login:127.0.0.1}:1988220":20},
      "spendBeforeThisProbe":1,"bucketCountAfter":1,
      "spendOnKeysThisProbeDidNotCreate":0}

Identical on the local run and on CI, which is what a mechanism claim needs.

Two things follow, one confirming and one correcting.

**Confirmed:** the key is shared and spendable by anyone on the loopback address.
`keysBefore` was already `1` — a login from somewhere else in the suite had spent
this bucket before the probe's first request — and the count moved by 19 while the
probe sent 14. So the collision is real, not theoretical. `bucketCountAfter` is `1`
and `spendOnKeysThisProbeDidNotCreate` is `0`, which rules out the other explanation
for a delta larger than the burst: the window did not roll over mid-burst, so the
extra five increments were written into the same bucket by other classes.

The probe uses the **shipped** budget (10 per 900 s) rather than a test-local one,
so the 429 starts at request 10 — one after the foreign spend — exactly where the
production configuration says it must.

**Corrected:** the key shape in this file's earlier draft was wrong. The live key
is `{cluckwork:win:auth-login:127.0.0.1}:<bucket>` — the namespace and window
prefix sit inside the hash tag, not just the namespace. Anything that reconstructs
the key by hand would have scanned for the wrong pattern and reported a false zero.

**Also corrected:** the probe's first version booted a serving child with an
unreachable database and waited on `/health/ready`. `DatabaseReadyHealthCheck`
makes that endpoint 503 whenever the database is unreachable, so the readiness
wait ran to its full 60 s and the test failed identically locally and on CI. The
limiter runs before the endpoint, so the probe now polls the login route itself
and does not depend on the database at all.

## Lane C — `StealLossConnectionReleaseTests` (Npgsql `Authenticate` timeout)

The lane timed out; this slice was completed by hand.

- The class is a bare `IClassFixture` (`StealLossConnectionReleaseTests.cs:61-62`),
  **not** in the shared collection, so it runs concurrently with the 1,024-test
  collection on a 2-core runner.
- Its app pool is deliberately `Maximum Pool Size=1;Timeout=3` (`:56`) — the
  tightest connect budget in the suite. The test also opens extra physical
  connections on purpose: a lock-holder connection and transaction, then a probe
  (`:130-157`, `:179-186`).
- The base factory hands the raw Testcontainers string straight through
  (`Infrastructure/CluckworkWebApplicationFactory.cs:23,64`) and
  `PostgresConnectionString.NormalizeAndValidate` (`:79-107`) rewrites only the
  URI form, the TLS floor and the GSS default — **no `Timeout` is ever set**, so
  the app's own pool waits on Npgsql's default while the lease and probe paths
  sit at 3 s.
- `NpgsqlConnector.Authenticate` timing out means the socket connected and the
  server never finished the handshake. Client-side pool exhaustion fails earlier,
  before `Authenticate`. So this is server-side starvation — Postgres forks per
  connection and shares two cores with Ryuk and roughly 127 other containers —
  not a client budget problem.

**Conclusion: contention, as the census said, and it is a symptom of the harness
rather than a defect in the test.** The lever that bounds it is fewer concurrent
containers and connections on the runner (the #839/#775 wall-clock work), not a
per-test timeout bump.

## Lane D — harness current state

| Lever | Current setting | Verdict |
|---|---|---|
| Shared fixture | 1,024 of 1,815 tests share one factory (`Infrastructure/IntegrationCollection.cs:5`) | already taken |
| Collection ordering | shared collection pinned first (`Infrastructure/IntegrationCollectionOrderer.cs:14-16`, #861) | variance reduction, done |
| xUnit parallelism | no `xunit.runner.json`; default settings; no test-level parallelism inside the shared collection | blocked by #839 |
| Runner CPU | 2 vCPU (`ci.yml:198`) | hard ceiling |
| Container reuse across matrix legs | impossible — fresh runner and fresh Docker daemon per leg | not actionable |
| Integration timeout | 25 min against ~5.5 min measured | healthy |
| Retry wrapper | none; flakes surface red | correct per #269 |

Actively harmful: nothing found, beyond CI keeping no failure-detail artifact,
which is why every one of these four flakes had to be re-derived from a stack
line.

## What follows from this

1. Do not lengthen a delay or add a parallelization gate to
   `DurableJobWorkerLeaderGateTests`. Neither has a mechanism.
2. Instrument the **login counter key across the whole assembly**, recording
   which process spent it — that is the only experiment that tests the
   cross-collection collision.
3. Freeze `TimeProvider` on the counter in both rate-limit tests. Still worth
   doing as variable elimination, but it removes window rollover only, not the
   collision.
4. Leave `StealLossConnectionReleaseTests` to the wall-clock work.
5. `DurableJobWorkerLeaderGateTests` needs an instrumented CI run; local load
   cannot reproduce it.
