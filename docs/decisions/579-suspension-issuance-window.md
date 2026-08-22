# Suspension is immediate for use, not for issuance — the check-then-mint window stays open (#579)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break).

**Status:** accepted (won't-fix, 2026-08-21, owner decision on #579)
**Date:** 2026-08-21

## What happened

There was no defect to fix — this is a deliberate declination, recorded so the
next reader does not re-derive it. It is an accepted-risk decision, not an
earned rule: the `AGENTS.md` bullet carries the same link shape as the
incident-earned rules because the decision record is its rationale home, but the
rule was not earned by a shipped defect.

Login does two things in sequence, in separate steps: `AuthEndpoints` checks
`Account.IsActive` on a plain read, then `IdentityProvider.LoginAsync` mints the
token pair. A `suspend-account` that commits between them leaves login returning
**200 with a freshly minted refresh-token row that post-dates the suspension's
revocation sweep**. `AccountSuspensionService`'s header states this plainly.

The gap was first weighed in #532, which declined the fix on cost; #579 was
filed (split from #534) so the gap would be tracked rather than dropped when
#534 closed, and re-decided here.

## The rule

**Suspension is immediate for *use*, not for *issuance*.** Do not add a
`FOR SHARE` lock on the account row inside login's issuance transaction to
close the window, and do not read #530's finish line — "immediate, race-safe
suspension" — as covering issuance. *Race-safe* means use: a suspended farm's
existing credentials die on their next presentation.

**The inertness of a window-minted credential rests on exactly four premises,
each pinned by name, each enforced by a test that fails when that premise alone
is broken — not just when the whole service is broken. A change that breaks any
one of them must reopen #579 rather than ship:**

1. **`CredentialEpochMiddleware` re-reads `Account.IsActive` live on every
   authenticated request** (it already joins user and account in the per-request
   read that #364 made fail-closed — the round trip *is* the guarantee, so it is
   never cached). A window-minted access token is therefore refused on its very
   next request, with the epoch already bumped.
2. **`RefreshAsync`'s own suspended-farm check** (`IdentityProvider.cs`, the
   `#532 — a suspended farm cannot rotate a session` block) refuses the
   window-minted refresh token on its first rotation attempt.
3. **`AccountSuspensionService` revokes every user's `CredentialEpoch` and
   `SecurityStamp` in the same transaction as `IsActive`** (one
   `AmbientTransaction`, one `SaveChanges`), for every credential that exists
   **when the sweep executes**. Note the scope: a refresh row minted *after*
   the sweep — precisely the window artifact this record exists for — is not
   destroyed by this premise; it survives the suspension transaction and is
   inert for the other three reasons (the live account checks) until
   reactivation's sweep destroys it for good (premise 4). Enforced by
   `SuspendAsync_SuspendedPremiseIsAtomic_RollbacksWithTheEpochBump`, whose
   static half bounds each revocation's `ExecuteUpdateAsync` call — both the
   user epoch/stamp sweep and the refresh-token sweep — to sit between the
   suspension transaction's begin and commit, and whose runtime half faults
   the audit write (the last in-transaction write) and asserts the account
   reverts to active **and** every epoch/stamp reverts and the token stays
   live. The static check is the load-bearing half for the split-transaction
   regression: a split that still commits in one Postgres transaction (an
   extra `SaveChanges`, a deferred `ExecuteUpdateAsync` awaited after the
   commit) is invisible to the runtime fault, which rolls back whatever
   transaction it is in — verified by mutation on this slice. The variants
   that leave `IsActive` false with live credentials (the sweep moved to a
   second transaction, or the two revocations split across transactions) are
   the ones the static check reddens on.
4. **Reactivation revokes again** — `ReactivationRevokesTheSessionsMintedBetweenSuspendAndReactivate`
   (`tests/Cluckwork.Api.IntegrationTests/AccountSuspensionTests.cs`) inserts
   the window artifact on purpose and proves it is destroyed for good at
   reactivation. It fabricates the row because the race is not reproducible on
   demand; a future interleaving test must drive the real window, not reuse the
   fabricated row.

The observable symptom is a user who appears to sign in and is bounced on their
first action — **not access to a suspended farm**.

## Why not the obvious alternative

Closing the window requires login to take a `FOR SHARE` lock on the account row
**before** minting, inside its issuance transaction — matching the account-first
ordering `AccountSuspensionService` and `AdminRecoveryService` already use.

It is a **cost question, not a deadlock question**. Every *suspension-side*
locking path takes the account row first (`AccountSuspensionService`,
`AdminRecoveryService`), so a login lock taken before the mint would serialise
against them, never deadlock. (Not every locking path in the repo is
account-first — the currency-lock protocol, e.g. `UpdateInventoryItemHandler`,
takes the item row first and the account `FOR SHARE` second, on its own
cycle-freedom argument — but suspension and that protocol never take locks
concurrently with each other, so the two orderings never meet.) The cost is a
lock plus an extra round trip on the login hot path, paid by every farm on every login, in order to
prevent a credential that is inert by the four premises above. #532 weighed
that and declined on cost, not on difficulty; #579 re-weighed it at Phase 1.6
scale (a handful of farms) and declined again.

If suspension ever gains user-facing frequency — many farms, frequent
operator-driven suspensions — the cost argument weakens and this decision
should be revisited. That revisit is a cost decision for the owner, with this
file as the starting point.

## What this does NOT cover

- It does not weaken *use*-side immediacy. The four premises are load-bearing:
  a refactor that moves the login account read into the issuance transaction,
  caches `CredentialEpochMiddleware`'s read, relaxes `RefreshAsync`'s suspended
  check, or splits suspension's three (now four) statements out of one
  transaction re-opens the window's consequences and must re-decide this.
- It does not bless a *wider* window. If a future change makes the minted
  credential usable (breaking any premise), the lock is no longer "prevent a
  credential that can never be used" — it is required, and this record no
  longer applies.
- It says nothing about `provision-account` / `bootstrap-admin` issuance paths,
  which run at boot time, not on the login hot path.

## How it is enforced

Nothing enforces the *declination* — it is a decision, and it relies on review:
the four premises are the tripwires, and each is pinned by the named test or
named code path above, each chosen to fail when its premise alone is broken:

| Premise | Guard | Fails when the premise alone breaks |
|---|---|---|
| 1 — live middleware read | `ABearerWhoseEpochStillMatches_IsRejected_WhenTheFarmIsInactive` (plus the #364 no-cache tests) | middleware stops reading `IsActive` (or caches it) while login still checks — the epoch still matches, so only the middleware's `AccountIsActive` clause can reject |
| 2 — `RefreshAsync`'s suspended check | `ARefreshTokenWhoseEpochStillMatches_IsRejected_WhenTheFarmIsInactive` | that block is deleted while the middleware stays live — the epoch still matches, so only the suspended-farm check can refuse the rotation |
| 3 — suspension's same-transaction revocation | `SuspendAsync_SuspendedPremiseIsAtomic_RollbacksWithTheEpochBump` | either sweep leaves the suspension transaction (a split `SaveChanges`, a deferred `ExecuteUpdateAsync` after the commit, or the two revocations in separate transactions) commits `IsActive` without the revocation |
| 4 — reactivation's revoke | `ReactivationRevokesTheSessionsMintedBetweenSuspendAndReactivate` | reactivation stops revoking, while a suspend→reactivate cycle still destroys the artifact |

The window *itself* is unguarded by design (that is the declination) — none of
these four tests can reproduce the race on demand; premise 4's test fabricates
the window artifact on purpose because it cannot drive the real window.
