# Disable a user, and change a user's email address

**Date:** 2026-08-01
**Phase:** 1.1 (epic #14)
**Status:** design, awaiting implementation plan
**Revision:** second draft, after an adversarial review of the first. The
changes are substantive and are recorded in *What the first draft got wrong* at
the end — read that before treating any of this as a small delta.

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

**Out:** role editing (**#355**). `CreateUser` fixes a role for the life of the
account, so promoting a worker to Manager today means creating a second account.
Same page, same machinery, genuinely a bigger gap than either feature here —
filed as its own slice.

**Out, but adjacent and newly filed:** step-up (#308) currently gates only
*Owner* creation, so a stolen Owner access token can already mint a Manager with
an attacker-controlled password and keep access long past the token's own
lifetime. That is a gap in #308's coverage, not something this feature
introduces or can close — see *Threat model* below.

## Decisions

### Disable is one flag serving both offboarding and suspension

A disabled account covers "this person left" and "this person is suspended
pending an investigation" with a single flag. That second case is what sets the
enforcement bar: a suspension that takes up to 15 minutes to bite is not a
suspension.

### The per-request check is a credential epoch, not the disabled flag

This is the central mechanism, and the first draft got it wrong by reaching for
the flag directly.

An access token today carries `sub`, `email`, `account_id`, `role`, and
`must_change_password` — and nothing else (`JwtTokenService`). Validation checks
signature, issuer, audience, and lifetime (`CluckworkIdentityServiceCollectionExtensions`).
There is no `SecurityStamp` in the token and nothing on the request path reads
one. Three consequences follow, and the flag cannot address any of them:

1. Rotating `SecurityStamp` on a change **does not invalidate a live access
   token**. It invalidates step-up grants, which bind to the stamp, and nothing
   else.
2. If the per-request check reads `DisabledAt`, then **re-enabling resurrects
   every still-unexpired pre-disable access token**. A suspension lifted after
   five minutes hands back the exact session it was meant to end.
3. An email change would revoke refresh tokens but leave the target's current
   tab working — the spec's own promise of "signs the target out" would be false.

So the user carries a **credential epoch**: a monotonically increasing `int`,
stamped into every access token as a claim, compared against the database on
every authenticated request. A mismatch is a 401.

```csharp
public int CredentialEpoch { get; set; }   // starts at 0, only ever increments
```

Bumping it is the single act that means "every credential minted before now is
dead." Disable bumps it. Email change bumps it. Password reset bumps it (all
three paths — self-service, an Owner's `SetUserPasswordAsync`, break-glass).
**Enable does not bump it and does not restore the old value** — that asymmetry
is the whole point of a monotonic counter, and it is what stops finding (2)
above.

This subsumes the disabled check rather than sitting beside it: the middleware
compares one claim to one column, and `DisabledAt` goes back to being what it
should be — the record of a state, not the enforcement mechanism.

`RefreshAsync` re-checks both the disabled state and the epoch. That closes the
issuance race below.

### Why not a cache

A short-TTL cache of the check would be per-process in-memory, reintroducing the
multi-replica problem **#338** already tracks for step-up grants and quietly
downgrading "immediate" to "within 30s, per replica." The traffic here is a
farm's worth of users; one indexed PK lookup per authenticated request is noise
next to what a daily-entry POST already does.

### Revoking refresh tokens is not enough on its own

`RefreshAsync` loads the token and user, then revokes the presented token and
inserts a child. `RevokeAllActiveForUserAsync` is a separate bulk update. A
refresh already in flight can therefore load *before* a disable, let the bulk
revoke commit, and insert its new active child *after* — a live refresh token
the disable never saw. For an email change that child works immediately; for a
disable it lies dormant and works the moment someone re-enables.

The epoch closes this: the child was minted under the old epoch, so `RefreshAsync`
refuses it, and any access token it could have produced fails the per-request
check. Bulk revocation stays — it is still the right thing to do — but it is no
longer what the guarantee rests on.

### The last-active-Owner guard needs an account-wide lock

Two guards are required or the farm can lock itself out: a user may not disable
themselves, and the last active Owner may not be disabled. `bootstrap-admin`
creates exactly one Owner by default, and `recover-admin` resets a password
without re-enabling anything.

A naive check is **not race-safe**, and Identity's `ConcurrencyStamp` does not
save it. Owners A and B disable each other concurrently: each reads two active
Owners, each targets a *different* user row, both guards pass, both commit, zero
active Owners remain. There is no shared concurrency token between two different
rows.

The operation needs an account-wide serialization point, taken inside the same
transaction, using the pattern already in the repo
(`AccountRepository.GetCurrentLockedAsync`, `SELECT … FOR UPDATE`):

1. Lock the tenant's `Accounts` row.
2. Re-read, account-scoped: the target user, its Owner membership, and the
   active-Owner count.
3. Apply the guards.
4. Write, and commit.

Every future path that can remove an Owner — role demotion (#355), any eventual
delete — must take the same lock. That is a standing constraint, not a detail of
this slice.

### `recover-admin` re-enables, and gains a lookup that does not need the email

`BreakGlassResetAsync` clears `DisabledAt` and bumps the epoch. Without the
first, the break-glass path cannot recover from a disable — the exact scenario
where it is most needed.

`AdminRecoveryService` currently locates its target *by email*, which is
circular if the lockout was caused by an email typo. It gains an
`--account`-plus-`--user-id` lookup as an alternative to `--email`.

### Changing an email

The email is the login identifier. The change bumps the epoch, revokes every
refresh token, and invalidates step-up grants; the target logs in again at the
new address. The collateral is one re-login.

**All four columns are written together**, through Identity's configured
normalizer: `Email`, `NormalizedEmail`, `UserName`, **and `NormalizedUserName`**.
The unique index is `UserNameIndex` on `NormalizedUserName`; `EmailIndex` on
`NormalizedEmail` is **not** unique. Writing only the first three would leave the
old address permanently reserved, bypass conflict detection on the new one, and
allow duplicate `NormalizedEmail` rows that make `FindByEmailAsync` ambiguous.

A **sole Owner may not change their own email.** Step-up proves the old
password; it does not prove the new address was typed correctly. A typo would
change the only login identifier and end the session in the same request. Same
guard family as self-disable.

The conflict response mirrors what `CreateUser` already does, which deliberately
does not reveal whether a colliding address belongs to another tenant — email
uniqueness is global because login has no account discriminator, and a bare
"address already taken" would be a cross-tenant oracle the existing code is
careful to avoid.

### There is no email verification, and the UI says so

There is no SMTP path in this application. The Owner's word is the verification.
The Help page states this so nobody waits for a confirmation mail that will
never arrive.

## Threat model, stated accurately

Both operations require a step-up grant (#308), on the grounds that they are the
levers that remove or repoint access.

The first draft justified this with a stolen-Owner-token chain and **that
justification was wrong**. `CreateUserHandler` requires step-up only when the new
user's role is Owner; creating a Manager, Sales user, or Worker is ungated, as is
resetting any non-Owner's password. A stolen Owner bearer token already converts
into durable Manager access without touching anybody's email. Gating email change
does not close that chain, because the chain does not need it.

Two honest statements replace it:

- Gating these operations is still right: they are destructive and
  identity-altering, and #308's purpose is to require a recent proof of the
  human before such an act.
- The durable-persistence gap is real and belongs to #308, not here. Closing it
  means gating *all* administrative user creation and *every* administrative
  password reset, which is a change to #308's stated design ("avoid a blanket
  prompt") and needs its own decision. **Filed separately.**

One residual limit, documented rather than fixed: step-up grants live in an
in-process registry, so "single-use" holds per replica. A captured access token
plus grant can be spent once against each. That is **#338**, pre-existing, and it
becomes more load-bearing once step-up is what stands between a stolen token and
disabling the farm's users.

## Design

### 1. Storage

`ApplicationUser` gains:

```csharp
public DateTimeOffset? DisabledAt { get; set; }   // null = active; a record, not the gate
public Guid? DisabledBy { get; set; }             // denormalized audit metadata
public int CredentialEpoch { get; set; }          // monotonic; the gate
```

`DisabledAt` is a nullable timestamp rather than the `IsActive` bool the domain
aggregates use: a user account is a security object where "when, and by whom" is
the first question asked.

`DisabledBy` carries **no FK**, and the honest reason is that it is denormalized
audit metadata, not a modelled relationship. (The first draft claimed an FK would
be invalidated by later disabling the referenced Owner. That is false — disabling
a row does not break a foreign key.) A plain `Guid?` does permit a cross-account
value; the writer is always the acting Owner in the same account, and the column
is never read for authorization.

One migration: three columns, `CredentialEpoch` non-nullable with default `0`,
no data statements. `MigrationSecurityReviewTests` stays green.

### 2. Application layer

Two features under `Features/Users/`, handler-per-feature.

**`SetUserActive/`** — `SetUserActiveCommand(Guid UserId, bool Active, string? StepUpToken)`.
One handler backs both directions, mirroring `SetProductActiveHandler`.

Inside one transaction, after taking the account lock:

| Condition | Result |
|---|---|
| Target is the caller | 422 `Users.CannotDisableSelf` |
| Target is the last active Owner (disable only) | 422 `Users.LastOwner` |
| Target not in this account | 404 |

On disable: stamp `DisabledAt` / `DisabledBy`, **bump `CredentialEpoch`**, rotate
`SecurityStamp`, revoke every refresh token, invalidate step-up grants.
On enable: clear `DisabledAt` / `DisabledBy`, and **leave the epoch alone**.
Audit `User.Disable` / `User.Enable`.

**`ChangeUserEmail/`** — `ChangeUserEmailCommand(Guid UserId, string Email, string? StepUpToken)`.
Writes the four columns atomically through the normalizer, bumps the epoch, and
takes the same revoke-everything path. Refuses a sole Owner changing their own
address. Audit `User.EmailChanged`, old → new.

Both call `IStepUpGrantService.ValidateAsync` first, in the shape
`CreateUserHandler` uses. `IIdentityProvider` gains `SetUserActiveAsync` and
`ChangeUserEmailAsync`, both account-scoped so a foreign user id resolves to
NotFound, never a cross-tenant write. `UserSummary` / `UserProfile` gain
`DisabledAt`.

`StepUpGrantService.IssueAsync` additionally refuses a disabled user, so a grant
cannot be minted in the window after a concurrent disable.

**On retries:** nothing special is needed. These are HTTP writes, so
`IdempotencyMiddleware` already runs the whole downstream pipeline inside its
request-wide transaction under `SingleAttemptExecution` — the inner Identity save
is never independently replayed, and an ambiguous outer commit is resolved by the
middleware replaying its own published response. A non-HTTP caller (a CLI verb)
wraps the user update, token revocation, and audit write in `AmbientTransaction`,
whose owned path is already single-attempt. (The first draft prescribed a
`RefreshAsync`-style durability probe here. That was wrong twice over: the save is
not independently replayed, and "the email is already ours" is not
attempt-unique evidence the way a freshly minted 256-bit token hash is.)

### 3. API surface

```
POST   /api/v1/users/{id}/disable     -> 204   (step-up)
POST   /api/v1/users/{id}/enable      -> 204   (step-up)
PUT    /api/v1/users/{id}/email       -> 204   (step-up; 409 on conflict)
```

Disable/enable are separate verbs rather than a field on `PUT /users/{id}`,
following `POST /products/{id}/deactivate`. Email is separate from `UpdateUser`
because it carries different auth weight — `UpdateUser` (display name) is
ungated and stays that way. All three inherit the group's `OwnerOnly` policy and
the `Idempotency-Key` requirement. `WithMaxRequestBodyBytes(2048)` on the email
PUT, matching the password endpoint.

`ListUsers` and `GetUser` grow `disabledAt`.

### 4. Enforcement

`JwtTokenService` stamps the epoch into every access token.

New `CredentialEpochMiddleware`, registered at an explicit position in
`Program.cs`:

```
UseAuthentication → TenantResolutionMiddleware → CredentialEpoch
    → MustChangePassword → UseAuthorization → Idempotency
```

**After authentication** — before it, every caller looks anonymous and the
middleware is inert. **Before `UseAuthorization`** — so it applies uniformly
regardless of an endpoint's `AuthPolicies` tier. **Before idempotency** — so a
blocked write burns no key.

It must:

- look the user up by **both `UserId` and `AccountId`** — `ApplicationUser` has
  no global tenant query filter, so the filter that protects every other entity
  is absent here;
- use an untracked projection, so it does not poison the request's Identity
  change tracker;
- skip re-execution under `IExceptionHandlerFeature`, as
  `MustChangePasswordMiddleware` does;
- leave `auth/logout` reachable, so a dead session can clear its own cookie.

It answers 401 with a distinct `Auth.CredentialsSuperseded` title, and disable
additionally surfaces `Auth.AccountDisabled`, so the SPA can tell "you were
disabled" from "your credentials were rotated."

`LoginAsync` refuses a disabled user with the *same* generic
`Identity.InvalidCredentials` as a wrong password, still paying the PBKDF2 cost —
the reply must never reveal account state, the reasoning the lockout branch
already documents. `RefreshAsync` refuses a disabled user **and** any token
minted under a superseded epoch.

### 5. SPA

Disabled users stay listed inline: a "Disabled" badge, a de-emphasized row, and
the action toggling to "Enable". Hiding people who still own history invites
"where did Maria go?" and a duplicate account. A `Show disabled` filter is the
right answer once a farm has years of turnover — `ListUsers` returns a flat
unpaged array today, so that is a larger change than this warrants.

Disable goes through a confirm dialog (existing `useConfirm()`): names the user,
warns they are signed out immediately and lose access, Cancel / Disable. Enable
is one click — not destructive. Email editing is a form on the user row,
surfacing the 409 as a field error.

**The 401 reason has to survive the auth teardown.** Today `client.ts` turns any
authenticated 401 into refresh-and-retry then calls a parameterless
`onUnauthenticated`, and `Login.tsx` maps every 401 to "invalid credentials". So
returning a distinct title is necessary but not sufficient: the teardown path
must carry the original error title through to the login screen, or the promised
"Your account has been disabled" can never render. That plumbing is part of this
slice, not an afterthought.

### 6. Testing

**Application** — the guards: self-disable, last-active-Owner, sole-Owner
self-email-change, both toggle directions, email normalization, conflict.

**Integration**

- A disabled user cannot log in, and the refusal is indistinguishable from a
  wrong password.
- A disabled user cannot refresh.
- A **live access token stops working on the next request**. The test that fails
  if the middleware is ever dropped.
- **Re-enabling does not resurrect a pre-disable access token.** The test that
  fails if the epoch degrades back into a boolean check.
- An email change kills the target's live access token, not just their refresh
  tokens.
- Step-up grants die with the disable, and none can be issued to a disabled user.
- 409 on an address already taken, including one held by **another account**,
  without revealing which.
- A foreign-account user id → 404 on every new endpoint.
- `recover-admin` re-enables, and can locate its target without the email.

**Race** — barrier-controlled, because sequential tests pass with every one of
these bugs present:

- Two Owners disabling each other concurrently must not reach zero active Owners.
- A refresh in flight across a disable must not leave a usable child token.
- A refresh in flight across an email change must not leave a usable child token.
- A step-up issuance racing a disable must not produce a usable grant.

**Middleware ordering** — asserted explicitly, since the guarantee is positional
and a silent reorder fails open.

**SPA (Vitest)** — confirm dialog, disabled-row rendering, enable path, the
email-edit form including the 409, and the disabled-reason surviving all the way
to the login screen.

### 7. Docs

- `specs/product/GLOSSARY.md` gains **disabled user** and **credential epoch**.
- Help page: what disabling does, that it is reversible with history retained,
  and that an email change signs the user out and sends no verification mail.
- Strings ship with es/tl inline per the translate-now policy, added to
  `TRANSLATED_NAMESPACES`.

## Delivery

1. **#356 — disable / enable a user.** Carries the credential epoch, the account
   lock, the middleware, the `recover-admin` changes, and the SPA plumbing for
   the 401 reason. Larger than the first draft implied.
2. **#357 — change a user's email.** Reuses all of it; adds the four-column
   atomic write and the sole-Owner guard. Smaller than the first draft implied —
   the retry work it described does not exist.

Role editing is **#355**. The #308 step-up-coverage gap is filed separately.

## What the first draft got wrong

Recorded so the same reasoning is not re-derived later.

1. **`SecurityStamp` rotation was treated as session termination.** It is not —
   the token carries no stamp and nothing on the request path reads one. This
   invalidated the draft's central promise for both features.
2. **The per-request check read `DisabledAt`.** Re-enabling would then resurrect
   every unexpired pre-disable access token.
3. **The last-Owner guard was called race-safe on the strength of
   `ConcurrencyStamp`.** Two Owners disabling each other touch different rows and
   share no concurrency token. It needs an account-wide lock.
4. **The email write omitted `NormalizedUserName`** — the column that actually
   carries the unique index. `NormalizedEmail`'s index is not unique.
5. **The step-up justification described a takeover chain that does not require
   an email change.** A stolen Owner token already mints a Manager, ungated.
6. **A `RefreshAsync`-style durability probe was prescribed for a save that is
   never independently replayed**, on evidence that is not attempt-unique.
7. **Bulk refresh-token revocation was assumed sufficient.** A concurrent refresh
   can insert a live child after the revoke commits.
8. **Self-email-change was not guarded**, so a sole Owner could lock themselves
   out with a typo, against a `recover-admin` that looks its target up by email.
9. **The `DisabledBy` FK omission was justified with a false claim** about
   foreign keys being invalidated by a disabled referent.
