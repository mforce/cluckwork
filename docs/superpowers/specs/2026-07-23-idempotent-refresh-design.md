# #176 — Idempotent refresh (grace-window mint-on-retry)

## Problem

#169 serialised refresh across browser tabs with the Web Locks API, closing the
common multi-tab race. One residual a page-owned lock cannot close remains: if a
tab dies in the sub-second between **sending** a refresh and **receiving** the
rotated cookie, the lock auto-releases while the cookie still holds the *old*
token. The next tab presents that already-rotated token, and today's
reuse-detection reads it as a replay → revokes the whole token family → both
tabs are logged out.

## Goal & the unavoidable trade-off

Make refresh **idempotent** for the immediately-previous rotation so a benign
dead-tab / concurrent retry succeeds instead of nuking the session — while
keeping theft-detection strict for every realistic replay.

Reuse-detection works by catching *divergence* (someone presents a token derived
from an already-rotated ancestor). Any server grace window that lets the racing
tab succeed must accept a just-revoked token, so **within that window a stolen
token is also accepted**. This is inherent and unavoidable; the mitigation is to
keep the window tiny (default 10s, vs the ~15-min refresh cadence) and to fail
strict everywhere else. Chosen deliberately (design decision, 2026-07-23).

## Design — mint-on-retry grace

In `IdentityProvider.RefreshAsync`, replace the "revoked token → immediately
revoke family" branch with:

```
present token T; stored = lookup(hash(T))
if stored is null            -> invalid            (unchanged)
if stored.RevokedAt is not null:
    # #176 grace: a token rotated within GraceWindow whose replacement is still
    # the live tip is a benign retry (the #169 residual), not a replay.
    if stored.ReplacedByTokenHash is not null
       and (now - stored.RevokedAt) <= GraceWindow
       and replacement = lookup(stored.ReplacedByTokenHash)
       and replacement.IsActive(now):
          stored = replacement        # advance the LIVE token; fall through to normal rotation
    else:
          RevokeAllActiveForUserAsync(...)          # genuine theft (unchanged)
          return invalid
if stored.ExpiresAt <= now   -> expired            (unchanged)
rotate stored -> new pair    (unchanged)
```

- For the actual tab-death case the replacement was never delivered to anyone,
  so advancing it does **not** fork the chain — there is one live token after.
- The grace path re-enters the *normal* rotation of the still-active replacement,
  so single-use + expiry semantics are preserved unchanged.

## Config

Add `JwtOptions.RefreshReuseGraceSeconds` (default **10**). Bound by config so a
deployment can tune or disable it; tests set it to `0` to exercise the strict
path deterministically.

## Theft-detection remains strict (all deterministic, no time-mocking)

- **Replacement already consumed:** rotate twice (`R0→R1→R2`), then replay `R0`.
  `R0`'s replacement `R1` is now revoked (not the live tip) → grace fails →
  revoke family. Catches an attacker whose stolen token's chain has moved on.
- **Grace disabled (factory `RefreshReuseGraceSeconds=0`):** immediate replay →
  `now - RevokedAt > 0` → expired → revoke family. Proves the grace gate is
  load-bearing.
- **Benign retry (default grace):** rotate `R0→R1`, immediately replay `R0` →
  within grace, `R1` active → succeeds with a fresh token; family NOT revoked.

## Test changes

- Update `Refresh_WithRotatedToken_IsRejected` and
  `Refresh_ReuseDetection_RevokesTheWholeFamily` (both assert *immediate* replay
  → 401, which is now the benign grace path) to the consumed-replacement /
  grace-0 theft scenarios.
- Add the benign-retry success test and a grace-0 factory theft test.

## Docs

- GLOSSARY "Session tokens": note the short idempotency grace on reuse-detection
  that closes the #169 residual.
- `web/README` auth section: the residual note now points here.

## Hardening (post 4-way review)

The 4-way review (codex + pi + 2 agents) + a reproduction test found the naive
grace above has two HIGH holes; both are now closed (user chose "full hardening"):

1. **Leap-frog / chain extension.** The grace advanced the replacement by minting
   a new token, which revoked the replacement — and that revoked link was itself
   grace-eligible, so a stolen token could be stepped down the chain (`T0→grace→T2`,
   then `T1→grace→T3`, …) to extend a session indefinitely within the window.
   Fix: `RefreshToken.RevokedByGrace` marks a link revoked BY a grace-advance;
   `TryGraceReplacementAsync` refuses to grace such a link, bounding the grace to
   a SINGLE hop off a normal rotation. A second-hop attempt hits the strict
   theft path → family revoked.

2. **Concurrent fork + backstop suppression.** `RefreshToken` had no concurrency
   control, so two parallel presentations of the same token both read it active
   and each minted a live child — a fork that, on the grace path, spawned
   multiple sessions AND skipped the family-revoke backstop entirely. Fix: a
   regenerated `ConcurrencyStamp` optimistic-concurrency token makes consuming a
   token an atomic compare-and-swap; the losing writer's `SaveChanges` throws
   `DbUpdateConcurrencyException` and `RefreshAsync` fails it closed. The two
   revoke paths (`RevokeRefreshTokenAsync`, `RevokeAllActiveForUserAsync`) became
   bulk `ExecuteUpdateAsync` so they don't fight the stamp and are safe to call
   from the rotation fail path. Invariant: two concurrent presentations of one
   token never leave more than one live tip.

Also: a clock-skew guard, so a `RevokedAt` stamped ahead of the reader can't
widen the window.

Corrected in #468 — as first shipped, this guard did not bound the window, it
burned the family. `elapsed` was measured from an instant captured BEFORE the
request read the token, so ordinary concurrency produced a negative elapsed (the
winner reads its clock after the loser, stamps `RevokedAt`, and commits first)
and the guard answered by revoking every session the user had. The window is now
measured from the read, which makes a negative elapsed impossible to reach by
ordering alone; a stamp still ahead of that read means the clocks disagree, and
that fails inert — a 401, no family revocation — rather than being read as
theft. The window itself is unchanged at `RefreshReuseGraceSeconds`.

## Out of scope

- The client Web Lock (#169) stays as the first line of defence; this grace is
  the server-side safety net for the tab-death residual only.
