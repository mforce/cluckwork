# Architecture: seeded records name a real person (#500)

## Fit

Four **non-HTTP** callers write audit rows today. All four resolve the tenant;
none resolves the actor, so all four land on `AuditWriter`'s
`"(unresolved)"` fallback. Only the first two are in the issue's scope, but the
third scope bullet ("should `(unresolved)` become a loud failure?") cannot be
answered without the other two, because **making it a loud failure breaks them**.

| Caller | File | Writes | Actor today | Actor after |
|---|---|---|---|---|
| `seed --profile demo` | `Infrastructure/Persistence/DemoDataSeeder.cs` | flocks, entries, customers, products, orders | `(unresolved)` | the account's Owner |
| `seed --profile simulation` | `Infrastructure/Persistence/SimulationDataSeeder.cs` | the whole fixture | `(unresolved)` | the persona whose job it is |
| `bootstrap-admin` | `Infrastructure/Identity/FirstRunAdminService.cs:265` | one `User.Create` for the first Owner | `(unresolved)` | explicit system actor |
| `recover-admin` | `Infrastructure/Identity/IdentityProvider.cs:907` (`BreakGlassResetAsync`) | one `User.BreakGlassReset` | `(unresolved)` **on purpose** — the code says so at line 918-921 | explicit system actor |

Also touched — and the earlier label "unchanged in behaviour" was wrong on two
of the three (round 3, #8):

- `Infrastructure/Repositories/AuditWriter.cs` — **behaviour changes**: it now
  fails closed on an unresolved actor.
- `Infrastructure/Identity/CurrentUserContext.cs` — **gains a capability**
  (`ResolveSystemActor`), and its header comment saying it is resolved by HTTP
  middleware becomes false once the CLI verbs and both seeders resolve their own.
- `Application/Common/ICurrentUser.cs` — no code change, but its comment
  ("handlers use it only through `IAuditWriter`") is false and is corrected:
  `FlockScopeGuard` reads it as an authorization input.

The SPA is not touched. `web/` has no `(unresolved)` literal — it renders
whatever email the API returns.

## The central decision: silent fallback → declared actor

`AuditWriter` already **fails closed on an unresolved tenant** and throws. The
symmetric treatment of the actor is the fix that stops this bug returning:

```
if (!user.IsResolved) throw;      // no caller may be anonymous by accident
```

But `recover-admin` genuinely has no human actor — the operator is at a shell,
not signed in — and its comment records that as a deliberate choice. So a bare
throw is wrong. The design is **fail closed on silence, allow an explicit
declaration**:

- a **real user** — `CurrentUserContext.Resolve(userId, email, roles)`, exactly
  what `TenantResolutionMiddleware` already does. Used by both seeders.
- an **explicit system actor** — a new `ResolveSystemActor(label)` that sets
  `ActorId = Guid.Empty` and a fixed label (`"(bootstrap-admin)"`,
  `"(break-glass)"`). Used by the two CLI provisioning/recovery paths.

`"(unresolved)"` then has no writer at all and the literal leaves the codebase.
Anything that forgets to declare gets a loud `InvalidOperationException` naming
the problem — the same shape, and the same message style, as the tenant guard
directly above it.

**The alternative (do less)** is to fix only the two seeders and leave the
fallback in place. It satisfies the issue's first two bullets and nothing else:
the fallback stays reachable, so the next non-HTTP caller reintroduces the bug
silently, which is precisely what the issue's third bullet asks about.
Recommendation: **fail closed**. Blast radius sized below and it is small.

## Endpoints

None. No route added, changed, or removed.

## Data

No schema change, no migration. `AuditEvent.ActorId` / `ActorEmail` already
exist and already accept `Guid.Empty` (today's fallback writes it).

Row *content* changes for **all four** callers. The two seeders are the bulk of
it and the reason this issue exists — demo's ~256 rows and the whole simulation
fixture stop reading `(unresolved)` and start naming a person. (An earlier
revision of this section named only the two CLI paths below, which read as if
the seeders wrote unchanged rows — the opposite of the issue's subject.)

The two CLI paths are worth naming separately because their new content is not a
person at all:
`bootstrap-admin`'s `User.Create` row and `recover-admin`'s
`User.BreakGlassReset` row stop saying `(unresolved)` and start saying
`(bootstrap-admin)` / `(break-glass)`. `recover-admin`'s existing
`host` + `osUser` details are unchanged and stay the real accountability.

Queries: unchanged. The guard test's query is new and cheap —
`SELECT count(*) FROM "AuditEvents" WHERE "AccountId" = @a AND "ActorEmail" = '(unresolved)'`,
expected `0`, run after each profile seeds.

## Flow

**Demo** (`DemoDataSeeder.SeedAsync`), after the existing `tenant.Resolve(accountId)`:

1. New preflight (Gate 1 decision A): the default account must have a user in
   the Owner role — the same check `SimulationDataSeeder.MissingBaseDataAsync`
   already runs, with the same "run `bootstrap-admin` first" message. Runs
   **before** `tenant.Resolve`, alongside the existing prerequisite checks, so
   it uses `IgnoreQueryFilters` like its neighbours.
2. `currentUser.Resolve(owner.Id, owner.Email, [Roles.Owner])`.
3. Everything downstream is unchanged — one actor for the whole demo seed.

**Simulation** (`SimulationDataSeeder.SeedAsync`), after `tenant.Resolve`:
the actor changes per phase, so each phase declares who is acting before it
calls handlers. `SeedCastAsync` must run first (it mints the personas) and is
itself authored by the Owner, which the existing preflight already guarantees
exists.

| Phase (existing method) | Authored by |
|---|---|
| `SeedCastAsync` — `User.Create` × cast | Owner |
| `SeedFlockTopologyAsync` — flocks (no bird movements: `SimulationDataSeeder` never injects `RecordBirdMovementHandler`; only the demo seeder produces `Flock.BirdMovement`) | Manager (rotating) |
| `RestrictOneWorkerAsync` — flock assignment | Owner |
| `SeedPrimaryTimeZoneAsync` — account settings | Owner |
| `SeedProductionHistoryAsync` — record + submit daily entries | Worker (rotating, see constraint below) |
| `SeedInventoryOperationsAsync` — items, purchases, adjustments, feed, water | Manager (rotating) |
| `SeedExpensesAsync` — categories + expenses | Manager (rotating) |
| `SeedProductCatalogAsync` — products | Manager (rotating) |
| `SeedCustomersAsync`, `SeedSalesAsync`, orders, confirm, payments | Sales (rotating) |
| `SeedSecondAccountAsync` | not applicable — writes `db.Accounts` directly, no audit row |

Rotation is deterministic (index modulo persona count), never random — the
fixture's whole contract is that a clean re-run converges (#279).

**Constraint the rotation must respect:** `RestrictOneWorkerAsync` narrows
`workerIds[0]` to `flockIds[0]`, and that worker must never author on another
flock.

**Corrected in revision 2 — this paragraph previously said the opposite.** It
claimed handlers "never consult flock assignments (authorization is an endpoint
concern), so nothing would *fail* — it would just be wrong". False.
`FlockScopeGuard.CheckAsync` is called by `RecordDailyEntryHandler`,
`SubmitDailyEntryHandler`, `RecordFeedUsageHandler` and
`RecordWaterUsageHandler`, and it opens with
`if (!user.IsResolved) return Result.Success();` — a bypass whose comment names
"startup/demo seeders" as its reason. **The seeders' unresolved actor is a
live authorization bypass today, and this change removes it.** Attributing the
restricted worker to a foreign flock therefore fails the seed outright, and
`SimActor.Roles` becomes a functional authorization input (Owner and Manager
bypass the guard by role), not an audit label. Full consequences in
`03-program-design.md`.

**bootstrap-admin** (`FirstRunAdminService`, after its existing
`tenant.Resolve(accountId)` at line 265): `currentUser.ResolveSystemActor(SystemActors.BootstrapAdmin)`.

**recover-admin** (`AdminRecoveryService`, after its existing
`tenant.Resolve(target.AccountId)` at line 102):
`currentUser.ResolveSystemActor(SystemActors.BreakGlass)`.

## Two independent blast radii — measure both

Revision 2 (round-1 review): the section below measures the **actor throw**, and
originally treated that as the blast radius of the whole change. It is not. The
**demo Owner prerequisite** is a second breaking change with its own callers,
and it was never measured. AGENTS.md #394 covers request/status contract
changes; a CLI *prerequisite* change is the same class of miss with a different
trigger.

**Blast radius of the demo prerequisite** (measured): no CI workflow and no
`tools/simulation/` script runs `seed --profile demo` — only docs mention it. In
the test suite it breaks three tests, two of which spawn the real CLI against a
database where `bootstrap-admin` never ran:
`SeedCommandTests.SeedCommand_Demo_SeedsDataAndExitsWithoutStartingKestrel`
(71-90); `SeedCommandTests.SeedCommand_Demo_AgainstAnUntouchedDatabase_MigratesAndSeedsInOneStep`
(135-145), whose comment states a green result *"proves `seed --profile demo`
needs nothing but a connection string"*; and
`DemoSeedTests.Boot_NeverAutoSeedsDemo_OnlyExplicitSeedAsyncDoes` (99). See the
BACKTRACK section of `03-program-design.md`.

## Blast radius of the fail-closed guard (measured, not estimated)

Anything that invokes an auditing handler with the tenant resolved but no actor
now throws. Counted in `tests/`:

- **250** uses of `TestHarness.WithTenantScopeAsync` — **not affected**. That
  helper hands back a raw `AppDbContext` for direct EF writes; it never goes
  through `IAuditWriter`.
- **17** raw `GetRequiredService<TenantContext>().Resolve(...)` sites across 7
  files (`CredentialEpochRaceTests`, `AdminRecoveryServiceTests`,
  `AuditProvenanceTests`, `CurrencyLockRaceTests`, `ChangeUserRoleRaceTests`,
  `DisableUserRaceTests`, `RetryBoundaryTests`). Only those that then call an
  auditing handler need an actor; the fix is one new helper beside
  `WithTenantScopeAsync` that resolves both.
- HTTP-driven tests are unaffected — `TenantResolutionMiddleware` resolves both.

Exact per-site list, established in round 1 — four sites reach an auditing
handler with no actor and would throw: `CurrencyLockRaceTests.cs:167`,
`ChangeUserRoleRaceTests.cs:81`, `DisableUserRaceTests.cs:82` and `:92`.

## External

None. No third-party API, no new env var, no config key. Nothing here reaches
the network.

## Non-CI callers to re-check (AGENTS.md #394)

No write **contract** changes (no new required field, no tightened validator,
no state-machine change), so `tools/simulation/k6/` and the Playwright specs are
unaffected by construction. Two things still need reading, not running:

- `tools/simulation/bootstrap.sh` already runs `bootstrap-admin` before
  `seed --profile simulation`, so demo's new prerequisite matches what the
  harness does. The harness's **demo** path — if it has one — must be checked.
- The Playwright specs assert on English through the SPA catalogs and one
  index-based selector on the Audit table. A row whose actor column changes from
  `(unresolved)` to an email could move that table's ordering only if it sorts
  by actor — to be verified at Gate 3, not assumed.

## Docs to update in the same PR (AGENTS.md standing rule)

The authoritative list is in `03-program-design.md`; this section previously
carried a shorter, divergent one that also named the wrong file — root
`README.md` contains **no** mention of `seed --profile demo`. The prose lives in
`deploy/README.md:30-32` and the AGENTS.md seed bullet.

- `deploy/README.md` + the AGENTS.md seed bullet +
  `docs/decisions/280-seed-and-simulation.md` — demo's new `bootstrap-admin`
  prerequisite.
- `docs/runbooks/break-glass-account-recovery.md` — the audit row's actor column
  now reads `(break-glass)`; the runbook's verification drill quotes it.
- `specs/product/GLOSSARY.md` + the SPA Help page only if either names the
  placeholder. To be checked, not assumed.
