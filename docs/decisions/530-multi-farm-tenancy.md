# Multi-farm tenancy: shared database, row-level isolation, farm-code login (#530)

> **Rule** — the one-paragraph versions live in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break).

**Status:** accepted (epic #530, Phase 1.6)
**Date:** 2026-08-25
**No incident** for the topology decisions — they are forward-looking choices, and a
reader is entitled to know that before treating them as load-bearing. Several
*sub*-decisions below are earned: they name the review round that found the defect.

**This record deviates from [`TEMPLATE.md`](TEMPLATE.md) deliberately.** The template is
shaped for one rule; this epic settled ten, and they interlock — the index swap only
makes sense given the topology, and the validator replacement only makes sense given
the index swap. Each section below answers the template's four questions (what
happened · the rule · why not the obvious alternative · what it does not cover) for
its own decision, and every claim carries the `path:line` that proves it.

---

## 1. Topology: one shared database, row-level `AccountId`

**The rule.** Every tenant-owned row carries `AccountId`, and isolation is enforced by
EF global query filters plus an insert/update interceptor — never by remembering to
write a `WHERE`. Break it and one missing predicate leaks across farms.

Shipped shape: `AccountId` on the entity base (`src/Cluckwork.Domain/Common/Entity.cs:6`),
**27** `HasQueryFilter` registrations (`src/Cluckwork.Infrastructure/Persistence/AppDbContext.cs:62-93`),
and `TenantStampInterceptor` stamping on insert and verifying on update/delete
(`src/Cluckwork.Infrastructure/Persistence/Interceptors/TenantStampInterceptor.cs:26-124`).

The filters fail **closed**: an unresolved `TenantContext` leaves `AccountId` at
`Guid.Empty` (`src/Cluckwork.Infrastructure/Persistence/TenantContext.cs:19-20`), which
matches zero real rows. Note the mechanism honestly — that is C# default-value
semantics, not an explicit guard line. Nothing throws on an unresolved read; it simply
returns nothing.

**Why not database-per-tenant.** The repo was already built for row-level isolation, and
per-tenant databases would discard all of it while breaking four things this repo has
spent real effort on: the `migrate` one-shot verb (#263) becomes a fan-out with
partial-failure semantics, the frozen-migration discipline (#407), the generated schema
docs (#417), and `/health/ready`'s pending-migration backstop. It also costs one Npgsql
pool per tenant inside one process.

**What this does NOT cover.** Noisy-neighbour isolation is partial. A query-filter bug has
all-tenant blast radius. Per-tenant backup and erasure become a query rather than a
`pg_dump`. These are given up knowingly, not overlooked.

**How it is enforced.** The interceptor throws `TenantWriteMismatchException` on a
mismatched write (`TenantStampInterceptor.cs:111-113` for inserts, `:120-122` for
update/delete). An enumerating Roslyn guard added by #536 walks every repository rather
than sampling a list.

---

## 2. Identity: swap the index, never rewrite the username

**The rule.** Per-farm email identity comes from account-scoping the Identity indexes —
`(AccountId, NormalizedUserName)` and `(AccountId, NormalizedEmail)`, both unique
(`src/Cluckwork.Infrastructure/Persistence/Migrations/20260819142300_AccountScopedIdentityIndexes.cs:21-31`).
The username stays the plain email.

**Why not a composite `{accountId}.{email}` username.** It was considered and rejected: it
requires rewriting every existing user row, and its failure mode on a partial rewrite is
total lockout — nobody can log in, including whoever would fix it. The index swap changes
no row data at all.

**Why `(AccountId, NormalizedEmail)` as well.** "The username happens to equal the email"
is a convention in this codebase, not a database invariant. Scoping only the username
index would leave the email index globally unique and the convention silently
load-bearing.

**What this does NOT cover.** Separate user rows stop cross-farm *mutation*, not cross-farm
credential *reuse* — see §9.

---

## 3. The default `IUserValidator` is replaced, not supplemented

**What happened.** The first design added an account-scoped validator *alongside* the stock
one. Review found it cannot work: `UserManager` runs **every** registered validator, and
the stock one always does a global `FindByNameAsync`, so farm B's duplicate email is
rejected as `DuplicateUserName` before Postgres ever evaluates the composite index. Worse,
the validator pipeline reruns on `UpdateUserAsync` — so `ResetPasswordAsync`,
`ChangePasswordAsync`, `AccessFailedAsync` and role changes break too, not just creation.

**The rule.** Register the replacement *before* `AddIdentityCore`, so the stock validator is
never added (`src/Cluckwork.Api/Hosting/CluckworkIdentityServiceCollectionExtensions.cs:50`,
with the ordering pinned by the comment at `:36-37`). Break the ordering and every
duplicate-email path across two farms starts failing, including password resets.

**What this does NOT cover.** Removing the stock validator removes its checks too, so the
replacement reimplements them: the non-blank check and `AllowedUserNameCharacters`
(`src/Cluckwork.Infrastructure/Identity/AccountScopedUserValidator.cs:122-133`) and the
email required/well-formed checks (`:171-181`). Drop those and invalid usernames become
acceptable.

---

## 4. Login takes a farm code, and says so when it is wrong

**The rule.** The login body is `{farmCode, email, password}`
(`src/Cluckwork.Api/Endpoints/Auth/AuthEndpoints.cs:624`); the farm is resolved by slug,
trimmed and lower-cased before lookup
(`src/Cluckwork.Infrastructure/Repositories/AccountRepository.cs:42-49`). An unrecognised
farm code returns a **distinct** response, `Auth.UnknownFarmCode`
(`AuthEndpoints.cs:160,193-206`), not the generic invalid-credentials error.

**Why a distinct response, given it is an enumeration oracle.** Because the generic
alternative is worse in practice: an operator who mistypes their farm code otherwise gets
"invalid credentials" and re-types their password repeatedly until the account locks. The
farm code is not a secret — it appears in URLs (`?farm=<code>`) and on printed material.
The disclosure is accepted deliberately, and bounded by the three rules below.

**Three constraints that come with it, each enforced in code.** The unknown-farm branch:

- is **rate-limited before the lookup** — the limiter is endpoint middleware
  (`AuthEndpoints.cs:44`), which ASP.NET runs before the handler body reaches
  `FindBySlugAsync` at `:193`;
- **carries no slug into logs or metrics** (`AuthEndpoints.cs:196-198`), or an
  attacker-supplied value becomes unbounded log cardinality;
- **deliberately does not call `AccessFailedAsync`** (`:199-201`). There is no user row on
  this branch, and a farm code must never burn a real account's lockout budget. This
  constraint is recorded in the code and nowhere else; it is easy to "fix" by adding the
  call for symmetry, which would hand an attacker a way to lock out any account whose
  farm code they know.

**Why farm code and not subdomain.** The host-agnostic boundary: this repo must build and
run against any host and never name or branch on a hosting provider. Host-header
resolution (#538) would add a fourth resolution source; the shipped design leaves room for
it without disturbing the others.

**Why `Account.Slug` uses a plain unique index**, not a fifth `lower(...)` expression index
(`20260818235944_AddAccountSlug.cs:58-62`): the slug is stored already canonical, so the
expression index would be indexing a transformation of a value that is never in any other
form. The reasoning is repeated at `src/Cluckwork.Domain/Accounts/Account.cs:20-22`.

---

## 5. Isolation is single-assignment, and write-side mismatches throw

**The rule.** `TenantContext.Resolve` may be called twice with the *same* account (a
deliberate no-op, so defensive callers do not become order-dependent) but throws
`TenantReassignmentException` on a differing re-resolve
(`src/Cluckwork.Infrastructure/Persistence/TenantContext.cs:22-35`).

**What happened.** The interceptor originally only *filled* an empty `AccountId` on
**Added** entities: it accepted an explicitly wrong non-empty value and ignored `Modified`
entirely. Review found it, and the shipped version verifies both `OriginalValue` and
`CurrentValue` on update and `OriginalValue` on delete
(`TenantStampInterceptor.cs:87-96`).

**What this does NOT cover.** The guard reads EF's `OriginalValue` as DB provenance; a
detached `Update`/`Remove` can still bypass the theft check. That gap is tracked
separately in #562 and is **not** closed by this epic.

---

## 6. Suspension is immediate for use

**The rule.** `Account.IsActive` is checked on **every authenticated request**, folded into
the per-request read `CredentialEpochMiddleware` already performs for #364's credential
epoch (`src/Cluckwork.Api/Middleware/CredentialEpochMiddleware.cs:54-57`, rejected at
`:69-73`). Suspension also revokes, in the same transaction, every user's credential epoch
and security stamp and every open refresh token
(`src/Cluckwork.Infrastructure/Identity/AccountSuspensionService.cs:164-190`).

**Why it costs nothing extra.** The round trip already happens for #364 and must not be
cached — the round trip *is* the fail-closed guarantee. `AccountIsActive` joins that
existing query rather than adding one.

**Why `Account.Version` must never be the authentication epoch.** It is an EF concurrency
token bumped by every ordinary settings edit, so renaming the farm would log everyone out.

**What this does NOT cover.** "Immediate" means immediate for **use**, not for **issuance**.
A suspension committing inside login's check-then-mint window still returns 200 with an
inert credential. That is a recorded accepted risk with its own record —
[`579-suspension-issuance-window.md`](579-suspension-issuance-window.md) — resting on four
named premises, each pinned by its own guard.

---

## 7. Scale-out: at most one leader, never "exactly once"

**The rule.** Background work runs behind a session-scoped Postgres advisory lock
(`src/Cluckwork.Infrastructure/Jobs/PostgresLeaderLease.cs:209`, registered at
`src/Cluckwork.Api/Hosting/CluckworkJobServiceCollectionExtensions.cs:16-18`). The
contract is **at most one active leader**, with **at-least-once** jobs and idempotent
handlers.

**Why not "exactly once".** A session lock vanishes when its session dies, so a leader can
lose the lock while its work is still running. Any design that says "exactly once" is
describing something this mechanism cannot provide, and a handler written against that
promise breaks the first time a backend restarts.

**Why the lease stays Postgres and does not move to Redis.** A Redis lock's failure mode is
precisely the double-execution the lease exists to prevent. Redis is a cache here; the
lease is a correctness primitive.

**What this does NOT cover.** The guarantee holds on a **session-pinned** endpoint — a
direct connection or a session-pooled proxy. Under a **transaction-pooling** proxy the lock
can migrate across backends and single-leader is *not* guaranteed; a backend-PID affinity
check narrows the window without closing it. That topology relies on the at-least-once +
idempotent contract until a dedicated session-pinned lease endpoint (#556) lands.

---

## 8. Shared state: Redis only, with an in-process fallback, behind capability ports

**The rule.** Shared state lives behind **capability-specific** ports — not one generic
key/value interface — with Redis primary and an in-process fallback wrapped by resilient
decorators (`src/Cluckwork.Infrastructure/SharedState/SharedStateRegistration.cs:50,59-63`).

**A blank connection string is a MODE, not an error.** It registers the in-process
implementations and returns (`SharedStateRegistration.cs:31-35`), and the boot guard
accepts it even for the serving role
(`src/Cluckwork.Api/Hosting/CluckworkSharedStateServiceCollectionExtensions.cs:37-38`).
The consequence must be stated plainly: **the app cannot tell a deliberate single-instance
deployment from a misconfigured multi-replica one.** The invariant "replicas > 1 ⇒ Redis
configured" is therefore **deploy-owned**, not app-enforced.

**Why fall back rather than fail open or fail closed.** The two capabilities have different
failure economics. The report concurrency cap is capacity protection and must **not** fail
open — it degrades to a bounded per-instance ceiling. The IP-keyed auth limiter is abuse
mitigation, so N replicas enforcing N budgets is a degradation rather than an outage.
Step-up grant replay is neither, and fails **closed**. Never unlimited, never a full
outage.

**Why the logout epoch is a user-row column and not shared state.** It is not cache-shaped:
one entry per user who has ever logged out, bounded by user count rather than traffic, and
it never expires. Redis would add **silent eviction** under `maxmemory` pressure —
load-dependent, invisible, and biased towards exactly the never-expiring keys an LRU policy
discards first. It is the same shape as `CredentialEpoch` (#364), so it lives beside it:
`src/Cluckwork.Infrastructure/Identity/ApplicationUser.cs:48`, compared at
`src/Cluckwork.Infrastructure/Identity/PersistentStepUpGrantRegistry.cs:53-61`.

That is what makes Redis-only safe: **nothing left in the volatile store causes a security
failure if it disappears.**

**Recorded dissent.** Both reviewers of the epic design recommended dropping Redis
entirely, on the grounds that Postgres is already mandatory and the volumes here — logins,
step-ups and report starts — do not justify a second dependency. The owner weighed that
against the native primitives and the absence of any expiry-cleanup work, and chose Redis.

**Atomicity consequence.** `TryConsumeIfNotLoggedOut` once checked the logout epoch and
consumed the grant id under **one** lock. With the epoch in Postgres and the grant id in
Redis, no single lock spans both stores, so the order is load-bearing: **consume in Redis
first, then read the epoch, and refuse on mismatch.** A logout landing between the two is
caught by the read; one landing after it is genuinely after admission. The cost is that a
logout-revoked grant burns its id.

---

## 9. Provisioning: two verbs, one Owner-creation core

**The rule.** `bootstrap-admin` keeps its first-run contract for the default account;
`provision-account` creates farm 2+. Both reach the same Owner-creation core —
`IIdentityProvider.CreateUserAsync` — with the same generated-password and
`MustChangePassword` semantics
(`src/Cluckwork.Infrastructure/Identity/AccountProvisioner.cs:109-111` and
`src/Cluckwork.Infrastructure/Identity/FirstRunAdminService.cs:340-342`).

**The #283 boundary, stated precisely.** The migration owns the **default** account's
reference data, guarded with `WHERE NOT EXISTS` and still unmodified
(`20260801190854_InitialCreate.cs:1426` and following). The **provisioner** owns a new
account's grades and unit conversions (`AccountProvisioner.cs:102-103`). The property #283
protects is *never boot-invoked* — not *never at runtime*. Reading it as the latter makes
`provision-account` look like a violation when it is the intended path.

**Accepted costs of separate user rows.** There is no global "this person has left the
company" operation. MFA, passkeys and SSO would be per farm. There is no supported way to
correlate one human across farms. And separate rows stop cross-farm *mutation*, not
cross-farm credential *reuse*: a malicious farm owner can set a known password and try it
elsewhere. Mitigated, not eliminated, by every administrator-set credential being
generated, temporary and `MustChangePassword` — cleared only by a self-change.
Cross-account password-history comparison is explicitly **not** attempted; it would
recreate the shared-person coupling the design exists to avoid.

---

## 10. An unattributable value cannot be rescued by any rule about when to read it (#586)

**What happened — earned, and it took four review findings.** The pre-paint script that
picks a farm's colour before first paint read one un-namespaced `localStorage` key. Slice
#586 namespaced it per farm, and kept the old key as a *fallback* so upgraded devices would
not flash the default palette. Reviewers broke that fallback four times:

1. an emptied roster (`"[]"`) is not a single-farm device — `removeFarmCode` writes it;
2. a roster of one does not mean one farm — "Forget this farm" **shrinks** the roster;
3. neither does an absent roster, once history is considered: a device that used Forget on
   an **older build** shrank its roster while leaving the key untouched;
4. and the same class reappeared as an async race, where a response fetched under farm A
   was cached under farm B after a switch.

Reproduced against the shipped script: roster `["farm-b"]` plus the old key holding
farm-A's colour painted **farm-A's colour on farm-B's login screen**.

**The rule.** When successive fixes all constrain *when* a value may be trusted, the value
is unattributable and the answer is to **delete it**, not to add another condition. #586
purges the key and never reads it. Each earlier fix was correct where it looked and missed
a case elsewhere, which is the signature of an invariant with no owner.

**The generalisable half.** Anything written before a tenant discriminator existed cannot be
attributed to a tenant afterwards, however clever the inference. Attribute at **write**
time or delete.

**What this does NOT cover.** The accepted cost is real: an upgraded device paints the
default palette until its next explicit login. Closing that needs `Account.Slug` on the
`/account` response so a cookie-restored session can repopulate its own cache — scoped out
of #586 as an API+SPA change and **not** implemented.

**Also amended by #586:** its acceptance criterion "a device holding two farms never paints
farm A's palette for farm B" was **unachievable as written**, because Forget lets a device
hold two farms while remembering one. The shipped guarantee is: never paints a palette for
a farm the device still **remembers**.

---

## Accepted disclosures, collected

Two, both deliberate and both bounded above:

- the **distinct unknown-farm response** (§4) confirms whether a farm code exists;
- the **device-local farm-code cache** is a durable roster of which farms a browser profile
  uses, which matters on a shared device.

## How this record is enforced

Most of it is enforced by the guards named per section. The parts that are **not**:

- the deploy-owned "replicas > 1 ⇒ Redis configured" invariant (§8) — **nothing in this
  repo enforces it**; it relies on the deployment repo;
- the "no slug as a metric label" and "no `AccessFailedAsync` on the unknown-farm branch"
  constraints (§4) — **nothing enforces these; they rely on review** and on the comments at
  the call site;
- §7's transaction-pooling caveat — **nothing detects that topology**; it relies on whoever
  configures the endpoint.

An unenforced invariant stated as a fact is a bug waiting to happen, so they are listed
here as unenforced rather than described as guarantees.
