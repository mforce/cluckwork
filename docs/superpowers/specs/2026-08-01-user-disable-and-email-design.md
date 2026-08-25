# Disable a user, and change a user's email address

**Date:** 2026-08-01
**Phase:** 1.1 (epic #14)
**Status:** design, awaiting implementation plan
**Revision:** tenth draft. Every round of review has found a real defect in the
previous one's fix — including after this document was declared frozen once.
The corrections are recorded at the end in the *What the Nth draft got wrong*
sections, and are the substance.

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
designed together — and shipped as three PRs, because the machinery has to be
deployed and drained before either feature can honestly promise what it promises.
See *Delivery*.

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
public int CredentialEpoch { get; set; } = 1;   // starts at 1, only ever increments
```

It starts at `1`, not `0`, and `0` is permanently retired — see *Storage* for why
that one-off matters more than it looks.

Bumping it is the single act that means "every credential minted before now is
dead." Disable bumps it. Email change bumps it. Password reset bumps it (all
three paths — self-service, an Owner's `SetUserPasswordAsync`, break-glass).
**Enable does not bump it and does not restore the old value** — that asymmetry
is the whole point of a monotonic counter, and it is what stops finding (2)
above.

This subsumes the disabled check rather than sitting beside it: the middleware
compares one claim to one column, and `DisabledAt` goes back to being what it
should be — the record of a state, not the enforcement mechanism.

The epoch has to be bound to **every** credential the system will later accept,
not only the access token. The refresh token gets its own stamped copy, checked
ahead of the grace/replay branch — see the next section for why leaving it off
defeats the whole mechanism.

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

**The epoch closes this only if the refresh token carries its own issuance
epoch.** Putting `CredentialEpoch` on the user alone is not enough, and the
second draft got this wrong: the child's *access* token would indeed be rejected
(minted under the old epoch), but the child *refresh* token is a durable row with
`RevokedAt == null`, and presenting it makes `RefreshAsync` read the user's
**current** epoch and mint a perfectly valid current-epoch pair. For an email
change that works immediately; for a disable the child lies dormant and works the
moment someone re-enables — the exact resurrection the epoch exists to prevent,
re-entered through the refresh door.

So `RefreshToken` carries `IssuedEpoch`, stamped at mint time, and `RefreshAsync`
refuses any token whose `IssuedEpoch` differs from the user's current
`CredentialEpoch`. A bump therefore kills the whole family by construction,
whether or not the bulk revoke happened to win the race.

That comparison sits **ahead of the #176 grace/replay branch**, not merely ahead
of the rotation — see *Enforcement* for why the difference is the whole finding.

Bulk revocation stays — it is still the right thing to do, and it closes the
window promptly — but it is no longer what the guarantee rests on. That matters
because `RevokeAllActiveForUserAsync` is a bulk `ExecuteUpdate` over rows that
are unrevoked *at that instant*; it cannot see a row that does not exist yet.

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
does not reveal whether a colliding address belongs to another tenant.

> **Superseded in part (#530, 2026-08-25).** The reasoning below was written when
> email uniqueness was global, because login had no account discriminator. That is
> no longer true: login takes a farm code and the Identity indexes are
> account-scoped (`(AccountId, NormalizedUserName)` and `(AccountId,
> NormalizedEmail)`), so one address can legitimately belong to users in several
> farms. The CONCLUSION still holds and is now load-bearing rather than
> incidental — a bare "address already taken" would be a cross-tenant oracle.
> See [`530-multi-farm-tenancy.md`](../../decisions/530-multi-farm-tenancy.md).

Originally: uniqueness was global because login had no account discriminator, and
a bare "address already taken" would be a cross-tenant oracle the existing code is
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

`RefreshToken` gains the matching half:

```csharp
public int IssuedEpoch { get; set; }   // the user's CredentialEpoch when this token was minted
```

Without it the epoch is defeated through the refresh door — see *Revoking
refresh tokens is not enough on its own* above. Every mint site stamps it:
`LoginAsync`, `RefreshAsync`'s rotation, and `ChangeOwnPasswordAsync`'s
re-issue.

One migration: three columns on `AspNetUsers` and one on `refresh_tokens`.

**Epoch `0` is permanently retired.** It is the value a row receives when it is
written by something that does not know the column exists, and it must never
equal a live user's epoch. So:

- `AspNetUsers.CredentialEpoch` — non-nullable, **default `1`**, and the C#
  property initializes to `1`. Existing users are backfilled to `1`.
- `refresh_tokens.IssuedEpoch` — non-nullable, **default `0`**, with no C#
  initializer worth trusting: every mint site stamps it explicitly from the
  user's current epoch. The default exists only to catch writers that don't.

Since epochs only ever increment from `1`, a token carrying `0` can never match
any user and is rejected inert, forever.

That asymmetry is doing two jobs.

**One: it makes the cutover actually bite.** Revoking legacy tokens is not
sufficient on its own. If users and legacy tokens both sat at `0`, then after the
victim logs in again their *new* token is also `0` — so a stolen legacy token
**passes** the epoch comparison, falls into the revoked-token replay branch, and
burns down the fresh family. The denial of service the check placement above
exists to prevent, reintroduced by the cutover itself. Users at `1` and legacy
tokens at `0` is what separates them.

**Two: it survives a rolling deploy.** Production runs `migrate` as a separate
job *before* the new serving process (#263), so an old binary can still be
serving while the columns exist. It knows nothing about `IssuedEpoch`, so its
`INSERT`s take the database default `0` — which is exactly the value no user will
ever carry. Tokens minted in that window are inert the moment the new binary
takes over. Their holders get logged out; nobody gets a credential the new
mechanism cannot see. There is no window in which an old writer can mint
something the new binary honours.

**The migration mutates no rows at all.** An earlier draft had it revoke every
existing refresh token as defence-in-depth. That is exactly the statement that
must not run while a pre-epoch replica is still serving, and the reason is worth
following carefully because it is what makes deploy A genuinely inert:

The pre-epoch `RefreshAsync` treats a revoked token with no live replacement as a
**replay**, and answers it by calling `RevokeAllActiveForUserAsync`. So a legacy
token that the migration marked revoked is, to an old replica, indistinguishable
from a stolen one. The user re-logs in on a new replica and gets a clean epoch-1
family; a forgotten tab then refreshes against an *old* replica with its
cutover-revoked token; the old replay branch fires and burns down the family that
was just issued. The old tab redirects to login tidily, having destroyed the
user's live session on the way out.

That is a user-visible break caused by the deploy that claimed to cause none.

So the migration is **additive only**: four columns with defaults, no `UPDATE`,
no `INSERT`. An old replica sees a database byte-identical in every column it
reads, and behaves exactly as it does today. A new replica gets full enforcement
immediately. There is no state in between.

**Additive is not the same as free.** Constant defaults avoid a heap rewrite on
the Postgres version this targets, but each `ALTER TABLE … ADD COLUMN` still
takes an `ACCESS EXCLUSIVE` lock. If a still-serving old process holds a long
transaction touching `refresh_tokens`, the migrate job waits — and its *queued*
exclusive request blocks every login and refresh behind it. `DatabaseReadyHealthCheck`
does not help: it gates the replacement process, not the old fleet still taking
traffic. A metadata-only migration would become an authentication outage.

So the migration sets a short **`lock_timeout`** and fails fast rather than
queueing, leaving the operator to retry. A failed migrate job is a deploy that
did not start; an unbounded lock wait is a deploy that took auth down. `refresh_tokens`
is the table that grows (#259, #270 exist because of it), so this is the one
most likely to be busy.

Nothing is lost, because **the revocation was never what drew the boundary** —
the epoch separation is. Legacy tokens carry `IssuedEpoch = 0`, users carry `1`,
and the new binary rejects the mismatch inert. Revoking them was belt-and-braces
on top of a mechanism that already held.

The belt-and-braces still has a home, just a later one. Once the pre-A fleet is
drained, a one-off `revoke-legacy-tokens` CLI verb (same run-then-exit shape as
`migrate`) performs the `UPDATE`. At that point no reader can misinterpret it,
and the direction of the defence is the one worth having: if the epoch comparison
ever regresses, revoked-and-retired tokens degrade to a denial of service,
whereas active-and-retired ones would degrade to *access*.

Its predicate is exactly `IssuedEpoch = 0 AND RevokedAt IS NULL`, and it is
**repeat-safe**: a second run updates zero rows, preserving the timestamps the
first one wrote. An implementation that stamps every epoch-`0` row on each
invocation would rewrite history and regenerate table-wide WAL every time an
operator retried — and operators retry one-off verbs, which is why `seed` and
`bootstrap-admin` are both idempotent. Pinned by a test asserting the second run
changes nothing.

`MigrationSecurityReviewTests` is unaffected either way — no INSERTs, and now no
data statements at all.

#### Rollback fails open unless it is done deliberately

"Epoch `0` is inert forever" holds only while a binary that reads `IssuedEpoch`
is running. Roll deploy A back and the pre-A binary ignores the column entirely.

The concrete hazard: during the mixed-fleet window an old replica can insert an
active default-`0` child immediately after a new replica's password reset revoked
the family — the same refresh race described above. The new binary rejects that
child on sight. **A rollback resurrects it**, and the password reset that was
supposed to kill it silently did not.

So rollback is not "redeploy the previous image." The order is: drain every
deploy-A process, **revoke all active refresh tokens**, then start the pre-A
fleet. That is one forced re-login, and it is what makes the rollback fail
closed. It belongs in the runbook, not in an operator's memory — the deployment
repo owns the procedure, this repo owns the requirement.

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

**An absent or unparsable epoch claim is a mismatch, not an exemption.** This is
the one place the middleware can fail open, and the codebase's own conventions
lead straight into it: `must_change_password` is *omitted* from the token when
false, so "this claim isn't here, therefore it doesn't apply" is the idiom a
reader already has in hand. Applied to the epoch it is a hole — every access
token minted before the deploy carries no epoch claim and stays cryptographically
valid for up to ~15 minutes, so treating absence as "not applicable" would let
the entire pre-cutover fleet keep authorizing requests straight through the
boundary the migration just drew.

The rule that makes this uniform rather than a special case: **a missing or
malformed claim parses to `0`**, and `0` is the retired sentinel no user can ever
carry (see *Storage*). So it flows through the ordinary comparison and 401s, with
no separate branch to forget. Same treatment for a claim that is present but not
an integer.

It answers 401 with a distinct `Auth.CredentialsSuperseded` title, and disable
additionally surfaces `Auth.AccountDisabled`, so the SPA can tell "you were
disabled" from "your credentials were rotated."

`LoginAsync` refuses a disabled user with the *same* generic
`Identity.InvalidCredentials` as a wrong password, still paying the PBKDF2 cost —
the reply must never reveal account state, the reasoning the lockout branch
already documents.

`RefreshAsync` refuses a disabled user, **and refuses any token whose
`IssuedEpoch` differs from the user's current `CredentialEpoch`**. That check,
not the bulk revocation, is what makes a bump terminate the whole token family.

**Where the check goes is load-bearing, and "before the rotation" is too late.**
The current flow looks up the token, and *if it is already revoked* runs the #176
grace/replay branch — which, when grace does not apply, calls
`RevokeAllActiveForUserAsync` and returns. That happens **before**
`FindByIdAsync` loads the user, so an epoch check placed anywhere near the
rotation never runs on this path. The consequence is not a bypass but a
denial of service: an attacker holding a superseded pre-bump token waits for the
legitimate user to log in again, presents the dead token, and burns down the
victim's brand-new current-epoch family. Repeatably.

So the order is: resolve the token, **load the user and compare epochs**, and
only then enter the grace/replay branch. An epoch mismatch fails closed *and
inert* — the generic `Identity.InvalidRefreshToken`, with **no** family
revocation, because a superseded token is evidence about a session that is
already dead and says nothing about the live one. Family revocation is preserved
for its real purpose: genuine same-epoch reuse, which is still the theft signal
#176 exists to catch.

**And the revocation itself must be scoped to the epoch that was checked.**
Passing the comparison is not enough, because the comparison is a point-in-time
read and `RevokeAllActiveForUserAsync` is user-wide. A request can read
`IssuedEpoch = E` against `CredentialEpoch = E`, pass — and then, before it
reaches the revocation, a disable/email/password change commits `E+1` and the
user logs in fresh at `E+1`. The stale request then burns a family minted after
the credentials it was checked against were rotated. Same denial of service as
before, now entered by *passing* the check rather than skipping it.

The replay revocation is therefore predicated on `IssuedEpoch = stored.IssuedEpoch`,
not on `UserId` alone. That is also the semantically correct thing independent of
the race: theft detection should burn the compromised family, not one issued
after the compromise was already answered by a rotation.

### 4a. Rolling deploys: the check has to exist everywhere before the mutations do

The retired-`0` sentinel makes rows *written* by an old binary inert. It does
nothing about requests *served* by one.

An old replica runs the pre-epoch pipeline: it validates the bearer's signature,
issuer, audience, and lifetime, and has no `CredentialEpochMiddleware` to
consult. So during a rolling deploy — new replicas up, old ones still serving —
an Owner can disable a user on a new replica while the load balancer keeps
routing that user's pre-deploy access token to an old one, which authorizes it
happily. The suspension is not immediate; it is immediate *on some fraction of
the fleet*, which is worse than a documented delay because nothing surfaces it.

This is not fixable by anything the mutation does. The enforcement gap belongs to
the replica that never learned to enforce.

**So the feature ships in two deploys, and the ordering is a hard constraint:**

The split is **every reader in A, only mutations and UI in B**. That is a
stronger rule than "the mechanism in A", and the difference is load-bearing:

1. **Deploy A — the mechanism and every reader, additive.** `CredentialEpoch`,
   `IssuedEpoch`, an **additive-only** migration, `JwtTokenService` stamping the
   claim, `CredentialEpochMiddleware`, `RefreshAsync`'s epoch comparison — **and
   every disabled-state check**: `LoginAsync`, `RefreshAsync`, and
   `StepUpGrantService.IssueAsync` all refuse a disabled user as of A. The
   `DisabledAt` column ships here too, unused by any mutation.

   Putting the disabled *readers* in B would leave a hole during B's own gradual
   rollout: a disabled user's login or refresh landing on an A-only replica would
   mint a **current-epoch** token, which every B replica then honours — the
   suspension bypassed by a credential minted after it. Readers must precede the
   writers that give them something to read.

   **No row mutations** — an old replica sees a database byte-identical in every
   column it reads and behaves exactly as it does today, while a new replica
   enforces fully. See *Storage* for why the cutover revocation cannot live here.

2. **Drain.** Every pre-A process gone.

3. **Deploy B — the mutations and the UI.** `POST /disable`, `POST /enable`,
   `PUT /email`, and the SPA. By the time an epoch bump can be triggered by a
   *new* operation, or a user can be disabled at all, every serving replica both
   enforces the check and reads the flag.

   **The API must be staged across the whole fleet before the SPA is exposed.**
   B rolls gradually too, so a browser that loads the new SPA from a B replica
   can have `/disable` routed to an A replica that has no such route —
   intermittent 404s on the new administrative actions. Either stage the API
   first and release the SPA last, or drain A→B before clients receive the new
   UI. Same class of problem as the A→B boundary, one deploy later.

   The optional `revoke-legacy-tokens` verb can run from here on.

The drain is the deployment repo's job, not this one's — what belongs here is the
**requirement**: deploy B must not be exposed until no process from before deploy
A is still serving. An orchestrator that reports "rollout complete" on the basis
of new replicas being healthy, without confirming old ones are gone, does not
satisfy it.

**Rollback has its own order** — drain deploy-A processes, revoke all active
refresh tokens, *then* start the pre-A fleet — because a pre-A binary ignores
`IssuedEpoch` and would resurrect any default-`0` token the epoch check had been
suppressing. Also in *Storage*.

A single-replica install has none of this, and the reference compose stack is
single-replica. The design cannot lean on that — #338 already contemplates more
than one — and a constraint that only holds at one replica is the kind that gets
discovered during the first scale-up.

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
- A refresh in flight across a disable must not leave a usable child token —
  asserted **by presenting the child**, not by checking that the access token it
  minted is rejected. The weaker assertion passes with the defect present.
- The same child must still be unusable **after the user is re-enabled**. This is
  the one that fails if `IssuedEpoch` is left off the refresh token.
- A refresh in flight across an email change must not leave a usable child token,
  asserted the same way.
- A step-up issuance racing a disable must not produce a usable grant.
- **A superseded token must not burn down the current family.** Bump the epoch,
  let the user log in again, then present the old pre-bump token: the new session
  keeps working. This is the test that fails if the epoch comparison sits
  anywhere after the grace/replay branch — and it fails as a denial of service,
  not as an access grant, so it will not show up in any "can the attacker get
  in?" test.
- **The same, but racing** — and this one has to be barrier-controlled, because
  the sequential version above passes with the defect present. Present a revoked
  epoch-`E` token, pause the request *after* the epoch comparison succeeds, bump
  the epoch and log the user in fresh at `E+1`, then release. The `E+1` family
  must survive. This is what fails if the replay revocation is scoped to
  `UserId` rather than to the epoch that was actually checked.
- **Genuine same-epoch reuse still burns the family.** The counterpart, so the
  fix above cannot be implemented by simply weakening #176's theft detection.

**Cutover** — asserted against a database seeded pre-migration, since the whole
point is a boundary the migration draws across data it did not create:

- A refresh token active before the migration is rejected by the new binary
  after it — on the epoch mismatch alone, with the row still `RevokedAt IS NULL`.
- **After that user logs in again, the stolen legacy token still cannot burn
  their new family.** The test that fails if legacy tokens and users share an
  epoch.
- **The migration mutates no rows.** Asserted directly: every
  `refresh_tokens.RevokedAt` is unchanged across the migration. This is the test
  that fails if someone "helpfully" reintroduces the cutover revocation into the
  migration, which would break old replicas mid-rollout.
- **Rollback fails closed.** Run the migration, mint a default-`0` child, then
  simulate the pre-A binary by ignoring `IssuedEpoch` — the child must be
  unusable, which is only true if the rollback procedure revoked it first.
- A row inserted with the column default `0` — standing in for an old binary
  still serving during a rolling deploy — is rejected by the new binary, and
  rejected *inert*.
- **An access token minted before the cutover is refused after it.** Every
  pre-deploy access token carries no epoch claim and stays cryptographically
  valid for its remaining lifetime, so this is the case that catches a middleware
  which treats an absent claim as "not applicable." Assert a *pre-migration*
  token making an ordinary authenticated request post-cutover and getting 401.
- A token whose epoch claim is present but malformed (non-integer, empty) is
  refused the same way. Pinned separately, because "absent" and "unparsable"
  are two different code paths and only one of them is obvious.

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

Three PRs, not two, and the first two are **separate deploys** — see *Rolling
deploys* above for why the ordering is a correctness constraint rather than a
preference.

1. **The credential epoch and every reader** (#364). `CredentialEpoch`,
   `IssuedEpoch`, **`DisabledAt` / `DisabledBy`**, the additive migration, the
   claim, the middleware, `RefreshAsync`'s epoch comparison and epoch-scoped
   replay revocation, and the SPA plumbing that carries a 401's reason through
   auth teardown.

   **Including all three disabled-state readers** — `LoginAsync`, `RefreshAsync`,
   and `StepUpGrantService.IssueAsync` refuse a disabled user as of this deploy,
   with the column written by nothing yet. This is not optional detail: if the
   readers slip to (2), a replica part-way through (2)'s rollout mints a
   current-epoch credential for a user another replica just disabled, and every
   updated replica honours it. See *Rolling deploys*.

   Makes no new promise: the only epoch bumps are the password-reset paths that
   already revoke refresh tokens today, and nothing can set `DisabledAt` yet.
2. **#356 — disable / enable a user.** The mutations, the account lock, the
   guards, the `recover-admin` changes, the SPA toggle and confirm. Must not be
   exposed until every pre-(1) process is drained.
3. **#357 — change a user's email.** Reuses all of it; adds the four-column
   atomic write and the sole-Owner guard. Smaller than the first draft implied —
   the retry work it described does not exist.

The split is not bookkeeping. (1) is where every one of the migration-boundary
corrections lives, and it is the part that has to be correct on a mixed fleet;
(2) and (3) are ordinary feature work on top of a mechanism that is already
everywhere.

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

## What the second draft got wrong

10. **The epoch was put on the user only.** That rejects the *access* token a
    racing refresh mints, and stops there — the *child refresh token* is a
    durable row the bulk revoke never saw, and presenting it makes
    `RefreshAsync` read the user's current epoch and mint a valid current-epoch
    pair. Immediately after an email change; after re-enabling, for a disable.
    The fix is `RefreshToken.IssuedEpoch` — though the third draft then placed
    that comparison too late in turn; see item 12.

    The general lesson, and the reason this survived a rewrite that was
    *specifically about* this failure mode: a revocation epoch has to be bound to
    **every credential the system will later accept**, not just the one whose
    weakness prompted it. The second draft found the access token had no binding
    and fixed exactly that, while the refresh token — the credential whose entire
    job is to outlive the access token — kept none. Any future credential type
    (a step-up grant made durable per #338, an API key, a device token) must be
    stamped at mint and checked at use, or it reopens the same door.

## What the third draft got wrong

11. **The cutover was designed to preserve live sessions**, and that was stated
    as a virtue ("evicts no live session"). It is the defect. The pre-epoch code
    already has the revoke-vs-refresh race, so a legacy token sitting
    `RevokedAt == null` may already be an attacker's orphan; backfilling every
    token and user to `0` certifies it as current. The migration cannot tell
    which legacy tokens predate their user's last credential change, so the only
    clean boundary is to revoke all of them and accept one forced re-login.

    *Superseded in part by items 13 and 16: the boundary is drawn by the epoch
    separation, not by the revocation, and the revocation cannot run in the
    migration at all. The diagnosis here was right; the remedy was not.*

12. **The epoch comparison was placed "before the rotation," which is too late.**
    A revoked token reaches #176's grace/replay branch — and
    `RevokeAllActiveForUserAsync` — before `FindByIdAsync` loads the user, so the
    check never runs on that path. An attacker holding a superseded token can
    therefore burn down the victim's new current-epoch family after they log back
    in. The comparison moves ahead of the grace branch, and a mismatch fails
    inert rather than revoking anything.

    Both of these share a shape worth naming, because it is the third time it has
    appeared in this document: **a check is only as good as the earliest path
    that can reach the thing it protects.** Draft two bound the epoch to the
    access token and missed the refresh token; draft three bound it to the
    refresh token and missed the branch that acts on a refresh token *without
    ever loading the user*. When adding a guard, enumerate every entry point to
    the guarded state first, then place the guard above all of them — do not
    place it next to the code that prompted the guard.

## What the fourth draft got wrong

13. **The cutover revoked legacy tokens but left them at the same epoch as their
    users.** Revocation is not differentiation. After the victim logs in again
    their new token is also epoch `0`, so a stolen legacy token *passes* the
    comparison, enters the replay branch, and burns the fresh family — the same
    denial of service, reintroduced by the fix for the previous one.

    Users now start at `1` and `0` is permanently retired. That also closes a
    rolling-deploy window the draft never considered: `migrate` runs before the
    new serving process (#263), so an old binary can still be inserting refresh
    tokens whose unknown column takes the database default — and `0` is precisely
    the value no user will ever carry, so those rows are inert by construction
    rather than by timing.

    Worth stating as its own rule, because it is a different mistake from items
    10–12: **a boundary needs a value, not just an event.** "Revoke everything at
    the cutover" is an event, and an event does not survive a writer that is
    still running or a row written after it. Reserving a sentinel the new world
    can never produce is what makes the boundary hold without depending on when
    anything happened.

## What the fifth draft got wrong

14. **The cutover covered refresh tokens and forgot access tokens.** Every access
    token minted before the deploy carries no epoch claim and stays
    cryptographically valid for its remaining ~15 minutes. If the middleware
    reads an absent claim as "not applicable," the whole pre-cutover fleet
    authorizes straight through the boundary the migration just drew.

    Absent or unparsable now parses to `0` — the retired sentinel — so it fails
    through the ordinary comparison with no special branch.

    The reason this was easy to miss is worth recording: the codebase's own
    convention pulls the other way. `must_change_password` is omitted from the
    token when false, so "claim absent ⇒ doesn't apply" is an idiom already in
    the reader's hand, and it is correct *there*. **A convention that is right
    for a feature flag is a vulnerability for a revocation check.** An omitted
    optional flag means "no", but an omitted mandatory one has to mean "refuse" —
    and nothing in the shape of the code distinguishes them.

    This is also the fourth consecutive round where the defect lived in the gap
    between "the new mechanism works" and "the old world is actually gone."
    Items 11, 13, and 14 are all migration-boundary failures, in three different
    credential types. A design that introduces a per-request check has to
    enumerate **every credential already in flight** on the day it ships, not
    just the ones it mints afterwards.

## What the sixth draft got wrong

15. **The rolling-deploy analysis covered rows written by an old binary and not
    requests served by one.** The retired `0` makes an old writer's tokens inert;
    it does nothing about an old *replica*, which runs the pre-epoch pipeline and
    has no middleware to consult. Disable a user on a new replica, and the load
    balancer can still route their pre-deploy access token to an old one that
    authorizes it. The suspension is immediate on part of the fleet — worse than
    a documented delay, because nothing surfaces it.

    The mechanism now ships as its own inert deploy first, and the mutations only
    after the old fleet is drained.

    The general form, and the one to carry into any future work of this kind:
    **adding a per-request check is a fleet-wide change, not a code change.**
    Until the last old process is gone, the guarantee is whatever the *weakest*
    replica enforces. Any feature whose promise depends on a new gate has to ship
    behind that gate being universal — which means the gate and the promise
    cannot be in the same deploy.

    Note the asymmetry with item 13. That one was solved by reserving a value the
    old world could not produce. This one cannot be, because the old world is not
    producing anything — it is *failing to check*. A sentinel defends against bad
    data; only ordering defends against absent enforcement.

## What the seventh draft got wrong

16. **Deploy A was called inert while its migration revoked every refresh
    token.** Those are contradictory. The pre-epoch `RefreshAsync` reads a
    revoked token with no live replacement as a *replay* and answers it by
    revoking the whole family — so a legacy row the migration marked revoked is,
    to an old replica, indistinguishable from a stolen one. User re-logs in on a
    new replica, a forgotten tab refreshes against an old one, and the old replay
    branch burns down the epoch-1 family that was just issued. The deploy that
    promised to break nothing breaks the session of anyone who left a tab open.

    The migration is now **additive only**. Nothing is lost, because the
    revocation was never what drew the boundary — the epoch separation is, and
    legacy tokens are already rejected inert on the mismatch. The
    defence-in-depth moves to a `revoke-legacy-tokens` verb run after the drain,
    when no reader can misinterpret it.

    The lesson is narrower than "drain first" and more useful: **a compatibility
    window is a constraint on writes, not only on reads.** It is not enough that
    the old binary tolerate the new *schema*; it must also tolerate every *value*
    the new deploy puts into it. Adding a column is safe. Changing a row an old
    code path already has opinions about is not, however harmless the change
    looks in the new code.

17. **"Epoch `0` is inert forever" quietly assumed the new binary is still
    running.** A rollback to the pre-A image drops the `IssuedEpoch` check
    entirely, resurrecting any default-`0` token — including a child an old
    replica inserted right after a new replica's password reset revoked the
    family. The reset that was supposed to kill it silently did not.

    Rollback therefore has its own ordered procedure: drain deploy-A processes,
    revoke all active refresh tokens, then start the pre-A fleet.

    Generally: **a security property that holds only while the new code is
    deployed is not a property, it is a configuration.** Every claim of the form
    "X can never happen" needs the rollback path checked before it is written
    down, because rollback is the one deploy nobody rehearses and everybody
    eventually performs.

## What the eighth draft got wrong

18. **The disabled-state readers were left in deploy B.** During B's own gradual
    rollout a disabled user's login or refresh can land on an A-only replica,
    which mints a **current-epoch** token that every B replica then honours — the
    suspension bypassed by a credential issued after it. The split is therefore
    *every reader in A, only mutations and UI in B*: `LoginAsync`,
    `RefreshAsync`, and `StepUpGrantService.IssueAsync` all refuse a disabled
    user as of A, with `DisabledAt` shipping unused.

    Stated generally, and this is the last variant of the rule the previous
    rounds kept circling: **readers ship before writers, always.** A reader
    deployed early is inert; a writer deployed early is a hole. The same logic
    applies one deploy later to B itself — the API must be staged fleet-wide
    before the SPA is exposed, or a browser served the new UI by a B replica gets
    404s from an A replica that has no such route.

19. **Operational hygiene the design waved at rather than specified:** the
    additive migration still takes an `ACCESS EXCLUSIVE` lock and needs a
    `lock_timeout`, so a busy `refresh_tokens` cannot turn a metadata-only change
    into an authentication outage; and `revoke-legacy-tokens` needs a pinned
    predicate (`IssuedEpoch = 0 AND RevokedAt IS NULL`) plus repeat-safety,
    because operators retry one-off verbs.

## What the ninth draft got wrong

20. **The replay revocation was scoped to the user, not to the epoch that was
    checked.** Placing the epoch comparison ahead of the grace branch (item 12)
    fixed the case where the check never ran. It did not fix the case where the
    check *runs and passes*, and the world moves underneath it: read
    `IssuedEpoch = E` against `CredentialEpoch = E`, pass, and before the request
    reaches `RevokeAllActiveForUserAsync` a bump commits `E+1` and a fresh login
    mints an `E+1` family — which the stale request then burns. The same denial
    of service, entered by passing the check rather than skipping it.

    Revocation is now predicated on `IssuedEpoch = stored.IssuedEpoch`, which is
    also the semantically right answer regardless of the race: theft detection
    should burn the compromised family, not one issued after the compromise was
    already answered.

    The rule: **a check and the action it authorizes must be scoped to the same
    thing.** Moving a guard earlier makes it *run*; it does not make what follows
    obey it. If the guard reads one row and the action writes a set, the set has
    to be narrowed to what the guard actually vouched for.

21. **The Delivery checklist contradicted the rollout requirement.** Item 18
    moved the disabled-state readers into deploy A in the *Rolling deploys*
    section, but the canonical delivery breakdown still listed only the epoch
    fields, middleware, and refresh comparison — so an implementer scoping work
    from the checklist would rebuild exactly the hole item 18 closed. A document
    that states a constraint in one section and contradicts it in the section
    people actually work from has not fixed anything.

## Where this stops being about the design

Rounds 1–4 found defects in the mechanism: it did not do what it claimed.
Rounds 5–8 found defects in the **deploy boundary**: the mechanism was right, but
the old world had not gone away. Round 9 found one more of each — a real race in
the mechanism (item 20) and an internal contradiction (item 21) — after this
document had already been declared frozen once. That call was premature, and the
correction is recorded rather than quietly amended.

What has genuinely stabilized is the *shape*: the epoch, its binding to both
credential types, the account-row lock, the four-column email write, and the
guards have survived untouched since round 3. What keeps producing findings is
the seam between the mechanism and everything around it — old replicas, later
requests, other sections of this document.

What remains open beyond that is **deployment procedure** — drains, staging
order, lock timeouts, rollback steps. Per this repo's host-agnostic boundary that
belongs in the deployment/ops repo as procedure, not here as prose. This spec
states the requirements; it should not grow into a runbook.
