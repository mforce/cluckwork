# Exactly one serving API instance (#271, #338)

> **Rule** — the one-paragraph version lives in
> [`AGENTS.md`](../../AGENTS.md#deploy-invariant-exactly-one-serving-api-instance-271-338);
> this file is the relocated rationale, including how the blocker list was
> derived and why that method matters.

**Status:** accepted — the invariant stands until all four blockers close
· **Date:** 2026-07

## The rule

**Run one serving instance.** More than one breaks four separate things, and
none of them announces itself as "you are running two replicas". This is a
requirement the app imposes on its host, so it lives in this repo; the concrete
replica count and how it is pinned are deploy-side.

The run-then-exit verbs (`migrate`, `seed`, `recover-admin`, `bootstrap-admin`,
`healthcheck`) are unaffected — they do not start the host's hosted services.

## What is exposed today

`AddHostedService<DurableJobWorker>()`
(`Hosting/CluckworkJobServiceCollectionExtensions.cs`) means **every** instance
runs the worker loop, and the poll claims nothing — no `FOR UPDATE SKIP LOCKED`,
no lease, no advisory lock. What that exposes today is **the three recurring
sweeps**, which ride the same poll and run unconditionally per instance:
`DailyEntryLockSweep`, `RefreshTokenPurgeSweep`, `IdempotencyRecordPurgeSweep`.

The durable-job half is still a scaffold that selects pending rows and logs
them — no handlers are registered — so job double-execution is **latent, not
live**. Registering the first handler makes it live, which is the moment this
invariant stops being about sweeps only.

## What an operator would actually see

Not every double-run is equally bad, and the difference decides what shows up.

The two purge sweeps are idempotent deletes: a second runner wastes work and
reports nothing — genuinely silent.

`DailyEntryLockSweep` is not silent. It reads-then-writes behind an optimistic
`Version` token, so the losing replica's `SaveChangesAsync` throws a concurrency
exception, which its per-account `catch` logs as `Lock sweep failed for account
{AccountId}` (`Jobs/DailyEntryLockSweep.cs`). That is observable — but it reads
as a *database fault*, not as "two replicas are sweeping", which is the sense in
which the duplicate stays invisible.

## The four blockers, all of which must close before scaling

They are **not all in #271**:

- **#271 — background work has no single-runner guarantee** (this record's
  subject). Needs an advisory-lock lease or `FOR UPDATE SKIP LOCKED` with crash
  recovery, **plus** a two-instance test proving each job and each sweep runs
  exactly once.
- **#338 — `IStepUpGrantRegistry` is process-local.**
  `InMemoryStepUpGrantRegistry` is a per-process singleton holding step-up
  replay tracking and logout epochs, and its own header says a multi-instance
  deployment must move it to a shared store. Both #308 guarantees degrade per
  replica: a single-use grant becomes usable **once per replica**, and a logout
  honoured by one replica is invisible to the others. These grants gate
  privileged account-control operations, so this is the blocker with teeth —
  closing #271 alone does not license scaling.
- **The IP-keyed auth limiters (#143) are in-process.** `AddRateLimiter`'s
  partitions live in each process — login, refresh, and client-error reports
  alike — so N replicas allow roughly N times the intended attempts per IP
  before lockout.
- **The per-account report concurrency cap (#311) is in-process.**
  `ReportConcurrencyLimiter` is a singleton owning a
  `PartitionedRateLimiter<Guid>` (`Api/RateLimiting/ReportConcurrencyLimiter.cs`,
  registered at `Hosting/CluckworkRateLimitingServiceCollectionExtensions.cs`),
  so one account can hold up to N × `ReportsConcurrency.PermitLimit` heavyweight
  report queries in flight — the DB/CPU ceiling that cap exists to bound,
  multiplied by the replica count.

The last two have no open issue; they are recorded here rather than left to be
rediscovered.

## How this list was derived, because it was twice derived wrongly

Both misses were the same shape — a process-local limiter — and the second one
lived in a file the first sweep had already opened.

**So do not extend this list from memory.** Re-derive it: enumerate **every**
`AddSingleton`/`AddHostedService` under `src/`, plus every in-memory state
primitive (`ConcurrentDictionary`, `IMemoryCache`, `PartitionedRateLimiter`,
`Channel`, `SemaphoreSlim`, mutable statics), then classify each one as safe or
not.

That walk currently finds **13** `AddSingleton` registrations under `src/` plus
1 `AddHostedService`, and the four above are what survives it.

**That number was wrong in `AGENTS.md` until 2026-08-16**, where it read 12 —
carried forward unchecked through the compression that produced this record, and
caught by review. Which is the rule proving itself: a walk that finds 13 against
a documented 12 reads as drift and invites someone to go looking for the
"extra", or to stop at 12 and miss one. **Re-run the count; do not trust this
paragraph either.**

```bash
grep -rn 'AddSingleton' src/ --include='*.cs' | wc -l      # 13 on 2026-08-16
grep -rn 'AddHostedService' src/ --include='*.cs' | wc -l  # 1
```

Excluded deliberately, so the next walk need not re-litigate them:
`TimeProvider.System`, the Serilog diagnostic contexts,
`IValidateOptions`/`IAuthorizationMiddlewareResultHandler` (all stateless), and
`FirstRunProvisioningLatch` — a monotonic one-way cache of "the default account
has an Owner", where a per-replica copy costs at most a few extra reads and
cannot go stale in the unsafe direction.

This is the general lesson recorded in
[`407-writing-a-guard.md`](407-writing-a-guard.md): two misses of the same shape
mean the **method** is wrong, so prefer "walk everything, exclude deliberately"
over "list what I thought of".

## What does NOT license scaling

**#307 (multi-replica HTTP write idempotency) is CLOSED**, so the request-path
half is genuinely done — do not read that closure, or #271's, as permission to
scale. Documenting the invariant is the interim mitigation, not the close for
any of the four.
