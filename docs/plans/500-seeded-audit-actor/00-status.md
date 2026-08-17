# Status: seeded audit events carry a real actor (#500)

> **Planning record — seeded audit events carry a real actor ([#500](https://github.com/mforce/cluckwork/issues/500)), August 2026.** What was *intended* at the time, not what shipped. The issue is closed; where this disagrees with the code, the code is right. See [`docs/plans/README.md`](../README.md).

- Gate 1 — Product: APPROVED 2026-08-11; decision A re-opened by the round-1
  review and **re-confirmed 2026-08-11** with the pinned-contract cost on the
  table (see `03-program-design.md`)
- Gate 2 — Architecture: APPROVED 2026-08-11 (revision 2 after the round-1 review)
- Gate 3 — Program Design: APPROVED 2026-08-11 (revision 5).
  44 confirmed defects across four review passes: `03a` (15), `03b` (12),
  `03c` (8 + the one that changed the design), `03d` (9).
  Round 3 closed the `ICurrentUser`-consumer class via two independent
  exhaustive walks, and produced **no product defect** — its whole yield was
  tests, mutations and comments, which is why the review loop stopped there.
- Gate 4 — Slice plan: APPROVED 2026-08-11 (`04-slices.md` revision 2).
  Reviewed by the panel (`04a`), which also covered Gates 1 and 2 — those had
  been approved with no dedicated pass, and the review found 4 more defects in
  them, 2 HIGH and in user-facing text. Gate 1/2 amendments stand without
  re-approval (owner, 2026-08-11): no decision changed, only wrong descriptions.

**All six slices are DONE (2026-08-12).** Full suite green: **1653** — 145
application, 325 domain, 1183 integration.

Reviewed at the mid-point (after slice 3) by the four-reviewer panel; ledger
`05a-review-slices-1-4.md`. Every mutation in the design's table has been run and
recorded in `05b-mutation-run.md`, including one that **survived** and forced a
test to be rewritten, and one that survives deliberately with the gap stated at
the line.

**Every gate gets the panel** (owner directive, 2026-08-11) — not just the design
gate. Recorded in memory as `review-policy-codex-two-agents`.

Work happens in the `cluckwork-500` git worktree, branch `fix/500-seeded-audit-actor`.
Review ledgers: `03a`, `03b`, `03c`, `03d`.

## Slices

- [x] Slice 1 — tracer bullet: every non-HTTP caller declares an actor; zero `(unresolved)` rows
      **DONE 2026-08-11.** Full suite green (1643: 145 application, 325 domain,
      1173 integration). Proven against real throwaway databases via the real
      CLI: **0** `(unresolved)` rows on either profile, the single `Guid.Empty`
      actor id being the deliberate `(bootstrap-admin)` row, and demo against a
      never-bootstrapped database failing closed with the message naming
      `bootstrap-admin`.

      **Correction (2026-08-12).** This entry originally recorded "demo → 950
      audit rows". That figure was wrong: 950 is demo **plus** simulation in one
      database (508 + 442), mislabelled as demo alone. Re-running the same proof
      against the finished code gives demo → **509** rows (508
      `owner@proof.local` + 1 `(bootstrap-admin)`). The zero-placeholder result
      it was cited for was never in doubt; the row count was. Caught only by
      re-running the proof before quoting it in the PR — which is the reason to
      re-run a figure rather than carry it forward.

      **The casualty walk (slice 1 task 1), recorded so it is not re-derived:**
      17 tenant-resolving scopes exist in `tests/`; **8** reach an auditing
      handler and needed an actor — `ChangeUserRoleRaceTests:81`,
      `CurrencyLockRaceTests:102`,`:167`,`:177`,`:246`,
      `DisableUserRaceTests:82`,`:92`,`:577`. The other 9 are safe and *why*
      matters: reads (`ListUsersAsync`, `IAuditEventRepository`), step-up
      issue/validate (writes no audit row), and `RetryBoundaryTests:174` whose
      injected fault throws inside `userManager.CreateAsync` before the audit
      call. In `src/`, `DailyEntryLockSweep` resolves a tenant but mutates the
      aggregate directly and never calls `IAuditWriter` — verified a third time
      here because it is production background code.
- [x] Slice 2 — the simulation cast becomes real people (per-phase personas)
      **DONE 2026-08-12.** Full suite green (1646: 145 application, 325 domain,
      1176 integration — three new tests). `SimActor`/`SimCast`/`Pick`/`ActAs`
      landed; every audited phase except the daily entries now declares its own
      persona, and `SeedPrimaryTimeZoneAsync` moved ahead of
      `RestrictOneWorkerAsync` so its `ActAs(Owner)` stopped being a no-op.

      **A slice-1 test was wrong, and slice 2's walk-everything guard found it.**
      `SimulationSeed_LeavesNoUnattributedAuditEvent` asserted over *every* audit
      row in the account, on the stated ground that only the seeder writes there.
      That is false: sibling facts in the class drive real endpoints, and the
      account export writes an `Account.Export` row. Nothing pins xUnit's fact
      order, so the population was order-dependent. `SimulationSeedFactory` now
      snapshots the seeded row ids in `InitializeAsync`, before any fact runs,
      and both tests filter to them. The old test still passed — HTTP rows carry
      a real actor — so this was a wrong test, not a live bug.

      Deliberately deferred to slice 3, and *named in the test* rather than
      omitted: `DailyEntry.Create` and `DailyEntry.Submit` are still
      Owner-authored. The persona test fails on any audited action that is
      neither expected nor in that deferred set, so a new one cannot slip
      through uncovered.

      #394 check (non-CI callers): no k6 script or Playwright spec reads
      provenance or created-by text, and `tools/simulation/ui/src/cast.ts:106`'s
      claim that the restricted worker is `sim-worker-1` still holds. No boot
      guard or config key changed, so #370 does not apply.
- [x] Slice 3 — the restricted worker, now an authorization matter
      **DONE 2026-08-12.** Full suite green (1648: 145 application, 325 domain,
      1178 integration). `WorkerFor` + per-flock eligibility wired into
      `SeedFlockHistoryAsync`; `RestrictOneWorkerAsync` returns the pair from
      **both** branches; `SimCast` carries the restricted pair. Entries are
      self-submitted for now — slice 4 adds the manager-approval shape.

      **The design's own partial-rerun test was insufficient, and the mutation
      is what showed it.** `03-program-design.md` specified it as "(a) the seed
      succeeds and (b) the replacement entry's actor is an eligible worker".
      Both clauses hold under the `(Guid.Empty, Guid.Empty)` mutation: at the
      draft window's day offsets the unfiltered pool (3) and the filtered pool
      (2) both yield a NON-restricted worker, just a different one. Mutant
      applied → **survived**. The test now also asserts WHICH eligible worker
      the rotation selects, re-deriving the rule from the durable anchor; mutant
      re-applied → **red on that Assert.Equal**; restored, rebuilt, green.
      This is the fourth time on this plan that "write the test, then claim it
      is the one that dies" produced a wrong claim — the method in the design
      doc (run the mutation first) is what caught it.

      One incidental discovery, recorded because it cost a cycle: a seeded
      daily entry cannot simply be deleted. Feed and water usage carry a
      nullable `DailyEntryId` under a **RESTRICT** FK and the usage window
      (4 days) covers the whole draft window (2 days), so every draft entry is
      referenced; and deleting the feed-usage row alone leaves its
      `InventoryMovement` behind, so the re-run writes a second one and the
      seed fails its own exact-count check.
- [x] Slice 4 — both provenance shapes (mixed submitter)
      **DONE 2026-08-12.** `SubmittedByManagerEvery = 3`; every third day a
      manager signs off the worker's entry, so the fixture carries both #494
      shapes. Two new tests, and all three of the design's mutations for this
      slice were run before the claims were written: manager-branch → `ActAs(Owner)`
      kills `…SubmitIsEitherTheRecordingWorkerOrAManager`; never-manager and
      always-manager each kill `…CarriesBothProvenanceShapes`.

      One methodology note worth keeping: the first attempt at the
      never-manager mutation used `if (false)`, which is an unreachable-code
      **warning** — and warnings are errors here, so the build failed and the
      test run silently used the previous mutation's binary. The `2` printed by
      a `grep -c "error"` was the only sign. Mutations must be written so the
      code still compiles; `if (… == -1)` was the replacement.

- [x] Slice 5 — remaining guards + the recorded mutation run
      Guards: `…ActorIdentityIsInternallyConsistent` (id and email must resolve
      to the same user), `…EachActionsActorHoldsTheExpectedRoles` (the stored
      roles the email prefix cannot see), `…WorkerRotationIsExactAndDeterministic`
      (per-flock eligible counts, full history, restricted flock identified by
      joining the assignment row). The recorded run is in `05b-mutation-run.md`.

      The identity/roles guards were independently asked for by the review panel
      (codex P2: "persona assertions validate ActorEmail but never prove
      ActorUserId, email and roles identify the same user; making `storeKeeper`
      the Owner with a manager email survives"). They were already planned for
      this slice, and now close exactly that.

- [x] Slice 6 — docs, and the comments carrying the false model
      `specs/product/GLOSSARY.md` (the audit-log entry gains the "every event
      names an actor" invariant + the two system actors), `AGENTS.md` (a new
      bullet beside the seed one), `deploy/README.md`, `docs/decisions/280`,
      `docs/decisions/283`, `docs/runbooks/break-glass-account-recovery.md`,
      `tools/simulation/README.md`.

      **The SPA Help page is deliberately NOT changed, and this is the reason:**
      a real farm never had `(unresolved)` rows and never could — every HTTP
      write resolves an actor, so the placeholder was only ever reachable from
      the two seeders and the two CLI verbs. The Help page's audience is farm
      users, for whom nothing changes. The SPA itself needs no change either: it
      renders whatever email the API sends (`recordHistory.createdBy`), with no
      placeholder literal anywhere in `web/`.

      **#370 (sim-harness boot guards) does not apply** — no boot guard and no
      config key was added, renamed or retired. `tools/simulation/reset.sh`
      already runs `bootstrap-admin` before seeding, which is exactly the
      prerequisite the seeders now enforce, so the harness needed no change to
      keep working. Checked rather than assumed.

## Notes for a fresh session

- Source issue: https://github.com/mforce/cluckwork/issues/500
- Facts established by reading the code (2026-08-11):
  - `AuditWriter.WriteAsync` (`src/Cluckwork.Infrastructure/Repositories/AuditWriter.cs:33-34`)
    falls back to `Guid.Empty` / `"(unresolved)"` when `ICurrentUser.IsResolved`
    is false. It already **throws** when the *tenant* is unresolved.
  - `CurrentUserContext` is registered scoped in
    `CluckworkIdentityServiceCollectionExtensions.cs:25-27`; the concrete type is
    resolvable from DI, so a seeder can take it and call `Resolve(...)` exactly
    the way it already takes `TenantContext` and calls `tenant.Resolve(...)`.
  - `TenantResolutionMiddleware` is the only caller of `CurrentUserContext.Resolve`
    today, so the CLI seed path always takes the fallback.
  - **`DemoDataSeeder` does not require an Owner user to exist.** Its
    `MissingBaseDataAsync` checks the account, the Owner *role row*, and the egg
    grades — not a user. It also creates no users. So `seed --profile demo`
    against a freshly migrated database can legitimately run with **zero users**.
  - `SimulationDataSeeder` **does** require an existing Owner user and fails
    closed with a message telling you to run `bootstrap-admin` first; it then
    creates the `sim-manager-N@` / `sim-sales-N@` / `sim-readonly-N@` /
    `sim-worker-N@` cast via the real `CreateUserHandler`.
  - No background job writes audit events today (`src/Cluckwork.Infrastructure/Jobs/`
    has no `IAuditWriter` reference), so the seeders are the only non-HTTP callers.

## Post-PR review rounds (codex on #517)

- **Round 1** — disabled Owners were not excluded from `FindOwnerAsync`, so a
  fixture could be signed by an account that cannot log in. Real; fixed.
- **Round 2**, against round 1's own fix — the message told the operator to run
  `bootstrap-admin`, which no-ops on a retained Owner role row. An instruction
  loop. Real; fixed.
- **Round 3**, three findings, all real:
  1. the message then named "re-enable from the Users screen", which is
     `OwnerOnly` and therefore unreachable from the state that prints it;
  2. the simulation branch keyed on `disabledOwners > 0` **without** `owner is
     null`, so a renamed grade (a supported user action, #283) plus one disabled
     co-Owner produced advice to go editing `DisabledAt` while the real fault was
     the grade;
  3. the repair named only `DisabledAt`, but `EnableUserAsync` clears
     `DisabledBy` too — *"both columns describe ONE live fact"* — so following it
     literally leaves stale disable metadata on an active user.

**The Help-page decision was reversed, and the reasoning is worth keeping.**
Slice 6 recorded a deliberate choice NOT to touch the SPA Help page, on the
ground that a real farm never had `(unresolved)` rows. That reasoning considered
only the five #494 History screens and missed the **audit log viewer**:
`(bootstrap-admin)` and `(break-glass)` are written on a real production farm —
the first Owner's `User.Create` and any break-glass reset — and an Owner
browsing `/audit` sees them. So it *was* a user-visible change, and Help now
explains both labels in en/es/tl.

- **Round 4**, three findings, all real — but one was overstated, and the
  mutation run is what showed it:
  1. the *round-3 fix's own* remaining defect. Round 3 stopped the message being
     wrong about the cause; it still named `bootstrap-admin`, which returns
     `AlreadyProvisioned` whenever any Owner exists — and one does, by
     construction, in exactly the state round 3 introduced. Fixed
     **structurally**, not with a third prose patch: the combined preflight is
     now two blocks (base data, then Owner), matching what `DemoDataSeeder` has
     always had. One `if` serving two unrelated causes had to guess which had
     failed, and that guess is what kept being wrong.
  2. a **DISABLED cast member** was accepted on a partial re-run. Confirmed
     silently green — deleting the guard flips the test's status assertion to
     `Seeded`, because disabling moves nobody between role buckets and so
     `ValidateCounts` cannot see it.
  3. a **promoted worker** was accepted into the worker pool, because the role
     check *exempted* workers rather than asserting they hold no assignable
     role. Real, but **not** the claimed silent success: `ValidateCounts` does
     catch it (`users.managers: expected 1, got 2; users.workers: expected 3,
     got 2`) — last, in `EmitManifestAsync`, after every durable write. So the
     defect is a misleading failure late, not a green run.

**A local reviewer then caught a defect the round-4 fix INTRODUCED.** Two
separate `if` blocks short-circuit, so a database missing both the base data and
the Owner reported only the first — the operator repaired the grades, re-ran,
and only then met the second problem. Two trips for one broken database, and a
direct regression against the goal that motivated the split. The base-data
message now carries an appendix naming the Owner remedy too, mutation-verified,
and its test doubles as proof that the neighbouring
`DoesNotContain("dotnet Cluckwork.Api.dll bootstrap-admin")` is not vacuous:
same message, opposite condition, string required to appear.

**Finding 3 changed one of its own tests.** The first version asserted
`Status == Failed`, which the late count check satisfies on its own — the
"mutant died, but not on its named assertion" trap. The test now pins
`DoesNotContain("expected")`, the only thing separating *refused at the cast*
from *refused at the manifest* once both produce `Failed`.

- **Round 5**, one finding, **refuted as stated** — and it still found something.
  The claim was that `GetUsersInRoleAsync` throws for an absent role, so the
  Owner lookup running before the base-data check would surface a generic
  `Failed` instead of the prerequisite advice. It does not throw:
  `UserStore.GetUsersInRoleAsync` returns an empty list when `FindRoleAsync`
  misses (throwing is `AddToRoleAsync`/`IsInRoleAsync`), and the test written to
  check this passed on the status and message assertions against unfixed code.
  What the empty list *does* cause is a defect one level down, introduced by
  round 4: `owner is null` cannot tell "roles were dropped" from "nobody is an
  Owner yet", so the dual-failure appendix advised `bootstrap-admin`, which
  cannot create a role. `FindOwnerAsync` now returns whether the role exists and
  the appendix is gated on it.

**Five rounds, and the yield is now visibly thinning.** Rounds 1–4 found live
defects in shipped behaviour; round 5's stated finding was wrong and what it
actually turned up was operator *wording* in a hand-corrupted-schema state —
a corner of the corner round 4 opened. Recorded here because the count that
governs when to stop the review loop measures confirmed product defects, and
this one is real but small.

**Four rounds, four-for-four on real defects, and every one against the
previous round's fix.** Each level was less obvious than the last: wrong actor →
remedy that loops → remedy that is unreachable → advice printed for the wrong
cause → the cast held to a weaker standard than the Owner. The lesson recorded
for next time: when a message is corrected twice, stop pinning its *wording* and
pin the *property* — here, that the remedy named must be performable from the
state that printed it. And when the same defect returns a third time, stop
patching the message and change the structure that forces the guess.
