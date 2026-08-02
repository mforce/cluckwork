# Disable a user, and change a user's email address

**Date:** 2026-08-01
**Phase:** 1.1 (epic #14)
**Status:** design, awaiting implementation plan

## Problem

The Users page ships create / list / rename / set-password / flock-assign
(`UserEndpoints`). There is no way to take someone's access away, and no way to
correct the address they log in with.

A worker leaves the farm and the only lever an Owner has is resetting their
password — which leaves a live account behind, reachable by anyone who learns
the new password. A typo'd email at creation time is unfixable: the account must
be abandoned and a second one created, leaving a phantom user attached to
whatever history the first one recorded.

Both gaps live on the same screen and share the same machinery, so they are
designed together and shipped as two PRs.

## Scope

**In:** an Owner can disable and re-enable a user; an Owner can change a user's
email address.

**Out:** hard delete. Users are referenced by audit rows, created-by trails on
daily entries and sales orders, and refresh-token history — a delete either
cascades and shreds the audit log or nulls out and leaves history recorded by
"(nobody)". Personal-data erasure is tracked separately as **#272** (GDPR + PH
DPA), which is where a real delete belongs, behind a retention policy.

**Out:** role editing. `CreateUser` fixes a role for the life of the account, so
promoting a worker to Manager today means creating a second account. Same page,
same step-up machinery, genuinely a bigger gap than either feature here — filed
as its own slice rather than folded in.

## Decisions

### Disable is one flag serving both offboarding and suspension

A disabled account covers "this person left" and "this person is suspended
pending an investigation" with a single flag. That second case is what sets the
enforcement bar: a suspension that takes up to 15 minutes to bite is not a
suspension.

### Enforcement is a per-request check, not a token-expiry wait

Access tokens live ~15 minutes and there is no server-side denylist
(`IIdentityProvider` documents this on `SetUserPasswordAsync`). So a disable
reaches an already-issued token one of three ways: wait for it to expire, check
the database on every authenticated request, or cache that check.

We check per request. The traffic is a farm's worth of users, not a public API —
one indexed PK lookup is noise next to what a daily-entry POST already does, and
it is the only option under which the flag means what the UI claims it means. A
short-TTL cache would be per-process in-memory, reintroducing the multi-replica
problem **#338** already tracks for step-up grants, and quietly downgrading
"immediate" to "within 30s, per replica."

### Two guards, or the farm can lock itself out

Users administration is `OwnerOnly`, so an Owner can disable another Owner, and
`bootstrap-admin` creates exactly one Owner by default. Disabling yourself, or
disabling the last active Owner, would leave a farm with no way back in through
the UI — and `recover-admin` resets a password without re-enabling anything.

Both are refused. This is `bootstrap-admin`'s exactly-one-first-run-Owner
invariant approached from the other end.

Owners remain disable-able in general: "one of our two Owners left" is a real
case, and routing it through a CLI would be over-restriction.

### `recover-admin` re-enables its target

`BreakGlassResetAsync` clears `DisabledAt`. Without this the break-glass path
cannot recover from a disable — the exact scenario where it is most needed.

### Changing an email revokes the target's sessions

The email is the login identifier. Changing it rotates the security stamp,
revokes every refresh token, and invalidates outstanding step-up grants, the
same posture `SetUserPasswordAsync` already takes. The collateral is one
re-login; the principle is that a changed identifier re-establishes the session.

### Both operations require a step-up grant (#308)

Consider a stolen Owner access token with ~15 minutes of validity. Change a
worker's email to an attacker-controlled address, then reset that worker's
password — which is *not* step-up-gated for worker targets
(`ResetWorkersPassword_NeedsNoStepUp_OrdinaryAdministrationStaysUngated`). That
converts a short window into durable access under an account nobody has reason
to audit. Gating the identifier change closes the half of that chain the
attacker cannot otherwise obtain.

Disable is gated on the same grounds: it is the lever that removes access.

### No email verification exists, and the UI says so

There is no SMTP path in this application. The Owner's word is the verification.
The Help page states this so nobody waits for a confirmation mail that will never
arrive.

## Design

### 1. Storage

`ApplicationUser` gains:

```csharp
public DateTimeOffset? DisabledAt { get; set; }   // null = active
public Guid? DisabledBy { get; set; }             // the Owner who did it
```

A nullable timestamp, not an `IsActive` bool. The domain aggregates (`Product`,
`EggGrade`, `InventoryItem`, `ExpenseCategory`) use a bool because they carry no
audit trail of their own; a user account is a security object where "when, and by
whom" is the first question asked. `DisabledBy` is a plain `Guid?` with no FK —
the referenced Owner may themselves be disabled later, and an FK buys nothing.

One new migration: two nullable columns, no backfill, no data statements.
Nothing resembling the hand-carried `InitialCreate` content, so a plain
`dotnet ef migrations add` is correct and `MigrationSecurityReviewTests` stays
green (no INSERTs, no credential-shaped rows).

### 2. Application layer

Two new features under `Features/Users/`, handler-per-feature:

**`SetUserActive/`** — `SetUserActiveCommand(Guid UserId, bool Active, string? StepUpToken)`.
One handler backs both directions, mirroring `SetProductActiveHandler`. Guards,
in order:

| Condition | Result |
|---|---|
| Target is the caller | 422 `Users.CannotDisableSelf` |
| Target is the last active Owner (disable only) | 422 `Users.LastOwner` |
| Target not in this account | 404 |

On disable: stamp `DisabledAt` / `DisabledBy`, rotate `SecurityStamp`, revoke
every refresh token, invalidate outstanding step-up grants. On enable: clear
`DisabledAt` / `DisabledBy`. Audit `User.Disable` / `User.Enable`.

**`ChangeUserEmail/`** — `ChangeUserEmailCommand(Guid UserId, string Email, string? StepUpToken)`.
Writes `Email`, `UserName`, and `NormalizedEmail` together — never one alone.
409 on the unique index. Then the same revoke-everything treatment as a disable.
Audit `User.EmailChanged`, recording old → new.

Both call `IStepUpGrantService.ValidateAsync` first, in the shape
`CreateUserHandler` already uses.

`IIdentityProvider` gains `SetUserActiveAsync` and `ChangeUserEmailAsync`, both
account-scoped so a foreign id resolves to NotFound rather than a cross-tenant
write. `UserSummary` and `UserProfile` gain `DisabledAt`.

#### Retry hazard (#269 family)

Both mutate an Identity user through `UserManager`, which is a `ConcurrencyStamp`
CAS. A replayed `SaveChanges` after an ambiguous commit re-issues the `UPDATE`
under a stale stamp, matches zero rows, and reports failure for work that landed.

Unlike `AccessFailedAsync`, neither is a counter — a replay writes nothing new
and is harmless in itself. So the cure is the `RefreshAsync` pattern, **not**
`SingleAttemptExecution`: on failure, ask the database whether this attempt's own
write is durable (is `DisabledAt` already our value? is the email already ours?),
and let the original error stand otherwise. Fails closed. Both are also
idempotency-key-wrapped writes, so the client-retry path covers most of it
already.

### 3. API surface

```
POST   /api/v1/users/{id}/disable     -> 204   (step-up)
POST   /api/v1/users/{id}/enable      -> 204   (step-up)
PUT    /api/v1/users/{id}/email       -> 204   (step-up; 409 on conflict)
```

Disable/enable are separate verbs rather than a field on `PUT /users/{id}`,
following `POST /products/{id}/deactivate` and `/egg-grades/{id}/deactivate`.

Email is separate from `UpdateUser` because it carries different auth weight —
`UpdateUser` (display name) is ungated and stays that way.

All three inherit the group's `OwnerOnly` policy and the `Idempotency-Key`
requirement. `WithMaxRequestBodyBytes(2048)` on the email PUT, matching the
password endpoint's reasoning.

`ListUsers` and `GetUser` responses grow `disabledAt`.

### 4. Enforcement

New `DisabledUserMiddleware`, placed beside `MustChangePasswordMiddleware`:

- **before `UseAuthorization`** — so it applies uniformly regardless of an
  endpoint's `AuthPolicies` tier;
- **before the idempotency middleware** — so a blocked write burns no key.

One indexed PK lookup per authenticated request. Refuses with 401 and a distinct
`Auth.AccountDisabled` title, so the SPA can show "Your account has been
disabled" rather than a generic session-expired bounce.

`LoginAsync` refuses a disabled user with the *same* generic
`Identity.InvalidCredentials` as a wrong password, still paying the PBKDF2 cost —
the reasoning the lockout branch already documents, so the reply never reveals
account state. `RefreshAsync` refuses too.

### 5. SPA

Users page lists disabled users inline, always visible: a "Disabled" badge and a
de-emphasized row, with the action toggling to "Enable". Hiding people who still
own history invites "where did Maria go?" and a duplicate account. A
`Show disabled` filter is the right answer once a farm has years of turnover —
`ListUsers` returns a flat unpaged array today, so that is a larger change than
this feature warrants.

Disable goes through a confirm dialog (existing `useConfirm()` hook): names the
user, warns that they are signed out immediately and lose access, Cancel /
Disable. Enable is a plain one-click — it is not destructive.

Email editing is a form on the user row, surfacing the 409 as a field error.

### 6. Testing

**Domain / Application** — guard rules: self-disable, last-active-Owner, both
directions of the toggle, email normalization, email conflict.

**Integration**

- A disabled user cannot log in, and the refusal is indistinguishable from a
  wrong password.
- A disabled user cannot refresh.
- A **live access token** stops working on the next request. This is the point of
  the middleware and the test that fails if it is ever dropped.
- Step-up grants die with the disable.
- An email change signs the target out; the new address logs in; the old does not.
- 409 on an address already taken.
- `recover-admin` re-enables a disabled target.

**Race** — two Owners disabling each other concurrently must not land at zero
active Owners. This is the `Version`-bump family of bug the repo has shipped
three times, wearing Identity's `ConcurrencyStamp` clothing. It gets a
parallel-race integration test like every other mutation.

**Retry** — a `RetryBoundaryTests` case per the hazard in §2, using
`TransientCommitFaultInterceptor`. Not `TransientCommandFaultInterceptor`: these
are multi-statement saves, where that interceptor is a fail-*before* and yields a
green test with the defect fully present.

**SPA (Vitest)** — the confirm dialog, disabled-row rendering, the enable path,
the email-edit form including the 409.

### 7. Docs

- `specs/product/GLOSSARY.md` gains **disabled user**.
- Help page gains a Users section: what disabling does, that it is reversible,
  that history is retained, and that an email change sends no verification mail.
- Strings ship with es/tl inline per the translate-now policy, added to
  `TRANSLATED_NAMESPACES`.

## Delivery

Two issues against epic #14, both added to its checklist:

1. **Disable / enable a user** — storage, guards, middleware, `recover-admin`
   re-enable, SPA toggle + confirm.
2. **Change a user's email** — reuses the revoke-sessions machinery from (1),
   so it ships second.

Role editing is filed separately.
