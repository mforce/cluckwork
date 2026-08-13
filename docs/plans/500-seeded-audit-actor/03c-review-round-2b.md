# Gate 3 review round 2b — the two re-run reviewers

Round 2's codex pass and implementability agent were killed by a process exit
mid-run and were re-run. Both landed. They found the single most important fact
in the whole review — **independently, and it invalidates a claim that appears in
both `02-architecture.md` and `03-program-design.md`**.

## The finding that changes the design

**`ICurrentUser` is an authorization input, not audit metadata.**

`FlockScopeGuard.CheckAsync` (`Infrastructure/Repositories/UserRoleAssignmentRepository.cs`)
opens with:

```csharp
// Non-HTTP system callers (startup/demo seeders) have no resolved
// user and are account-level by definition; every HTTP route behind
// this guard also requires authentication, so a real request always
// arrives resolved.
if (!user.IsResolved) return Result.Success();
```

`RecordDailyEntryHandler`, `SubmitDailyEntryHandler`, `RecordFeedUsageHandler`
and `RecordWaterUsageHandler` all call it. So the seeders' unresolved actor is
**load-bearing today**: it is a documented authorization bypass, and this design
removes it.

Both docs claimed the opposite — *"handlers never consult flock assignments
(authorization is an endpoint concern)… nothing throws"*. That is false, and
several conclusions rested on it:

1. **`WorkerFor`'s exclusion rule stops being a fidelity nicety and becomes a
   correctness requirement.** Picking the restricted worker for a foreign flock
   now returns `FlockScope.NotAssigned`, `Require(...)` throws, and
   `SeedAsync`'s catch turns the **whole** simulation seed into
   `SeedResult.Failed`. The rule as designed is already correct; what changes is
   that a violation is loud instead of silent — better, but nothing said so.
2. **The mutation table's blast radius was wrong again** (the fifth instance of
   that shape). "Make `WorkerFor` ignore the restriction" does not just
   misattribute rows — it fails the entire seed, so every assertion depending on
   a successful seed goes red. Revision 3 already changed the *method* to "record
   every red test, require the named one among them", which is exactly what this
   case needs.
3. **Ordering is load-bearing and already correct.** `RestrictOneWorkerAsync`
   runs before production history, so worker[0] carries its assignment row by the
   time entries are seeded; workers 1-2 have no rows and stay grandfathered
   account-wide.
4. **Roles on the resolved actor are functional, not cosmetic.** Owner and
   Manager bypass the guard by role. A `SimActor` built with the wrong `Roles`
   list changes authorization behaviour.
5. **That bypass branch becomes unreachable** — every handler behind the guard
   also audits, so an unresolved actor now throws at the audit step first. The
   comment naming "startup/demo seeders" as its justification becomes false and
   must be updated in the same PR.

## Also confirmed and fixed in revision 4

| # | Finding | Found by | Disposition |
|---|---|---|---|
| 1 | **`RestrictOneWorkerAsync`'s idempotent re-run branch has an unspecified return.** The design documents only "(Guid.Empty, Guid.Empty) when no worker exists to restrict". An implementer returning the same for the *already-assigned* short-circuit makes `RestrictedFlockId` empty on every run after the first, so the restricted worker becomes eligible for both flocks — and `SimulationCrossDayRerunTests` cannot catch it, because a fully-converged re-run short-circuits on the natural key before `WorkerFor` is ever evaluated. Latent until a partial re-run. | implementability agent | The signature now specifies **both** early returns: the idempotent branch returns the **existing** pair. |
| 2 | **The order and expense helpers fan in from five call sites with no indexing scheme.** `EnsureDraftOrderAsync` is called twice directly plus once per `EnsureConfirmedOrderAsync`, which is called three times directly plus ~12 times from `SeedRecurringOrdersAsync`; `EnsureExpenseAsync` is the same shape. No `orderIndex` exists in the real code — the design's call stack collapsed all of it into one line and its signature list threaded nothing into the helpers. | implementability agent | Helper signatures and the index source are specified. |
| 3 | **`SeedCastAsync` needs more rewriting than stated**: it discards `EnsureUserAsync`'s return for Managers/Sales/ReadOnly (only Workers accumulate), and `EnsureUserAsync`'s "existing user found by email" branch must now call `UserManager.GetRolesAsync` to rebuild `SimActor.Roles` — which finding #4 above makes functional, not cosmetic. | implementability agent | Stated. |
| 4 | **`ActorUserId` and `ActorEmail` are asserted independently**, so a row pairing one user's id with another's email passes every proposed assertion — while `AuditEventRepository`'s provenance logic keys on `ActorUserId`, so History would name the wrong person. | codex | Assertions now join to the user and require id, email **and** role to agree. |
| 5 | **`Account.UpdateSettings` has no attribution assertion**, and dropping `ActAs(Owner)` before `SeedPrimaryTimeZoneAsync` passes *accidentally* because the previous phase left the Owner resolved. A later reordering silently misattributes it. | codex | Assertion added, plus a mutation row for that phase's `ActAs`. |
| 6 | **Already-seeded databases are never repaired.** Demo exits on the flock guard; simulation's existence guards skip everything; audit rows are in neither the manifest counts nor the fingerprint nor the cross-day snapshot. An existing fixture keeps hundreds of `(unresolved)` rows and is certified `AlreadySeeded`. The plan left the implementer to invent whether this is fresh-database-only. | codex | Stated explicitly as fresh-or-reset, consistent with the issue's "no backfill" scope. |
| 7 | `02-architecture.md` lists "bird movements" under the simulation seeder's flock phase. `SimulationDataSeeder` never injects `RecordBirdMovementHandler` — only the demo seeder produces `Flock.BirdMovement`. | implementability agent | Corrected. |
| 8 | **Cast sizes have no upper bound**, and the seeded work does not scale with them: at the fixture's history length the sales phase creates ~6 orders, so `Simulation:Sales=7` makes "every writing persona authors something" unsatisfiable for a correct implementation, while exact-count validation happily accepts seven users. | codex | The guarantee is narrowed to the configured/default cast, which revision 3 already began by asserting the fixture's counts. |

## Confirmed, nothing new

- **No `Ensure*` helper crosses two phases needing different actor roles** — the
  concern that motivated this review pass. `EnsureUserAsync` is Owner-authored
  throughout; `LoadSaleableGradesAsync` is read-only; order helpers are all
  Sales-authored; inventory and expense helpers all Manager-authored. The real
  fan-in problem is finding #2 above, which is about indexing, not role mixing.
- **The mixed-submitter split is sound.** `DraftWindowDays = 2` and
  `SubmittedByManagerEvery = 3` leave the manager branch reachable — the first
  submittable day, `d = 3`, is already a manager day — and `MinSentinelAgeDays = 9`
  keeps manager days 3, 6, 9 with worker-submitted days between. No
  recorded-but-never-submitted state beyond the intentional Draft window.
- **Submit-by-Manager is safe downstream.** `entry.Submit(...)` takes no actor;
  egg lots, stock movements, mortality movements, lifecycle counts and the
  fingerprint carry no actor field. Managers bypass `FlockScopeGuard` by role.
- **Manifest exact-count validation is unaffected by actor identity.** A scope
  failure prevents the manifest from being computed at all rather than producing
  wrong counts.
