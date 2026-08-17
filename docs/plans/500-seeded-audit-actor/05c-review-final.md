# Final pre-PR review (2026-08-12)

> **Planning record — seeded audit events carry a real actor ([#500](https://github.com/mforce/cluckwork/issues/500)), August 2026.** What was *intended* at the time, not what shipped. The issue is closed; where this disagrees with the code, the code is right. See [`docs/plans/README.md`](../README.md).

Same four-reviewer panel on the complete diff: codex, pi, and two local agents
(one on product code and docs, one on tests). The mid-point ledger is
[`05a`](05a-review-slices-1-4.md).

**Yield: 5 confirmed defects fixed, 2 recorded, 3 claims refuted.** This round was
worth running: it found a **false claim in an authorization comment that this
plan's own earlier review had already disproved**, which is exactly the failure
mode #500 exists to fix.

---

## P1 — a false claim in `FlockScopeGuard`'s comment, contradicting round 1

The rewritten comment said the `if (!user.IsResolved) return Result.Success();`
branch is now **unreachable**, "because every handler behind this guard also
writes an audit event, and IAuditWriter fails closed on an unresolved actor".

Two of the four handlers behind that guard write **no audit row at all**:
`RecordFeedUsageHandler` and `RecordWaterUsageHandler` (verified — neither
references `IAuditWriter`). For those, an unresolved caller reaches the branch and
is granted **account-wide** access, silently.

**Round 1 of this plan's own review established that fact** — its audited-action
walk lists both handlers among those that audit nothing ([`03a`](03a-review-round-1.md)).
Round 3 then asserted the opposite to justify "unreachable", nothing in between
re-checked it, and the claim shipped into a comment in an authorization path.

Nothing exploits it today: both seeders declare an actor before every feed/water
call, and every HTTP route behind the guard is authenticated. So it is a live gap
for a **future** non-HTTP caller, not a present defect. Fixed by stating the
truth in both the comment and `03-program-design.md`, including that closing the
gap means flipping an authorization default from open to closed — a behaviour
change beyond this issue's scope, which deserves its own issue rather than a
drive-by.

**The lesson is about method, not about this line.** A fact was established, then
contradicted three rounds later by an assertion nobody re-derived. Reviews that
only look forward cannot catch that; this one caught it by reading the plan's own
history against the code.

## P2 — fixed

- **`ResolveSystemActor`'s comment claimed it grants no authorization privilege.**
  False in the other direction: empty roles skips the *role* bypass, but a system
  actor holds no `UserRoleAssignment` rows either, and zero rows is
  `FlockScopeGuard`'s account-wide case. A system actor therefore has **more**
  effective flock reach than a restricted worker. Acceptable only because
  `bootstrap-admin` and `recover-admin` touch no flock-scoped handler — now
  stated, with the condition a future system actor would have to meet.
- **`DemoDataSeeder` passed a literal `[Roles.Owner]`** where the simulation
  seeder fetches from `UserManager`, under a comment saying roles must never come
  from a literal because they are an authorization input. The demo seeder was the
  counter-example to its sibling's own rule. Now fetched.
- **A cast persona whose role changed outside the seeder was silently accepted
  into the wrong pool.** Raised independently by codex and pi, which is why it was
  acted on rather than noted. A `sim-manager-1` demoted to ReadOnly kept authoring
  the manager phases and — holding no assignment rows — passed `FlockScopeGuard`
  on its account-wide branch; the seed then failed much later on a count mismatch
  whose message pointed nowhere near the cause. Now fails immediately, naming the
  user and the missing role, guarded by
  `SimulationReconfiguredCastTests` and pinned by mutation row 17.

## Recorded, not fixed

- **`DemoSeedTests` shares a container with `SeedAndFlockTests`, which seeds its
  own Owner into the same account.** `FindOwnerAsync` takes the lowest Id among
  all of them, so which Owner signs the demo fixture there depends on sibling
  ordering that xUnit does not guarantee. Nothing in that file reads the author,
  so nothing is wrong today — but the natural next edit (an attribution
  assertion, now that `DemoSeedActorTests` exists) would be flaky by
  construction. A warning now sits at the top of the file pointing at the class
  that has its own container for exactly this.
- **Both rotation tests reconstruct the worker pool with `OrderBy(Email)`**, which
  reproduces creation order only while the pool is single-digit
  (`sim-worker-10` sorts before `sim-worker-2`). Safe at the configured 3, and
  the cast-count precondition fails first on any config change that would break
  it. Noted at both sites.

## Refuted

- **"The rotation test mixes a UTC anchor with farm-local dates and flakes at
  UTC midnight"** (pi, HIGH). The seeder writes `date = today.AddDays(-d)` where
  `today` **is** `state.Anchor` — assigned from it on a first run, read back from
  it on every later one. The difference is exactly `d`; there is no timezone term
  anywhere in the expression. `SeedPrimaryTimeZoneAsync` changes the *account's*
  zone, not this anchor.
- **"The partial-rerun test still cannot observe its mutation"** (pi, SEVERE).
  Same error as the mid-point round, from the same reviewer: the filter compares
  against `RestrictedWorkerId`, which the mutation sets to `Guid.Empty`, so it
  excludes nobody and the pool is 3, not 2. The mutation was run; it dies.
- **"The `Pick` fallback should fail rather than degrade for Sales"** (pi). Not a
  defect but a decision already taken and recorded as least-confident decision #5
  in `03-program-design.md`: `Workers = 0` is a deliberately tolerated
  configuration, so the fallback warns rather than refusing. Re-litigating it here
  would be changing an approved design without the owner.

## Where the loop was stopped

This round found real product defects, so the standing "two consecutive rounds
with no product defect" stop condition was not reached. The loop stops here
because the **planned work is complete**, not because the reviewers went quiet —
they never do. Anything they raise after this belongs on the PR.
