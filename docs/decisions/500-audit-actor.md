# Nothing writes an audit event without an actor (#500)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale. The design record that produced it is
> [`docs/plans/500-seeded-audit-actor/`](../plans/500-seeded-audit-actor/) —
> a planning record, not current documentation.

**Status:** accepted · **Date:** 2026-08

## What happened

`AuditWriter` used to fall back to `"(unresolved)"` when `ICurrentUser` resolved
to nothing. Silent, reachable only from non-HTTP callers, and it shipped **~256
such rows into every demo farm** — invisible until #494 rendered provenance on
five screens, at which point the farm's own history read "(unresolved)".

## The rule

`AuditWriter` **throws** on an unresolved `ICurrentUser`, symmetric with the
tenant guard beside it. Every non-HTTP caller therefore declares who it is:

- `bootstrap-admin` and `recover-admin` declare **system actors**
  (`SystemActors.BootstrapAdmin` / `.BreakGlass`, via
  `CurrentUserContext.ResolveSystemActor`);
- **both seeders require an Owner** — demo exits `PrerequisitesMissing` naming
  `bootstrap-admin`, a deliberate break with the old "demo needs nothing but a
  connection string" contract;
- simulation attributes **per persona**: managers create flocks, products and
  expenses, sales staff book orders, a rotating worker pool records the daily
  entries, and roughly one submission in three is a manager signing off somebody
  else's draft (both #494 provenance shapes).

## The trap: an actor is an authorization input, not a label

`ICurrentUser` is an **AUTHORIZATION input**, not an audit label.
`FlockScopeGuard` reads `Roles`/`UserId`, so which persona the seeder acts as
decides what it is *allowed* to write: picking the flock-restricted worker for a
foreign flock returns `FlockScope.NotAssigned` and fails the entire seed.

**Never resolve an actor carrying roles it does not actually hold.** Build one
from `UserManager.GetRolesAsync`, never from a literal.
