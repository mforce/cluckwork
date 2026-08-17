# Gate 3 review round 3 — findings and dispositions

> **Planning record — seeded audit events carry a real actor ([#500](https://github.com/mforce/cluckwork/issues/500)), August 2026.** What was *intended* at the time, not what shipped. The issue is closed; where this disagrees with the code, the code is right. See [`docs/plans/README.md`](../README.md).

Four reviewers on revision 4: a `pi` contrarian pre-mortem, a codex pass, and two
Claude Sonnet agents (an exhaustive `ICurrentUser` consumer walk, and a test-plan
re-review). All four reported.

## The class opened by round 2b is now closed — by two independent walks

Round 2b found that `FlockScopeGuard` makes `ICurrentUser` an authorization
input, invalidating a claim revisions 1-3 made in two documents. The obvious
follow-up question — *what else is like that?* — was answered by **walking, not
listing** (AGENTS.md's rule after a "list what I thought of" miss), twice, with
different tools.

Both walks independently produced the **same inventory of 8 consumers**, and the
same classification:

- **Reachable from a seeder, behaviour changes, plan handles it:** `AuditWriter`
  and `FlockScopeGuard`. Only those two.
- **Unreachable from a seeder:** `TenantResolutionMiddleware`,
  `IdempotencyMiddleware`, and the Auth/Me/User endpoint delegates — seeders call
  `HandleAsync` directly with an explicit actor id and never traverse a
  minimal-API delegate or the HTTP pipeline. Plus the DI registration, which
  wires rather than consumes.
- **Behaviour changes and the plan does NOT handle it:** *nothing*.

Nothing else reads `.Roles`; `FlockScopeGuard`'s
`UserRoleAssignments.Where(a => a.UserId == user.UserId)` is the only query in
`src/` filtered by the current user. For demo specifically, resolving an Owner
moves `FlockScopeGuard` from its unresolved bypass to its Owner-role bypass —
both return success before querying assignments — so **no demo fixture content
changes**, only attribution.

Deliberate exclusions, recorded so the next walk need not re-litigate them:
`IHttpContextAccessor` reads of `HttpContext.User` (a different mechanism);
persisted `ApplicationUser.Email`/`.Role` fields on *target* users; the
`command.Role == Owner` step-up gate (keyed off a plain `Guid actingUserId`, and
seeders never call those handlers); `AssignFlockHandler` (not behind the guard).

## Two claims checked and refuted

- **`Resolve` does not re-read roles.** The contrarian's top-severity item was
  that if `Resolve` re-fetched roles from the database, `SimActor.Roles` would be
  decorative and the whole eligibility argument would collapse.
  `CurrentUserContext.Resolve` stores its three parameters verbatim and sets
  `IsResolved` — no DB read. Refuted. The *procedural* criticism stands and is
  actioned: revision 5 states the mechanism rather than asserting "the same as
  the middleware", which is precisely the shape of the FlockScopeGuard miss.
- **Round 2b finding #4's prose oversold its consequence.** An `ActorUserId`
  that disagrees with `ActorEmail` does **not** make the History line "name the
  wrong person" through the id: `EntityProvenance` carries only
  `CreatedByEmail`/`LastChangedByEmail`, and the id never reaches the UI. What
  the mismatch actually does is make
  `AuditEventRepository.GetProvenanceChunkAsync`'s creator and
  self-promotion-exclusion SQL — which genuinely does compare `ActorUserId` —
  select the **wrong row**, whose email is then displayed as authoritative. A
  real bug, a different one. The test remains worth writing; the justification is
  corrected.

## Confirmed and fixed in revision 5

| # | Finding | Found by | Disposition |
|---|---|---|---|
| 1 | **The `Account.UpdateSettings` mutation row cannot catch its own mutation, and revision 4 contradicts itself about it.** The doc says the assertion passes accidentally because the preceding phase leaves the Owner resolved — then claims dropping that phase's `ActAs(Owner)` turns it red. `CurrentUserContext` is mutable scoped state with no reset between phases, so deleting a redundant `ActAs(Owner)` changes nothing observable. | codex, test agent | **Reordered:** `RestrictOneWorkerAsync` now runs *after* `SeedPrimaryTimeZoneAsync`, so the phase preceding settings is Manager-authored and the mutation is genuinely caught. The two are order-independent of each other, and the timezone still precedes every dated fixture row. |
| 2 | **The partial-rerun mutation has no test that can reach its failing state.** Greps of `tests/` find no growing-history rerun anywhere; `SimulationSeedFactory` seeds once, and `SimulationCrossDayRerunTests` deliberately reuses the durable anchor so the day range is identical and the natural-key check short-circuits before `WorkerFor` is evaluated. Running the design's own prescribed method against that mutation today yields an all-green suite. | contrarian, codex, test agent | A concrete test is specified: delete one foreign-flock entry, re-run the seeder, assert an eligible worker reconstructs it and the seed succeeds. |
| 3 | **`ActorIdentityIsInternallyConsistent`'s "holds the role the action implies" is unsatisfiable for the two actions that matter most.** Workers deliberately carry **no** stored role row (`"Worker"` is a pseudo-role mapped to null), so a literal reading fails every correct `DailyEntry.Create`; and `DailyEntry.Submit`'s actor is either a role-less Worker or a Manager, which no single action→role table can express. | codex, test agent | The mapping is now explicit per action, including the absence case. |
| 4 | **`DemoSeed_WithNoOwner_FailsClosed` has no isolation strategy.** `DemoSeedTests` shares one container and `SeedDefaults.AccountId` across its facts, with no `ITestCaseOrderer` configured anywhere; its two existing facts already document that fragility. If the new test runs after the fixture-populating one, `FindOwnerAsync` finds the pre-existing Owner and the seeder reports `AlreadySeeded` rather than `PrerequisitesMissing` — a failure for ordering reasons unrelated to the guard under test. | test agent | The test gets its **own factory/container**, the way `SimulationSeedFactory` already does, rather than relying on undocumented xUnit sequencing. |
| 5 | **`FindOwnerAsync`'s account scoping is unstated, and `UserManager.GetUsersInRoleAsync` is not account-scoped** — it takes only a role name and returns Owners across every account. The existing seeder compensates in memory (`owners.Any(u => u.AccountId == accountId)`), and so does its manifest count. An implementer taking `.First()` attributes all ~256 demo rows to another tenant's Owner — and nothing looks wrong, because `ActorEmail` is read off the audit row rather than a join. | contrarian | The `AccountId` filter is stated, plus a **deterministic** choice when several Owners exist (otherwise demo attribution varies run to run, breaking the fixture's own determinism contract). |
| 6 | **Four call sites still ride ambient actor state** — `SeedFeedUsageAsync`, `SeedWaterUsageAsync`, `EnsureOpeningPurchaseAsync`, `EnsureAdjustmentAsync` — while every other helper takes an explicit `SimActor`. That contradicts the design's own rule that ambient actor state is this issue's bug class, and no mutation row pins who is resolved during those loops. | consumer walk | Explicit `SimActor` parameters, consistent with the order/expense helpers. |
| 7 | **The stated justification for the `GetRolesAsync` requirement is wrong**, though the requirement is fine. A Manager persona carries no `UserRoleAssignment` row, so even a wrongly-empty `Roles` list still passes the guard via its "zero assignment rows = unscoped" fallback — not via the role check the comment invokes. | consumer walk | Justification corrected; requirement kept. |
| 8 | **The invalidated "audit metadata only" model is still documented at the abstraction boundary**: `ICurrentUser.cs:3` ("handlers use it only through `IAuditWriter`"), `TenantResolutionMiddleware.cs:6` (actor resolution described as audit-trail work), `CurrentUserContext.cs:5` ("resolved by HTTP middleware", false once CLI verbs resolve system actors), and `02-architecture.md` labelling `AuditWriter`/`CurrentUserContext` "unchanged in behaviour". | codex | All four corrected in this PR. Leaving them is leaving the exact false model that produced three revisions of wrong reasoning. |
| 9 | The demo Owner's password is random and printed only to `bootstrap-admin`'s stdout, so "looking requires a login, and a login requires an Owner" is incomplete — it requires *that* password (or the must-change-password flow). | contrarian | Added to the docs note, since the replaced comment is meant to explain the new prerequisite to a developer hitting it. |

## Recorded, not actioned

- **`ActorEmail` is a truncated snapshot** (`MaxActorEmailLength = 256`, no FK).
  Real, but inert for this fixture: `sim-manager-1@sim.local` is far under the
  limit. Unguarded by choice.
- **The "human-authored" qualifier in the identity test currently filters
  nothing** — `SimulationSeedFactory`'s Owner is created via raw
  `UserManager.CreateAsync` (no audit row), and `ResolveSystemActor` is
  unreachable from that fixture. Correct, just unexercised.
- **`SeedPrimaryTimeZoneAsync`'s early return** is real, but that fixture starts
  from the migration-baked `"UTC"` default against `America/Chicago`, so its one
  run is never short-circuited.
- **Non-default cast configurations remain untested**, and under
  `Managers = 0` the Owner fallback would make the "never the Owner" clause fail.
  The suite asserts the fixture's counts, so this cannot fire unnoticed.
