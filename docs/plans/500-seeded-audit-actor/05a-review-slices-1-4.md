# Mid-point review — slices 1-3 (2026-08-12)

Panel: codex, pi, and two local agents (one on the product code, one on the
tests), per the standing policy. Reviewed `git diff --cached` at the end of
slice 3. Slice 4 was built in parallel and is therefore **outside** what the
panel saw — both local agents noticed the moving worktree and said so, which is
itself worth recording: the diff file handed to a reviewer is a snapshot, and a
reviewer that does not check `git status` will silently review the wrong thing.

**Yield: 3 confirmed defects, 2 refuted claims, 4 out-of-scope observations.**

---

## Confirmed and fixed

### 1. `Pick`'s nested fallback warned when nothing had been substituted (codex, P3)

`WorkerFor` read:

```csharp
Pick(eligible, dayIndex, Pick(cast.Managers, dayIndex, cast.Owner, "Managers"), "Workers")
```

C# evaluates the inner call **as an argument**, so the manager-pool `Pick` ran on
every production day regardless of whether any worker was available. At
`Simulation:Managers = 0` that logged *"provenance is degraded"* once per
(flock, day) — 180 warnings on a default 90-day run — while a worker had in fact
been selected and nothing was degraded.

This inverts the rule the warning exists to serve. AGENTS.md's "no silent caps"
asks for a degradation to be visible; a warning that fires when the degradation
did **not** happen is worse than none, because the next person to see it goes
looking for a fault that is not there.

Fixed by adding a deferred-fallback `Pick` overload taking `Func<SimActor>`, used
only by `WorkerFor`; the eager overload now delegates to it. The eight other call
sites are unchanged.

### 2. The Owner was looked up twice (local agent, product, P3)

`03-program-design.md` specified `MissingBaseDataAsync(accountId, owner, ct)` so
the Owner is "looked up once here and passed in, never queried twice". The
shipped simulation seeder did not do that: the preflight queried internally and
`SeedAsync` queried again for the `ApplicationUser` to act as.

Not a live bug — both lookups are deterministic and a null second result already
returned `Failed` — but two lookups **can** disagree (an Owner disabled or
reassigned between them), and the seeder would then act as a user its own
preflight never approved. Fixed to match the design; `DemoDataSeeder` already
did it correctly.

### 3. `ResolveTenantAndActor` hardcoded `Roles.Owner` (local agent, tests, low)

Inert for every current caller — none drives a handler that reads
`ICurrentUser.Roles`. But it is shared test infrastructure, and a future race
test over a flock-scoped handler would silently receive `FlockScopeGuard`'s Owner
bypass whatever role its acting user actually holds, passing while proving
nothing. Now an optional parameter, with the trap named in the comment.

---

## Confirmed, NOT fixed — recorded instead

### 4. The sales-confirmation `ActAs` is covered by no test (codex, P2)

Verified by mutation: deleting `ActAs(actor)` in `EnsureConfirmedOrderAsync`
leaves the **entire suite green**. The line is correct and load-bearing — on a
re-run where a draft order already exists, `EnsureDraftOrderAsync` returns early
without acting, so without it the confirmation would be authorized and audited as
the inventory phase's manager.

Reaching it needs a fixture where a Draft order survives into a re-run, and the
only way to build one is to force a confirmed order back to Draft — which replays
FIFO allocation against the same lots. The line is correct and cheap; the fixture
that would prove it is neither. **The gap is now stated at the line itself**, so
the next reader does not mistake an uncovered line for a dead one.

---

## Refuted

### 5. "The partial-rerun test cannot observe the bug it names" (pi, claimed SEVERE)

pi reasoned that under the `(Guid.Empty, Guid.Empty)` mutation the eligible pool
still filters to 2 members, so the correct and buggy code pick the same worker.

Wrong: the filter is `Workers.Where(w => w.UserId != cast.RestrictedWorkerId)`,
and the mutation sets `RestrictedWorkerId` to **`Guid.Empty`** — which matches no
worker, so nothing is excluded and the pool is **3**. The rotation index then
lands on a different worker, which is exactly why the mutant dies.

Not an argument: the mutation was run. Mutant applied → red on
`Assert.Equal(expected.Id, create.ActorUserId)` with two differing GUIDs;
restored, rebuilt, green.

### 6. "The test's day index disagrees with the seeder's, and depends on the parity of `HistoryDays`" (pi, claimed HIGH)

Rests on `d = 1` being the oldest seeded day. It is the **newest**:
`SeedFlockHistoryAsync` writes `today.AddDays(-d)` for `d = 1..historyDays`, so
`d` is the day's distance from the anchor and the test's
`anchor.DayNumber - date.DayNumber` **is** that same index. No parity term
exists. The green baseline is independent evidence: were the formula wrong, the
unmutated test would fail.

pi retracted three further findings mid-report and found nothing else. Net yield
from pi this round: **zero**.

---

## Out of scope — pre-existing, not introduced here

- **A Draft entry outside the draft window is never repaired on a re-run**
  (codex, P2). The natural-key check `continue`s before the submit branch, so an
  interrupted prior run can leave an entry Draft that the definition says should
  be Submitted, and the exact-count validation then fails instead of converging.
  Pre-existing #243/#279 behaviour, unchanged by this PR, and consistent with the
  documented "a polluted account fails closed" contract.
- **The worker-assignment idempotency probe tolerates EXTRA assignments**
  (codex, P2). If worker 1 somehow also holds House B, the probe still reports
  the expected pair and the "restricted" worker is not genuinely restricted.
  Pre-existing probe semantics; this PR only added the return value. Note that
  `WorkerFor` still excludes worker 1 from other flocks, so the seed neither
  fails nor misattributes — the fixture is merely less pointed than it claims.
- **The partial-rerun test leaves an orphaned audit row** (codex, P3). The
  deleted entry's `DailyEntry.Create` row survives and now points at nothing.
  Audit rows are in neither the manifest counts nor the fingerprint, and no
  assertion in that fixture reads them, so nothing breaks.
- **`DemoSeedTests` fact ordering** (local agent, tests, low). The claim was that
  `Boot_NeverAutoSeedsDemo_OnlyExplicitSeedAsyncDoes` checks only
  `result.IsSuccess`, which `AlreadySeeded` also satisfies. **Inaccurate** — the
  test asserts `Assert.Equal(SeedStatus.Seeded, result.Status)` on the next line.

---

## One thing the panel could not have found

`SchemaDocsTests.PostgresImagePin_IsOneIdenticalStringAcrossEveryTrackedFile`
walks every **tracked** file, so it only began seeing `docs/plans/` once those
files were staged for this review — after slice 3's green full-suite run. It then
failed on a wrapped prose line in `04-slices.md` beginning `from fire-and-forget
…`, which reads as a Dockerfile `FROM` whose image is off-line. Rewrapped.

Worth knowing generally: **staging a new directory can turn a repo-wide guard
red without any code change**, and a full-suite run taken before `git add`
does not predict the one taken after.
