# Program Design: seeded records name a real person (#500)

> **Revision 5** (2026-08-11). Four review passes, 44 confirmed defects — round 3
> added 9 ([`03d`](03d-review-round-3.md)) and, importantly, **closed the class**
> that round 2b opened: two independent exhaustive walks of every `ICurrentUser`
> consumer in `src/` agree that `AuditWriter` and `FlockScopeGuard` are the only
> seeder-reachable ones, and both are handled. Round 3's yield was entirely
> tests, mutations and comments — no product defect.
>
> **Revision 4** (2026-08-11). Three review passes, 35 confirmed defects:
> round 1 ([`03a`](03a-review-round-1.md), 15), round 2 reviewing the fixes
> ([`03b`](03b-review-round-2.md), 12), and round 2b — two reviewers re-run after
> a process exit killed them ([`03c`](03c-review-round-2b.md), 8 plus the one
> that changed the design). All folded in here.
>
> **The design-changing one:** `ICurrentUser` is an authorization input, not
> audit metadata. `FlockScopeGuard` short-circuits on an unresolved actor, so the
> seeders currently run behind a documented authorization bypass that this change
> removes. Revisions 1-3 asserted the opposite. See the worker-eligibility
> section.

## The audited action set — derived, not assumed

Round 1's most expensive finding: **the persona mapping was written against the
handlers the seeders call, and most of those handlers write no audit row.**
Everything below is derived from a walk of every `audit.WriteAsync` call site in
`src/`, so it names only actions that exist.

**Audited, and produced by the simulation seeder:**
`User.Create` (written by `IdentityProvider`, *not* `CreateUserHandler`),
`Flock.Create`, `User.FlockAssign`, the farm-settings update,
`DailyEntry.Create`, `DailyEntry.Submit`, `Product.Create`,
`SalesOrder.Create`, `SalesOrder.AddItem`, `SalesOrder.Confirm`,
`InventoryItem.Adjust`, `Expense.Create`.
The demo seeder additionally produces `Flock.BirdMovement`.

**Called by the seeders and audited by nothing** — an `ActAs` before them is
unobservable and no test can pin it: `CreateCustomer`, `RecordPayment`,
`CreateInventoryItem`, `RecordPurchase`, `RecordFeedUsage`, `RecordWaterUsage`,
`CreateExpenseCategory`. The seeder still declares the right actor before each
(consistency is free, and the guard requires *some* actor), but this design makes
no coverage claim about them. Saying so is the point: the deleted
`Payment.* → sim-sales-` assertion was a guard over an empty set, which reads as
safety and is not.

A consequence worth recording, though it is #494's business and not this issue's:
entities with no audit action (customers, inventory items, payments) render **no
History line at all**.

## Files

**New**

- `src/Cluckwork.Application/Common/SystemActors.cs` — the two non-human actor
  labels, beside `AuditActions.cs`: audit-row vocabulary belongs in one place.
- `tests/Cluckwork.Api.IntegrationTests/AuditActorTests.cs` — the guard on
  `AuditWriter`'s fail-closed behaviour.

**Changed — source**

- `Infrastructure/Repositories/AuditWriter.cs` — throw instead of falling back.
- `Infrastructure/Identity/CurrentUserContext.cs` — add `ResolveSystemActor`.
- `Infrastructure/Persistence/DemoDataSeeder.cs` — Owner preflight + one
  `Resolve`. **Ctor gains `UserManager<ApplicationUser> users` and
  `CurrentUserContext currentUser`.**
- `Infrastructure/Persistence/SimulationDataSeeder.cs` — cast record, per-phase
  actor, empty-pool warning. **Ctor gains `CurrentUserContext currentUser`.**
- `Infrastructure/Identity/FirstRunAdminService.cs` — **ctor gains
  `CurrentUserContext currentUser`**; one call beside its `tenant.Resolve` (line 265).
- `Infrastructure/Identity/AdminRecoveryService.cs` — **ctor gains
  `CurrentUserContext currentUser`**; one call beside its `tenant.Resolve` (line 102).
- `Infrastructure/Identity/IdentityProvider.cs` — comment only. Lines 918-921
  document the `(unresolved)` behaviour that is going away.
- `Infrastructure/Repositories/UserRoleAssignmentRepository.cs` — comment only.
  `FlockScopeGuard`'s `!IsResolved` bypass names "startup/demo seeders" as its
  justification; after this change no seeder reaches it and the branch is
  unreachable for every caller behind the guard. Leaving that comment would be a
  false statement in an authorization path.
- **Three more comments carrying the invalidated "audit metadata only" model**
  (round 3, #8) — leaving them leaves the exact false model that produced three
  revisions of wrong reasoning, at the abstraction boundary where the next
  implementer will read it:
  - `Application/Common/ICurrentUser.cs:3` — "Handlers use it only through
    `IAuditWriter`". False: `FlockScopeGuard` reads it as an authorization input.
  - `Api/Middleware/TenantResolutionMiddleware.cs:6` — describes actor
    resolution as audit-trail work only.
  - `Infrastructure/Identity/CurrentUserContext.cs:5` — "resolved by
    `TenantResolutionMiddleware` on an HTTP request". False once the CLI verbs
    and both seeders resolve their own actors.

All four services are DI-resolved via `GetRequiredService<T>()` and constructed
with `new` nowhere in `src/` or `tests/`, so the widened constructors need no
call-site changes.

**Changed — tests**

- `Infrastructure/TestHarness.cs` — a `WithTenantAndActorScopeAsync` sibling.
- The four direct-scope sites that reach an auditing handler with no actor and
  would now throw: `CurrencyLockRaceTests.cs:167`, `ChangeUserRoleRaceTests.cs:81`,
  `DisableUserRaceTests.cs:82` and `:92`.
- `DemoSeedTests.cs`, `SimulationSeederTests.cs`, `AdminRecoveryServiceTests.cs`,
  `BootstrapAdminCommandTests.cs` — new assertions (below).
- Broken by the demo prerequisite, per the re-confirmed Gate 1 decision:
  `SeedCommandTests.cs:71-90`, `SeedCommandTests.cs:135-145` (including the
  comment stating the old claim), and `DemoSeedTests.cs:99`.

**Docs, same PR** — `deploy/README.md:30-32` and the AGENTS.md seed bullet (root
`README.md` contains no mention of `seed --profile demo` — verified),
`docs/decisions/280-seed-and-simulation.md`,
`docs/decisions/283-first-run-admin-provisioning.md` (the canonical statement of
the `bootstrap-admin` flow whose audit row changes),
`docs/runbooks/break-glass-account-recovery.md`, the AGENTS.md seed bullet.

**Deliberately not changed:** `Application/Common/ICurrentUser.cs`. `AuditWriter`
reads only `IsResolved`/`UserId`/`Email`. A system actor *is* resolved; adding an
`IsSystem` flag would invite a caller to branch on it.

## Types & signatures

```csharp
// src/Cluckwork.Application/Common/SystemActors.cs
public static class SystemActors
{
    public const string BootstrapAdmin = "(bootstrap-admin)";
    public const string BreakGlass = "(break-glass)";
}
```

```csharp
// CurrentUserContext.cs — added
public void ResolveSystemActor(string label);   // UserId = Guid.Empty, Email = label, Roles = []
```

```csharp
// AuditWriter.WriteAsync — the guard replacing lines 33-34.
// The message names what THIS caller can act on: AuditWriter holds only
// ICurrentUser, which does not expose ResolveSystemActor, so telling the caller
// to call it would describe a fix it cannot perform (round 2, #9).
if (!user.IsResolved)
    throw new InvalidOperationException(
        "Audit events require a resolved actor — the current user must be resolved before " +
        "calling IAuditWriter. A non-HTTP caller (CLI verb, seeder) must declare one: a real " +
        "user, or a system actor via the concrete CurrentUserContext.");

var actorId = user.UserId;      // AuditEvent's parameter; the column is ActorUserId
var actorEmail = user.Email;
```

```csharp
// SimulationDataSeeder.cs
private sealed record SimActor(Guid UserId, string Email, IReadOnlyList<string> Roles);

private sealed record SimCast(
    SimActor Owner,
    IReadOnlyList<SimActor> Managers,
    IReadOnlyList<SimActor> Sales,
    IReadOnlyList<SimActor> Workers,
    Guid RestrictedWorkerId,   // Guid.Empty until RestrictOneWorkerAsync reports the pair
    Guid RestrictedFlockId);

// TOTAL for every pool, including an empty one — Managers/Sales/Workers may all
// be 0 (SimulationOptions validates no count, and the seeder already tolerates
// Workers == 0), and `pool[index % pool.Count]` would divide by zero.
//
// The fallback is a fidelity DEGRADATION, so it does not happen quietly: the
// caller logs a warning naming the pool and the substitute actor (round 2, #6 —
// AGENTS.md "no silent caps"). Crashing instead would be worse; hiding it would
// be the same class of defect this issue reports.
private SimActor Pick(IReadOnlyList<SimActor> pool, int index, SimActor fallback, string poolName);

private SimActor WorkerFor(SimCast cast, Guid flockId, int dayIndex);

// ActAs is a pure assignment. CurrentUserContext.Resolve stores the three
// parameters VERBATIM and sets IsResolved — no database read, no role re-fetch.
// Stated rather than described as "what the middleware does" (round 3): the
// FlockScopeGuard miss happened precisely because a mechanism was characterised
// instead of walked, and WorkerFor's whole eligibility argument depends on
// SimActor.Roles being exactly what the guard later reads.
private void ActAs(SimActor actor);   // currentUser.Resolve(actor.UserId, actor.Email, actor.Roles)

// Returns the cast; the restricted pair is Guid.Empty at this point.
private async Task<SimCast> SeedCastAsync(Guid accountId, SimulationOptions sim, CancellationToken ct);

private async Task<SimActor> EnsureUserAsync(
    Guid accountId, string email, string role, string name, string password, CancellationToken ct);

// Returns the pair it restricted, so the caller can rebuild the record:
//   cast = cast with { RestrictedWorkerId = w, RestrictedFlockId = f };
//
// BOTH early returns must be specified, because getting the second one wrong is
// a latent re-run bug no existing test can catch (round 2b, #1):
//   - no worker exists to restrict     → (Guid.Empty, Guid.Empty)
//   - the assignment ALREADY EXISTS    → the EXISTING pair, never Guid.Empty.
// Returning Empty from the idempotent branch leaves RestrictedFlockId empty on
// every run after the first, so `flockId == cast.RestrictedFlockId` never
// matches and the restricted worker becomes eligible for every flock — which
// now FAILS THE SEED via FlockScopeGuard. A fully converged re-run hides it
// (the natural-key check short-circuits before WorkerFor is reached); it
// surfaces only on a partial re-run that adds days.
private async Task<(Guid WorkerId, Guid FlockId)> RestrictOneWorkerAsync(
    Guid accountId, SimCast cast, IReadOnlyList<Guid> flockIds, CancellationToken ct);

// SeedCastAsync needs more rewriting than "return SimCast" suggests: today it
// discards EnsureUserAsync's return for Managers/Sales/ReadOnly (only Workers
// accumulate), so three of four loops change. And EnsureUserAsync's "existing
// user found by email" branch must call UserManager.GetRolesAsync to rebuild
// SimActor.Roles.
//
// Why, precisely (round 3, #7 — revision 4's stated reason was wrong): it is
// NOT that an empty Roles list would fail FlockScopeGuard for a Manager. A
// Manager persona carries no UserRoleAssignment row, so an empty Roles list
// still passes, via the guard's "zero assignment rows = unscoped" fallback
// rather than its role check. The requirement stands because SimActor.Roles is
// a functional authorization input wherever assignment rows DO exist, and
// because an actor reconstructed differently on a re-run than on a first run is
// exactly the silent divergence this design exists to prevent.

// The order and expense helpers fan in from five call sites and have no index
// of their own (round 2b, #2). Each takes the actor explicitly rather than
// inventing an index inside: the CALLER owns the rotation.
private async Task<Guid> EnsureDraftOrderAsync(…, SimActor actor, CancellationToken ct);
private async Task<Guid> EnsureConfirmedOrderAsync(…, SimActor actor, CancellationToken ct);
private async Task EnsurePartialPaymentAsync(…, SimActor actor, CancellationToken ct);
private async Task SeedRecurringOrdersAsync(…, SimCast cast, …, CancellationToken ct);   // rotates on its own i
private async Task EnsureExpenseAsync(…, SimActor actor, CancellationToken ct);
private async Task SeedRecurringExpensesAsync(…, SimCast cast, …, CancellationToken ct); // rotates on its own i

// These four were the last call sites still riding ambient actor state while
// every other helper took an explicit actor (round 3, #6) — an inconsistency
// with this design's own rule that ambient actor state is the bug class #500 is
// about. Correct today only by inspection, and pinned by no mutation row.
private async Task SeedFeedUsageAsync(…, SimActor actor, CancellationToken ct);
private async Task SeedWaterUsageAsync(…, SimActor actor, CancellationToken ct);
private async Task<Guid> EnsureOpeningPurchaseAsync(…, SimActor actor, CancellationToken ct);
private async Task EnsureAdjustmentAsync(…, SimActor actor, CancellationToken ct);

// Phases take the cast explicitly — ambient actor state fails silently wrong,
// which is this issue's own bug class.
private async Task SeedProductionHistoryAsync(
    Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, SimCast cast,
    SimulationOptions sim, CancellationToken ct);
private async Task SeedFlockHistoryAsync(
    Guid accountId, Guid flockId, int baseline, DateOnly today, int historyDays,
    IReadOnlyDictionary<string, Guid> grades, SimCast cast, CancellationToken ct);
private async Task SeedSalesAsync(
    Guid accountId, DateOnly today, SimCast cast, SimulationOptions sim, CancellationToken ct);
private async Task SeedInventoryOperationsAsync(
    Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, SimCast cast,
    SimulationOptions sim, CancellationToken ct);

// How often a Manager, rather than the recording Worker, submits the entry.
private const int SubmittedByManagerEvery = 3;
```

```csharp
// Both seeders — the preflight must HAND BACK the Owner. MissingBaseDataAsync
// returns bool and discards the owners it queried today, so the Owner is looked
// up once here and passed in, never queried twice.
//
// BOTH run before tenant.Resolve, so BOTH must ignore the tenant query filter,
// or the preflight and the lookup can disagree about whether an Owner exists
// (round 2, #12). FindOwnerAsync goes through UserManager.GetUsersInRoleAsync
// exactly as SimulationDataSeeder.MissingBaseDataAsync already does.
//
// TWO THINGS THAT MUST BE SPELLED OUT, not left to "match the existing
// mechanism" (round 3, #5 — the earlier wording said exactly that and said
// nothing about either):
//
//   1. GetUsersInRoleAsync IS NOT ACCOUNT-SCOPED. It takes a role name and
//      returns Owners across EVERY account; the existing callers filter in
//      memory (`owners.Any(u => u.AccountId == accountId)`). FindOwnerAsync
//      MUST apply the same AccountId filter. Taking .First() off the raw result
//      attributes the entire demo fixture to another tenant's Owner — and
//      nothing looks wrong, because the History line renders ActorEmail off the
//      audit row rather than through a join.
//   2. The choice among several Owners MUST BE DETERMINISTIC (order by Id).
//      A fixture whose attribution varies run to run breaks the determinism
//      contract the whole seeder rests on (#279).
private async Task<ApplicationUser?> FindOwnerAsync(Guid accountId, CancellationToken ct);
private async Task<bool> MissingBaseDataAsync(Guid accountId, ApplicationUser? owner, CancellationToken ct);
```

## Call stack

**Demo** — `DemoDataSeeder.SeedAsync`

```
SeedAsync(ct)
├─ owner ← FindOwnerAsync(accountId)          NEW
├─ MissingBaseDataAsync(accountId, owner)     owner is null ⇒ PrerequisitesMissing("… run bootstrap-admin …")
├─ anyFlocks guard                            (unchanged)
├─ tenant.Resolve(accountId)                  (unchanged)
├─ currentUser.Resolve(owner.Id, owner.Email!, [Roles.Owner])   NEW
└─ SeedDemoAsync(...)                         (unchanged — every audited handler now stamps the Owner)
```

**Simulation** — `SimulationDataSeeder.SeedAsync`

```
SeedAsync(ct)
├─ CastPassword guard                          (unchanged)
├─ owner ← FindOwnerAsync(accountId)           NEW — replaces the discarded lookup inside the preflight
├─ MissingBaseDataAsync(accountId, owner)      (unchanged otherwise)
├─ tenant.Resolve(accountId)                   (unchanged)
├─ ActAs(ownerActor)                           NEW — covers the cast and anything before its own ActAs
├─ cast ← SeedCastAsync                        User.Create × cast, authored by the Owner
├─ SeedFlockTopologyAsync                      ActAs(Pick(Managers, flockIndex, Owner)) per flock → Flock.Create
├─ SeedPrimaryTimeZoneAsync                    ActAs(Owner) → Account.UpdateSettings
│    REORDERED (round 3, #1): this now follows a MANAGER-authored phase. It used
│    to follow RestrictOneWorkerAsync, which leaves the Owner resolved — so
│    deleting this phase's own ActAs(Owner) changed nothing observable and the
│    mutation pinning it was a no-op. The two phases are order-independent of
│    each other, and the timezone still precedes every dated fixture row.
├─ (w, f) ← RestrictOneWorkerAsync             ActAs(Owner) → User.FlockAssign
│    cast = cast with { RestrictedWorkerId = w, RestrictedFlockId = f }
├─ SeedProductionHistoryAsync
│   └─ SeedFlockHistoryAsync (per flock, per day d)
│       ├─ ActAs(WorkerFor(cast, flockId, d))                    → DailyEntry.Create
│       └─ d % SubmittedByManagerEvery == 0
│            ? ActAs(Pick(Managers, d, Owner))                   → DailyEntry.Submit  (approved)
│            : (the recording worker stays resolved)             → DailyEntry.Submit  (self-submitted)
├─ SeedInventoryOperationsAsync                ActAs(Pick(Managers, …, Owner))
│   │                                          only InventoryItem.Adjust is audited here
│   └─ SeedExpensesAsync                       ActAs(Pick(Managers, …, Owner)) → Expense.Create
├─ SeedSalesAsync
│   ├─ SeedProductCatalogAsync                 ActAs(Pick(Managers, …, Owner)) → Product.Create
│   ├─ SeedCustomersAsync                      ActAs(Pick(Sales, …, Owner))    — audits nothing
│   └─ orders / items / confirm / payments     ActAs(Pick(Sales, orderIndex, Owner))
│                                              → SalesOrder.Create/AddItem/Confirm (payments audit nothing)
└─ SeedSecondAccountAsync                      untouched — raw db.Accounts.Add, no audit row
```

**Worker eligibility** (`WorkerFor`). `RestrictOneWorkerAsync` narrows
`Workers[0]` to `flockIds[0]`, so that worker must never author on another flock.

**This is a correctness requirement, not a fidelity nicety** — revisions 1-3 said
the opposite and were wrong. `FlockScopeGuard.CheckAsync`
(`Infrastructure/Repositories/UserRoleAssignmentRepository.cs`) begins
`if (!user.IsResolved) return Result.Success();`, with a comment naming
"startup/demo seeders" as the reason. `RecordDailyEntryHandler`,
`SubmitDailyEntryHandler`, `RecordFeedUsageHandler` and `RecordWaterUsageHandler`
all call it, so **the seeders' unresolved actor is today a documented
authorization bypass, and this design removes it.** Consequences:

- Picking the restricted worker for a foreign flock returns
  `FlockScope.NotAssigned`; `Require(...)` throws; `SeedAsync`'s catch turns the
  **whole** seed into `SeedResult.Failed`. Loud, not silent — better than what
  earlier revisions assumed, but it must be designed for deliberately.
- **`SimActor.Roles` is functional, not cosmetic.** Owner and Manager bypass the
  guard *by role*, so a `SimActor` carrying the wrong roles changes
  authorization behaviour, not just an audit label.
- Ordering is load-bearing and already correct: `RestrictOneWorkerAsync` runs
  before production history, so worker[0] carries its assignment row by the time
  entries are seeded, and workers 1-2 have no rows and stay account-wide
  (the guard grandfathers a worker with no assignments).
- That `!IsResolved` branch's comment becomes false and is updated in this PR.

  > **CORRECTED after the final review (2026-08-12).** This bullet claimed the
  > branch becomes **unreachable** because "every handler behind the guard also
  > audits". That is false, and this document's own round-1 review had already
  > recorded why: `RecordFeedUsage` and `RecordWaterUsage` **write no audit row**
  > ([`03a`](03a-review-round-1.md), the audited-action walk). So the branch is
  > unreachable for `RecordDailyEntry`/`SubmitDailyEntry` only; for the other two
  > an unresolved caller still reaches it and is granted account-wide access.
  >
  > Nothing exploits it today — both seeders declare an actor before every
  > feed/water call, and every HTTP route behind the guard is authenticated — so
  > it is a live gap for a future non-HTTP caller rather than a present defect.
  > Worth recording how it happened: round 1 established the fact, round 3
  > asserted its opposite to justify "unreachable", and no round in between
  > re-checked. The claim then shipped into a comment in an authorization path,
  > which is precisely the failure mode this whole issue is about.

```csharp
var eligible = flockId == cast.RestrictedFlockId
    ? cast.Workers                                   // the restricted one belongs here
    : cast.Workers.Where(w => w.UserId != cast.RestrictedWorkerId).ToList();  // materialize: Pick takes IReadOnlyList
return Pick(eligible, dayIndex, Pick(cast.Managers, dayIndex, cast.Owner, "Managers"), "Workers");
```

**Eligibility is per-flock, and the counts differ per flock.** At the default
cast (`Workers = 3`) the restricted flock has 3 eligible workers and every other
flock has 2. Any test asserting the rotation must use the per-flock count, not a
single pool size — see the rotation test below.

Total by construction: `Pick` handles every empty pool and the Owner always
exists, because `MissingBaseDataAsync` refuses to seed without one.

**bootstrap-admin** — `FirstRunAdminService.ProvisionAsync`, after
`tenant.Resolve(accountId)` (line 265):
`currentUser.ResolveSystemActor(SystemActors.BootstrapAdmin)`.

**recover-admin** — `AdminRecoveryService.RecoverAsync`, after
`tenant.Resolve(target.AccountId)` (line 102):
`currentUser.ResolveSystemActor(SystemActors.BreakGlass)`.

Round 1 verified against the source that in both, resolution precedes every
audit-touching statement, and that the new throw fires before any `AddAsync`
inside an `AmbientTransaction` that commits only on success — so a violation
rolls back rather than half-writing.

## Test plan

**Scoping — different for the two seeders, and for a reason.**

- **Demo:** the new audit-actor assertions run against a **delta** — capture the
  account's existing `AuditEvent` ids, then call `SeedAsync`, then assert only
  over rows that appeared. `DemoSeedTests` calls `SeedAsync` inside the `[Fact]`
  body, so this is possible, and it shares a container with other tests that
  touch `SeedDefaults.AccountId`, so it is necessary.
- **Simulation:** **no delta** — it is both impossible and unnecessary.
  `SimulationSeedFactory` seeds in `InitializeAsync`, before any `[Fact]` runs,
  so no test body can observe a "before" state. It is unnecessary because that
  fixture owns its own Postgres container and `TestHarness.SeedUserAsync` creates
  its Owner through raw `UserManager.CreateAsync`, bypassing `IdentityProvider`
  — writing no audit row. Every `AuditEvent` in that account came from the
  seeder. **Recorded so this is not "fixed" back into a delta later.**

This scoping applies only to the new audit-actor assertions; the file's existing
count/manifest assertions are untouched and need no scoping.

**Configuration precondition.** Every persona assertion below is true of the
*configured* cast, not of all configurations — at `Simulation:Workers=0` a
correct `WorkerFor` attributes entries to a Manager. So the suite first asserts
the fixture's own counts (`Workers=3, Managers=1, Sales=1, ReadOnly=4`). A config
change then fails loudly instead of silently changing what every other assertion
means.

**`AuditActorTests.cs`** (new)

- `WriteAsync_WithUnresolvedActor_Throws` — tenant resolved, actor not. Asserts
  the message names the **actor**, so the pre-existing tenant guard firing
  instead cannot satisfy it.
- `WriteAsync_WithUnresolvedActor_AddsNothingToTheChangeTracker` — asserts the
  change tracker holds no added `AuditEvent`. Querying the table would pass for
  a guard placed *after* `AddAsync`, because `AuditWriter` never saves.
- `WriteAsync_WithSystemActor_StampsTheLabelAndEmptyActorUserId` — the contract
  both CLI paths depend on.

**`DemoSeedTests.cs`**

- `DemoSeed_WithNoOwner_FailsClosed` — **needs its own factory and container**,
  the way `SimulationSeedFactory` already has one. `DemoSeedTests` shares one
  container and `SeedDefaults.AccountId` across its facts, with no
  `ITestCaseOrderer` configured anywhere in the project, and its two existing
  facts already document that fragility. Run after the fixture-populating fact,
  this test would find the pre-existing Owner and get `AlreadySeeded` instead of
  `PrerequisitesMissing` — failing for ordering reasons unrelated to the guard
  under test (round 3, #4). Do not rely on xUnit's default sequencing.

  The fixture seeds a lone **Manager** and no Owner, so a `FindOwnerAsync` that
  merely checks "any user exists" fails it.
  Asserts `PrerequisitesMissing`, that the message names `bootstrap-admin`, and
  that **no flock rows were written** — the message alone would pass for a
  seeder that had already written half a farm.
- `DemoSeed_AttributesEverySeededAuditEventToTheOwner` — over the delta,
  `Assert.All`: every row has `ActorEmail == owner.Email` **and**
  `ActorUserId == owner.Id`. Positive form; `!= "(unresolved)"` would pass for
  any wrong-but-non-placeholder value.

**`SimulationSeederTests.cs`**

- `SimulationSeed_LeavesNoUnattributedAuditEvent` — no `(unresolved)` label and
  no `Guid.Empty` actor id. Two clauses; label and id can drift apart.
- `SimulationSeed_AttributesEachAuditedActionToItsPersona` — **`Assert.All` per
  exact action**, never one existence check across a family (a family check
  passes when `SalesOrder.Create` is right and `AddItem`/`Confirm` carry a stale
  actor). `DailyEntry.Create` → a `sim-worker-` address; `SalesOrder.Create`,
  `SalesOrder.AddItem`, `SalesOrder.Confirm` each → `sim-sales-`; `Flock.Create`,
  `Product.Create`, `Expense.Create`, `InventoryItem.Adjust` each →
  `sim-manager-`; `User.Create`, `User.FlockAssign` **and
  `Account.UpdateSettings`** → the Owner. The settings row must be listed
  explicitly: dropping `ActAs(Owner)` before `SeedPrimaryTimeZoneAsync` passes
  *accidentally* today, because the preceding phase leaves the Owner resolved —
  so a later reordering would misattribute it silently (round 2b, #5).
- `SimulationSeed_ActorIdentityIsInternallyConsistent` — for every human-authored
  seeded row, `ActorUserId` and `ActorEmail` must resolve to the **same**
  `ApplicationUser`. Asserting the two independently passes for a row pairing one
  user's id with another's email.

  *What such a mismatch actually costs* (round 3 corrected revision 4's
  overstatement here): the id never reaches the UI — `EntityProvenance` carries
  only `CreatedByEmail`/`LastChangedByEmail`. But
  `AuditEventRepository.GetProvenanceChunkAsync`'s creator and
  self-promotion-exclusion SQL genuinely compares `ActorUserId`, so a mismatch
  makes it select the **wrong row**, whose email is then displayed as
  authoritative. A real bug, and a different one from "names the wrong person".

  *The role clause, stated per action* — a single "holds the role the action
  implies" rule is unsatisfiable, because Workers deliberately carry **no** role
  row (`"Worker"` is a pseudo-role the handler maps to null) and
  `DailyEntry.Submit` may be either a role-less Worker or a Manager (round 3, #3):
  - `DailyEntry.Create` → the actor holds **zero** stored roles.
  - `SalesOrder.*` → `Sales`. `Flock.Create`, `Product.Create`, `Expense.Create`,
    `InventoryItem.Adjust` → `Manager`. `User.Create`, `User.FlockAssign`,
    `Account.UpdateSettings` → `Owner`.
  - `DailyEntry.Submit` → zero roles **if** the actor equals that entry's create
    actor, otherwise `Manager`. Never `Owner`.
- `SimulationSeed_SubmitIsEitherTheRecordingWorkerOrAManager` — for every
  `DailyEntry.Submit`, the actor is either that entry's own `DailyEntry.Create`
  actor or a `sim-manager-` address, and **never the Owner**; and at least one
  row of each kind exists. Without this, replacing
  `ActAs(Pick(Managers, d, Owner))` with plain `ActAs(Owner)` is caught by
  nothing — "the actors differ" is still satisfied.
- `SimulationSeed_WritingPersonasAuthorSomething_AndReadOnlyAuthorsNothing` —
  every Manager, Sales and Worker persona appears as an actor at least once, and
  every `sim-readonly-` persona appears **never**. ReadOnly personas exist (4 by
  default) and no phase makes them act; asserting that exclusion is what makes it
  deliberate rather than forgotten (#407). The positive clause is near-vacuous
  for Managers and Sales at the default pool size of 1 — stated, not hidden.
- `SimulationSeed_WorkerRotationIsExactAndDeterministic` — the only assertion
  that can fail for a `Pick` returning **any constant** or a **random** element.
  Three things it must get right, each of which round 2 caught:
  - **Which flock.** Identify the restricted flock by joining
    `UserRoleAssignments.FlockId` — the pattern
    `SimulationSeed_RestrictsExactlyOneWorkerToOneOfTwoFlocks` already uses —
    never by list index, which depends on unordered Postgres return.
  - **Which count.** Eligible is 3 on the restricted flock and 2 elsewhere.
    Assert both flocks, each against its own count.
  - **How many samples.** Across the **full seeded history** (90 days by
    default), not one cycle. A random `Pick` then survives with probability
    (1/3)⁹⁰ rather than the (1/3)⁴ ≈ 1.2% a four-day check would allow. This is
    a probabilistic kill, not a certain one; the number is stated rather than
    claimed away.
- `SimulationSeed_RestrictedWorkerAuthorsOnlyItsAssignedFlock` — two clauses:
  the restricted worker authors **at least one** `DailyEntry.Create` on its
  assigned flock, and **none** on any other. The positive clause is what stops it
  passing vacuously for an implementation where that worker never acts at all.
  Meaningful only at `Workers > 1`; the suite runs at the default 3.
- `SimulationSeed_PartialRerunReconstructsWithAnEligibleWorker` — **new, and the
  only test that can reach the failing state of the `RestrictOneWorkerAsync`
  idempotent branch.** Delete one daily entry on the *non*-restricted flock,
  re-run the seeder, and assert (a) the seed succeeds and (b) the replacement
  entry's actor is an eligible worker — i.e. not the restricted one. Round 3
  established that no such scenario exists anywhere in `tests/`:
  `SimulationSeedFactory` seeds once, and `SimulationCrossDayRerunTests`
  deliberately reuses the durable anchor so the day range is identical and the
  natural-key check short-circuits before `WorkerFor` is ever evaluated. Without
  this test, returning `(Guid.Empty, Guid.Empty)` from that branch stays green
  forever, and the failure surfaces only in a real partial re-run — where it
  fails the whole seed via `FlockScopeGuard`.
- `SimulationSeed_CarriesBothProvenanceShapes` — at least one submitted entry
  whose `DailyEntry.Create` and `DailyEntry.Submit` actors are the **same**
  person, and at least one where they **differ**. Both clauses; either alone
  passes for a fixture that mixed nothing.

**`AdminRecoveryServiceTests.cs`** — the break-glass row's actor is
`SystemActors.BreakGlass` and the `host`/`osUser` details are still present
(guards against "fixed the actor, dropped the accountability").

**`BootstrapAdminCommandTests.cs`** — the first Owner's `User.Create` row is
attributed to `SystemActors.BootstrapAdmin`.

### Mutation check — the method, which changed

Round 1 caught one table row that undercounted which tests a mutation kills;
round 2 caught **three more**. Four misses of one shape means the method was
wrong, not the list (AGENTS.md). The method was *write the test, then claim it is
the one that dies*. It is now:

1. Apply the mutation and rebuild — **never `--no-build`**, which re-runs the
   previous binary and yields a false green.
2. Run the full suite and **record every test that goes red**.
3. Require the named assertion to be **among** them. The table's named test is a
   **minimum, not the complete set** — a row may list one test and still be
   satisfied by five going red.
4. Restore, rebuild, and confirm the baseline is green before claiming anything.
5. **No row may be written before its mutation has been run.** The table below is
   the plan for that run, not a record of it.

| Mutation | Named assertion that MUST be among the red |
|---|---|
| revert `AuditWriter` to the `(unresolved)` fallback | `AuditActorTests` ×**2** — `…_Throws` and `…AddsNothingToTheChangeTracker`. **Not ×3**: `WriteAsync_WithSystemActor_…` uses a *resolved* actor, and the old ternaries preserve both its label and `Guid.Empty`, so it stays green |
| remove only the actor guard, keep the tenant guard | `WriteAsync_WithUnresolvedActor_Throws` |
| move the actor guard to after `AddAsync` | `…AddsNothingToTheChangeTracker` |
| drop every `ActAs` in `SeedFlockHistoryAsync` | `…AttributesEachAuditedActionToItsPersona` |
| make `WorkerFor` ignore the restriction — note this now **fails the whole seed** via `FlockScopeGuard`, so expect the suite-wide red, not one test | `…RestrictedWorkerAuthorsOnlyItsAssignedFlock` |
| return `(Guid.Empty, Guid.Empty)` from `RestrictOneWorkerAsync`'s idempotent branch | `…PartialRerunReconstructsWithAnEligibleWorker` — **and only that test**; every other test seeds once or converges, so all of them stay green |
| drop `ActAs(Owner)` before `SeedPrimaryTimeZoneAsync` | `…AttributesEachAuditedActionToItsPersona` (the `Account.UpdateSettings` clause) — **valid only after the reorder above**; while that phase followed `RestrictOneWorkerAsync` this mutation was a no-op, because the Owner was already resolved |
| stamp a valid but wrong `ActorUserId` beside a correct `ActorEmail` | `…ActorIdentityIsInternallyConsistent` |
| make `Pick` return a constant element | `…WorkerRotationIsExactAndDeterministic` |
| make `Pick` choose randomly | `…WorkerRotationIsExactAndDeterministic` |
| replace the manager-submit branch with `ActAs(Owner)` | `…SubmitIsEitherTheRecordingWorkerOrAManager` |
| drop demo's `FindOwnerAsync` preflight | `DemoSeed_WithNoOwner_FailsClosed` |
| make the preflight accept any user, not an Owner | `DemoSeed_WithNoOwner_FailsClosed` |
| make every entry self-submitted | `…CarriesBothProvenanceShapes` |
| make every entry manager-submitted | `…CarriesBothProvenanceShapes` |

## Gate 1 decision A — re-confirmed 2026-08-11 after the round-1 finding

Gate 1 approved option A: demo requires an Owner, same as simulation. Round 1
found this is not merely an undocumented habit — it is a contract pinned by a
test whose comment says so. `SeedCommandTests.cs` spawns the real CLI against
databases where `bootstrap-admin` never ran:
`SeedCommand_Demo_SeedsDataAndExitsWithoutStartingKestrel` (71-90) and
`SeedCommand_Demo_AgainstAnUntouchedDatabase_MigratesAndSeedsInOneStep`
(135-145), whose comment states a green result *"proves `seed --profile demo`
needs nothing but a connection string"*. `DemoSeedTests.cs:99` breaks too.

**The owner re-confirmed A with that cost on the table.** Two alternatives were
rejected: *Owner-if-present, else a `(demo-seed)` system actor* preserves the CLI
contract but reintroduces a parenthetical placeholder on ~256 rows across the
five #494 screens as soon as someone runs `bootstrap-admin` and logs in; *the
seeder mints its own demo Owner* preserves it too, but `bootstrap-admin` only
provisions when the default account has **no** Owner, so it would become a silent
no-op and leave the developer unable to provision an admin whose password they
know.

The reasoning to record in the replaced comment: a demo fixture exists to be
looked at, looking requires a login, and a login requires an Owner — so the
prerequisite converts a later surprise into an immediate, clearly-worded failure.

## What this does NOT do: existing databases are not repaired

A database that already carries seeded `(unresolved)` rows keeps them. Demo
exits on its flock guard; simulation's existence guards skip every already-
present entity; and audit rows appear in neither the manifest counts, nor the
fingerprint, nor `SimulationCrossDayRerunTests`' snapshot — so a completed
fixture is certified `AlreadySeeded` and nothing is rewritten.

**The fix applies to a fresh or reset database only.** That is consistent with
the issue, which puts backfilling pre-existing provenance out of scope, but it
was left implicit and an implementer would otherwise have had to invent it.
`tools/simulation/reset.sh` is the supported way to get a repaired fixture.

## Least confident decisions

1. **Both submitter shapes, deterministically mixed** (owner's call). The least
   confident part is the *ratio*: `SubmittedByManagerEvery = 3` is a guess at a
   farm's real approval rate, and no test fails if it becomes 1:1.
2. **`(bootstrap-admin)` / `(break-glass)` are still parenthetical placeholders**
   — the shape the issue complains about. Defensible: they are honest, they are
   not "unresolved", and neither row renders on any of the five #494 screens.
   Round 1 pushed back that pinning them in a test turns a known compromise into
   an asserted contract.
3. **`ResolveSystemActor` is callable from anywhere**, so a future HTTP caller
   could declare itself a system actor and launder an unattributable write.
   Nothing ties it to CLI-only callers and this design proposes no guard for that
   boundary — the same is true of `Resolve`, but that is an argument about
   symmetry, not safety.
4. **Manager and Sales rotation is untested** at the fixture's own counts (pool
   size 1), and the design accepts that rather than adding a second fixture that
   would ripple into the manifest's exact-count validation.
5. **The `Pick` fallback degrades rather than failing.** It warns, but a
   simulation fixture whose whole purpose is fidelity arguably ought to refuse to
   seed a cast that cannot support attribution. Warning was chosen because
   `Workers = 0` is already a deliberately tolerated configuration.
