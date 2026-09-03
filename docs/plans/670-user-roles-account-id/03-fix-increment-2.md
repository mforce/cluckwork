# Runbook — #670 fix increment 2: the decision record names the two residuals it was silent on, and one tsv rationale describes a read

You are the same implementer, in the same worktree (`/home/mforce/.herdr/worktrees/cluckwork/fix-670-user-roles-account-id`, branch `fix/670-user-roles-account-id`, PR #675 open at `6586d19b681aa35bd385363b44c0cb63fda84988`). Same rules as `01-implementer-runbook.md`; this file is committed beside it as `03-fix-increment-2.md`. Docs and one data-file rationale only — no code, no tests. Execute top to bottom: edit, build, the one narrowed test run, commit, push.

## What round 2 found (adversary seat, all `defer`, all against the RECORD, none against the product)

- **F-3-r2:** under an UNRESOLVED tenant neither write layer inspects a DELETE, and on `AspNetUserRoles` — unlike every filtered entity — nothing sits between such a scope and another farm's rows: there is no query filter, so no `IgnoreQueryFilters()` marker a reviewer would see. `db.UserRoles.Where(…).ToListAsync()` + `RemoveRange` in an unresolved scope deletes every farm's matching grants with no refusal. Nothing in `src/` does this today; what holds the arm shut is #536's scanner (every `db.UserRoles` site is a classified candidate in `filter-free-set-sites.tsv`). The §5 paragraph does not say so. **Fix: one sentence naming the arm and its actual control.**
- **F-4-r2:** `RoleManager.DeleteAsync(role)` cascades every farm's grants of that role (`FK_AspNetUserRoles_AspNetRoles_RoleId … ON DELETE CASCADE`, `AspNetRoles` is global reference data) with the change tracker never holding an `IdentityUserRole` — invisible to both layers. No caller in `src/`. The residual list names only a future `UserManager` claim/login/token call. **Fix: extend the residual sentence and the enforcement bullet.**
- **F-5-r2 (pre-existing rationale, in a file this PR edited):** `filter-free-set-sites.tsv:77` says `DisableUser revokes the user's roles by userRole.UserId == userId`; `IdentityProvider.cs:1114-1118` is a READ (`select r.Name).AnyAsync`) and `DisableUserAsync` deletes no role row. **Fix: the rationale text; the key (`path:line<TAB>db.UserRoles`) stays byte-identical** — `TenantBypassRealTreeTests` keys rows on path and set and reads the reason separately.

The other three seats (`repo-rules`, `false-green`, `caller-breakage`) were clean on this head.

## Rules (unchanged)

- Transcribe VERBATIM. Files you may edit: `docs/decisions/530-multi-farm-tenancy.md`, `tests/Cluckwork.Application.Tests/TenantBypass/Data/filter-free-set-sites.tsv` (line 77 only, and ONLY the text after the second TAB), `docs/plans/670-user-roles-account-id/03-fix-increment-2.md` (this file, committed). Anything else, STOP and report.
- `TenancyDocsFreshnessTests` greps every tracked file for "dormant tenancy" phrasings; the blocks below carry none — do not paraphrase them.

## Verify prerequisites

```bash
git branch --show-current   # expect: fix/670-user-roles-account-id
git status --porcelain      # expect: only "?? docs/plans/670-user-roles-account-id/03-fix-increment-2.md"
git rev-parse HEAD          # expect: 6586d19b681aa35bd385363b44c0cb63fda84988
```

===================================================================================
# INCREMENT 4 — the record (F-3-r2, F-4-r2) and the rationale (F-5-r2)
===================================================================================

## 4a. The decision record, §5

Edit `docs/decisions/530-multi-farm-tenancy.md`. Find this exact text:

```text
deliberately — `FirstRunStatusService` reads the table anonymously — and the #536 scanner keeps
`UserRoles` on its stricter non-tenant track because that split is on CLR shape. Pinned by
`UserRoleTenantWriteTests`, `UserRoleAccountIdModelTests` and `UserRoleAccountIdMigrationTests`.
**Accepted risk, deliberately:** `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens` and
`AspNetRoleClaims` keep no tenant column. Nothing in `src/` writes or reads them, any direct
`db.<Set>` access is already a #536 candidate requiring an allow-list entry, and `AspNetRoles` is
global reference data. The residual is a future `UserManager` claim/login/token call, which no
source walk can see — the same treatment as `AspNetUserRoles` is the fix the day one appears.
```

Replace with:

```text
deliberately — `FirstRunStatusService` reads the table anonymously — and the #536 scanner keeps
`UserRoles` on its stricter non-tenant track because that split is on CLR shape. Pinned by
`UserRoleTenantWriteTests`, `UserRoleAccountIdModelTests` and `UserRoleAccountIdMigrationTests`
(the last four of those tests cover the tracked shape too: loading another farm's row is a
one-line query on a filter-free table, and a relabel is refused by the interceptor and then by the
FK, a tracked `Remove` by the interceptor alone). **What no layer covers on this table, stated
exactly:** under an *unresolved* tenant neither write layer inspects a `DELETE` — the same as every
entity — but on every filtered entity such a scope reads zero rows unless it writes
`IgnoreQueryFilters()`, a marker a reviewer sees, while here there is no filter and so no marker: an
unresolved-tenant `db.UserRoles.Where(…)` + `RemoveRange` would delete every farm's matching
grants with no refusal. Nothing in `src/` does that, and what holds the arm shut is #536's scanner
(every `db.UserRoles` site is a classified candidate in `filter-free-set-sites.tsv`) — a guarded
convention, not a mechanism; narrowing that entry is what reopens it.
**Accepted risk, deliberately:** `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserTokens` and
`AspNetRoleClaims` keep no tenant column. Nothing in `src/` writes or reads them, any direct
`db.<Set>` access is already a #536 candidate requiring an allow-list entry, and `AspNetRoles` is
global reference data. Two residuals no source walk can see: a future `UserManager`
claim/login/token call — the same treatment as `AspNetUserRoles` is the fix the day one appears —
and `RoleManager.DeleteAsync`, which deletes a *global* role and, through
`FK_AspNetUserRoles_AspNetRoles_RoleId … ON DELETE CASCADE`, every farm's grants of it with the
change tracker never holding an `IdentityUserRole` row; no caller exists, and one would be a
farm-wide operation by construction, never a per-farm one.
```

## 4b. The decision record, "How this record is enforced"

Same file. Find this exact text:

```text
- the accepted risk on the four claim/login/token/role-claim Identity tables (§5, #670) —
  **nothing enforces that no writer appears**; a direct `db.<Set>` access is caught by #536's
  scanner, a `UserManager` claim/login/token call is not, and relies on review;
```

Replace with:

```text
- the accepted risk on the four claim/login/token/role-claim Identity tables (§5, #670) —
  **nothing enforces that no writer appears**; a direct `db.<Set>` access is caught by #536's
  scanner, a `UserManager` claim/login/token call is not, and relies on review;
- the unresolved-tenant `DELETE` arm on `AspNetUserRoles` (§5, #670) — **held shut by #536's
  scanner's classification of every `db.UserRoles` site, not by a write layer**; and
  `RoleManager.DeleteAsync`'s cascade across every farm's grants — **nothing enforces that no
  caller appears**; both rely on review;
```

## 4c. The tsv rationale

Edit `tests/Cluckwork.Application.Tests/TenantBypass/Data/filter-free-set-sites.tsv`. Find this exact line (77 — the two TABs are real tab characters; the key before the second TAB must not change):

```text
src/Cluckwork.Infrastructure/Identity/IdentityProvider.cs:1115	db.UserRoles	scoped-by-user-id: DisableUser revokes the user's roles by userRole.UserId == userId (account-unique).
```

Replace with:

```text
src/Cluckwork.Infrastructure/Identity/IdentityProvider.cs:1115	db.UserRoles	scoped-by-user-id: DisableUser READS the target's Owner membership by userRole.UserId == userId (account-unique) for the last-Owner check; it writes no role row (it disables the user, rotates the stamp, bumps the epoch, revokes refresh tokens).
```

## 4d. Build, the narrowed runs, commit, push

Run **G1**. Then **G2 narrowed** (Application form) to `TenantBypass` — expect 34 passed (the row's key is unchanged). Then **G2 narrowed** (Application form) to `TenancyDocsFreshnessTests` — expect 1 passed. Then:

```bash
git status --porcelain   # expect exactly: " M docs/decisions/530-multi-farm-tenancy.md", " M tests/Cluckwork.Application.Tests/TenantBypass/Data/filter-free-set-sites.tsv", "?? docs/plans/670-user-roles-account-id/03-fix-increment-2.md"
git add docs/decisions/530-multi-farm-tenancy.md tests/Cluckwork.Application.Tests/TenantBypass/Data/filter-free-set-sites.tsv docs/plans/670-user-roles-account-id/03-fix-increment-2.md
git commit -m "docs(tenancy): name the unresolved-tenant delete arm and the role-cascade residual on AspNetUserRoles (#670)"
git push origin fix/670-user-roles-account-id
```

## Report back

The commit SHA, the G1 tail, the two narrowed lines (34 and 1), `git status --porcelain` empty after the commit, and confirmation that YOU applied the three blocks verbatim.
