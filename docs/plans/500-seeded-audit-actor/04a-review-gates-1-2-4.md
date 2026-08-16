# Review of Gates 1, 2 and 4 — findings and dispositions

> **Planning record — seeded audit events carry a real actor ([#500](https://github.com/mforce/cluckwork/issues/500)), August 2026.** What was *intended* at the time, not what shipped. The issue is closed; where this disagrees with the code, the code is right. See [`docs/plans/README.md`](../README.md).

Gates 1 and 2 were approved **without a dedicated review pass**, and Gate 4 was
taken to approval the same way. The owner caught it: *"all gates should be
verified by local agents."* This is that review — a `pi` contrarian and a codex
pass plus a Claude agent on the slice plan, and a Claude agent on the two
approved documents. It found **9 defects**, two of them HIGH in user-facing text.

The rule is now recorded in memory as *every gate gets the panel*, with this as
its evidence.

## Gate 4 — the slice plan

| # | Finding | Found by | Disposition |
|---|---|---|---|
| 1 | **Slice 1's casualty list was hand-assembled and is wrong.** I named four direct-scope sites; a walk finds roughly **eight**: `CurrencyLockRaceTests` at `:99-105`, `:174-180` (a *sibling scope inside the very test I cited at :167*), and `:243-248` (a `[Theory]` whose 4 of 7 rows route to audited expense / sales-order / product-create / product-update handlers), plus `DisableUserRaceTests:577-579` (`SetUserPasswordAsync` → `IdentityProvider.cs:993`). Every one throws at slice 1's commit. | codex, slice agent | **The list is deleted and replaced by a method.** Slice 1's first task is now the walk itself; the enumeration is an output of slice 1, not an input. Third instance of this failure shape in one session — AGENTS.md's rule applies. |
| 2 | **Slice 2 would leave the suite RED, not merely churn.** Slice 2 attributed daily entries to workers while `WorkerFor` and the restricted pair stayed in slice 3. The assignment row is written before production history, so a plain rotation over 3 workers × 12 days necessarily selects worker 0 on the foreign flock → `FlockScope.NotAssigned` → `SeedAsync` returns `Failed` → the fixture's `InitializeAsync` throws → the whole simulation suite is red. There is no pre-existing "naive" picker to lean on: `SeedFlockHistoryAsync` has no actor selection at all today. | codex, slice agent, contrarian | **Worker attribution moves wholly into slice 3.** Slice 2 covers the Owner/Manager/Sales phases only; daily entries stay Owner-attributed one slice longer. |
| 3 | **The system-actor path ships untested in every slice.** Slice 1 introduces `SystemActors`, `ResolveSystemActor`, and makes both CLI verbs depend on it — yet `AdminRecoveryServiceTests` and `BootstrapAdminCommandTests` appear in the design's test plan and in **no slice**, leaving two mutation rows with no home. | contrarian | Both assigned to slice 1, beside the feature they cover. |
| 4 | **Slice 1's proof cannot be run as written.** Both profiles now require an Owner, so `seed` exits `PrerequisitesMissing` before writing any audit row. The proof omits the `bootstrap-admin` step that slice 1 itself makes mandatory. | contrarian, codex | Proof restated as migrate → `bootstrap-admin` → seed. |
| 5 | **The "~256 rows that exist today" baseline is unreproducible.** Those rows live in a database seeded *before* the change, which the design explicitly never repairs. Against the fresh database the proof specifies, the before-count is zero. As written it implies a before/after comparison the design forbids. | contrarian | Baseline re-attributed to the issue's own probe run; the slice-1 proof compares against zero on a fresh database. |
| 6 | **`AuditActorTests ×3` is wrong in the design's mutation table.** Reverting `AuditWriter` to the fallback leaves `WriteAsync_WithSystemActor_StampsTheLabelAndEmptyActorUserId` green — it uses a *resolved* actor, and the old ternaries preserve both its label and `Guid.Empty`. | codex | Corrected to ×2 in `03-program-design.md`. |
| 7 | Slice 3's "fails the whole seed with `FlockScope.NotAssigned`" is a **mutation observation**, not something any named slice-3 test asserts. | contrarian | Said plainly, so it does not read as test coverage. |
| 8 | Slice 2 should name the `Account.UpdateSettings → Owner` clause explicitly, since its validity depends on the phase reorder that same slice delivers. | contrarian | Stated. |

**Cleared with evidence** (recorded so the next reviewer need not redo it): no
attribution-test churn — slice 1's tests survive every later slice unmodified,
because the demo path stays Owner-only forever and
`…LeavesNoUnattributedAuditEvent` asserts only absence. The demo-prerequisite
casualty list (`SeedCommandTests:71-90`, `:135-145`, `DemoSeedTests:99`) **is**
complete; `OneShotVerbMinimalConfigTests:99` already tolerates exit 0 *or* 1, so
a new `PrerequisitesMissing` does not break it. No forward dependencies beyond
finding 2. `AuditTests`, `AuditProvenanceTests`, `BaselineSeedCurrencyTests`,
`SeedAndFlockTests`, `SimulationSeedCommandTests`, `SimulationCrossDayRerunTests`,
`CredentialEpochRaceTests`, `AdminRecoveryServiceTests` and
`ChangeUserRoleRaceTests` were each checked and are not casualties. One near-miss
was ruled out by inspection: `RetryBoundaryTests:174` calls `CreateUserAsync`
with an unresolved actor, but its injected fault fires inside
`userManager.CreateAsync` and throws *before* the `audit.WriteAsync` at
`IdentityProvider.cs:441`.

## Gates 1 and 2 — the documents approved unreviewed

| # | Finding | Disposition |
|---|---|---|
| 9 | **Decision A's cost is still incomplete, and this is the sharpest version of it.** `bootstrap-admin` prints the temporary password **only on first provisioning**; a re-run against an already-provisioned account prints "already provisioned … nothing to do" and names nobody. So on any persistent dev database, reused CI container or shared demo host, demo data is attributed to an Owner **whose credentials nobody has** — and viewing the demo requires logging in as somebody. The escape is the `recover-admin` break-glass procedure. Round 3's finding #9 covered only the first-run case. | Recorded in `01-product.md` as the third consequence, with the `recover-admin` escape named. |
| 10 | **The Announcement's "the placeholder can no longer appear by accident" is false** for every database seeded before this ships — and it contradicts the doc's **own** success metric two paragraphs above, which correctly scopes to a "freshly seeded farm". | Sentence corrected. |
| 11 | **The docs-to-update list names a file that does not contain the prose.** Root `README.md` has **zero** mentions of `seed --profile demo`; it is `deploy/README.md:30-32` and the AGENTS.md seed bullet. Gate 2's two-item list also contradicts Gate 3's five-item list. | Both lists corrected and reconciled. |
| 12 | **Gate 2's Data section undercounts its own subject** — it names row-content changes for `bootstrap-admin` and `recover-admin` only, omitting demo (~256 rows) and simulation, which are the entire point of #500. A reader of that section alone would conclude the seeders write unchanged rows. | Corrected. |

**Cleared:** Gate 1's problem statement faithfully matches the issue and its
success metric is measurable by tests the design actually specifies. Gate 2's
four-caller table verifies line by line against source; the authorization claim
that survived three revisions is genuinely gone from the current text; the #394
and #370 assessments hold; and the one index-based Playwright selector
(`owner.spec.ts:131`) targets the **action** column, not the actor, so it is
unaffected either way.
