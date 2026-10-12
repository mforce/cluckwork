using Cluckwork.Application.Modules.Access.Contracts;
using System.Runtime.ExceptionServices;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

public sealed partial class IdentityProvider
{
    public async Task<Result<TokenPair>> RefreshAsync(string refreshToken, CancellationToken ct = default, Guid? expectedAccountId = null)
    {
        var presentedHash = Hash(refreshToken);

        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == presentedHash, ct);
        if (stored is null)
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidRefreshToken", "Refresh token is invalid."));

        // #547/#532 — the tab told us which farm it expects; the stored token
        // says which farm it actually belongs to. Per-farm names prevent this
        // mismatch in normal traffic, but retain the check as defence-in-depth
        // against a malformed or misplaced cookie.
        //
        // Refuse BEFORE rotating, and leave the stored token untouched and
        // usable: the tab that asked is the one that must recover, and it must
        // not damage the session that legitimately owns the cookie. Rotating or
        // revoking here would sign out the farm that did nothing wrong.
        //
        // A DISTINCT code, unlike every other refresh rejection: this one is
        // recoverable by re-bootstrapping the tab, not by signing in again, and
        // the SPA needs to tell those apart.
        if (expectedAccountId is not null && stored.AccountId != expectedAccountId)
            return Result.Failure<TokenPair>(Error.Validation(
                "Auth.SessionChanged", "This session now belongs to a different farm."));

        // #468 — the clock is read AFTER the lookup, and this ordering is load-
        // bearing: the #176 grace window below measures how long ago the row we
        // just READ was revoked. Reading the clock first makes that elapsed
        // NEGATIVE under ordinary concurrency — a competing request can read its
        // own clock later than ours, stamp RevokedAt with it, and commit before
        // our lookup runs — which the skew guard in InspectGraceReplacementAsync
        // then read as a replay and answered by revoking the whole family. Two
        // tabs refreshing at once signed the user out of every device. Reading
        // here instead makes the ordering an invariant rather than a race: any
        // revocation we can observe was committed before this line, so its stamp
        // precedes this instant on a single clock. The rotation below stamps the
        // same instant (RevokedAt, ExpiresAt), so the row we write stays
        // consistent with the one we read.
        var now = timeProvider.GetUtcNow();

        // This MUST precede the #176 grace/replay branch. A retired token is
        // not evidence about the current family, so it fails inert rather than
        // letting replay detection revoke a later epoch's credentials.
        var user = await userManager.FindByIdAsync(stored.UserId.ToString());
        // #1031 — the floor stops a retired-zero or negative row matching a stored
        // epoch below 1.
        if (user is null || user.DisabledAt is not null || user.CredentialEpoch < 1
            || stored.IssuedEpoch != user.CredentialEpoch)
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidRefreshToken", "Refresh token is invalid."));

        // #532 — a suspended farm cannot rotate a session. Deliberately the SAME
        // generic error every other refresh rejection uses, not Auth.FarmSuspended:
        // refresh carries no user-facing copy of its own (the SPA treats any 401
        // here as a dead session and stops), and the caller learns the real reason
        // from CredentialEpochMiddleware on their next ordinary request, which
        // does say Auth.FarmSuspended. Suspension also bumps every user's
        // CredentialEpoch, so the line above already rejects most tokens; this
        // check is what closes the window for one minted in the same instant.
        var accountIsActive = await db.Accounts.IgnoreQueryFilters()
            .Where(account => account.Id == stored.AccountId)
            .Select(account => (bool?)account.IsActive)
            .FirstOrDefaultAsync(ct);
        if (accountIsActive != true)
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidRefreshToken", "Refresh token is invalid."));

        // Presenting an already-rotated/revoked token normally means it was replayed —
        // treat as a possible theft and revoke every active token for the user.
        var viaGrace = false;
        if (stored.RevokedAt is not null)
        {
            // #176 — idempotency grace: a token rotated within the last
            // RefreshReuseGraceSeconds whose replacement is still the live tip is a
            // benign concurrent/dead-tab retry (the #169 residual), not a replay.
            // Advance the still-active replacement (fall through to the normal
            // rotation below) and hand the caller a fresh token instead of revoking
            // the family. For the actual tab-death case the replacement was never
            // delivered, so this does not fork the chain. Anything else — a stale
            // token, an expired grace, or a replacement already gone — is a genuine
            // replay and still burns the family down.
            var (graced, failInert) =
                await InspectGraceReplacementAsync(stored, now, ct);
            if (failInert)
            {
                // Either a mixed-version replica linked this current-epoch token
                // to a child whose default epoch is permanently retired (#364),
                // or the revocation is stamped ahead of this request's clock
                // (#468). Neither is evidence of theft in the current family:
                // fail inertly, or repeated presentation of the parent could
                // keep revoking unrelated sessions — ones minted after the new
                // version took over, or ones a disagreeing clock made look
                // impossible.
                return Result.Failure<TokenPair>(Error.Validation(
                    "Identity.InvalidRefreshToken", "Refresh token is invalid."));
            }
            if (graced is null)
            {
                // #273 — a genuine replay: log BEFORE attempting the revoke, so
                // detection is recorded even if the revoke itself throws (the
                // catch below logs that separately as Auth.RefreshRevocationFailed
                // and rethrows — this call still completes and returns normally).
                logger.LogWarning("{SecurityEvent} user={UserId} client={ClientIp}",
                    SecurityEvents.RefreshTokenReplayDetected, stored.UserId, ClientIp);
                // Merge note: #364 (on main after this branch was cut) scopes the
                // revoke to the epoch that ISSUED the presented token. Keep that
                // argument — the 2-arg overload still exists and still compiles,
                // so dropping it is silent, and it would widen a replay
                // revocation to credentials minted by a LATER epoch: a stale
                // token could keep killing sessions created by a password reset
                // that already superseded it.
                await RevokeAllActiveForUserAsync(stored.UserId, stored.IssuedEpoch, now, ct);
                return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidRefreshToken", "Refresh token is invalid."));
            }
            stored = graced;
            viaGrace = true;
        }

        if (stored.ExpiresAt <= now)
            return Result.Failure<TokenPair>(Error.Validation("Identity.ExpiredRefreshToken", "Refresh token has expired."));

        // Rotate: revoke the presented token and issue a fresh one. Marking the
        // revocation as grace-sourced (#176) bounds the grace to a single hop: the
        // token minted here can be rotated normally, but this just-revoked link
        // can never itself be grace-advanced, so a stolen token can't be
        // leap-frogged down the chain.
        var (rawToken, newHash) = GenerateRefreshToken();
        stored.RevokedAt = now;
        stored.ReplacedByTokenHash = newHash;
        stored.RevokedByGrace = viaGrace;
        stored.ConcurrencyStamp = Guid.NewGuid().ToString(); // rotate the CAS token (#176)
        var minted = NewToken(user, newHash);
        db.RefreshTokens.Add(minted);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // #269 review (#350, codex round 5) — the rotation is a compare-and-
            // swap on a SINGLE-USE record: it rewrites ConcurrencyStamp so a
            // competing consumer of the same token matches no row. Refresh is
            // anonymous, so IdempotencyMiddleware's tenant gate skips it and this
            // save is a self-contained unit the execution strategy REPLAYS.
            //
            // On the ambiguous commit — Postgres committed the rotation and only
            // the acknowledgment was lost — the replay re-issues the batch against
            // state its own first attempt already moved: the UPDATE carries the
            // superseded stamp, and the INSERT carries a TokenHash now in the
            // unique index. Whichever of the two the server rejects first, the
            // failure lands here describing a request that in fact SUCCEEDED, and
            // it is NOT recoverable by trying again: the rotation is durable, so
            // the caller's cookie holds a REVOKED token, and the child that
            // replaced it is live but was delivered to nobody. Measured pre-fix:
            // 409 "Data conflict" (the duplicate INSERT surfaces first, so the
            // #176 branch below was never even reached), one live orphan token,
            // no Set-Cookie. The user is signed out by the very resilience
            // feature meant to absorb the blip.
            //
            // WHY THE PROBE RATHER THAN SingleAttemptExecution. The branch's rule
            // is that a unit is unreplayable when the REPLAY IS OBSERVABLE. Here
            // it is not: EF wraps the two statements in one transaction, so a
            // replay after the commit lands writes nothing at all and a replay
            // after a rolled-back attempt simply succeeds. The defect is not the
            // replay — it is this catch MISREADING what the replay's failure
            // means. So fix the reading and keep the retry: refusing to replay was
            // measured to turn the ordinary fail-BEFORE blip (which EF absorbs
            // today, 200 OK) into a 409, i.e. it would trade one signed-out user
            // for another. Contrast AccountLockout, where the replay really is
            // observable — a second durable increment — and single-attempt is the
            // only answer.
            //
            // Order matters: ask the DATABASE first, classify second. Which
            // exception EF raises depends on statement ordering inside the batch,
            // so a branch keyed on the exception type would be reading a coin
            // flip; whether our own token is durable is a fact.
            //
            // The probe is itself a DB read (#269 codex review, #350 round 5's
            // successor): under a SUSTAINED outage — not just the single blip the
            // whole retry-then-probe dance is built to absorb — it can throw too,
            // and a throw from inside this catch replaces it, skipping every
            // branch below including the alert. Log the ROTATION's failure (ex),
            // never the probe's — the probe throwing only corroborates the same
            // outage — and preserve the fail-closed contract via
            // ExceptionDispatchInfo so the original exception (type, message,
            // stack) still reaches the caller unchanged (codex review of #349).
            bool durable;
            try
            {
                durable = await MintedTokenIsDurableAsync(newHash);
            }
            catch (Exception) when (ex is not OperationCanceledException)
            {
                LogRevocationFailed(ex, user.Id);
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw; // unreachable; satisfies flow analysis
            }

            if (durable)
            {
                // This attempt's rotation is durable, so deliver the token it
                // minted. Same move IdempotencyMiddleware already makes on its own
                // failure path ("the claim's own status is the authoritative
                // answer to 'did my work become durable?'"), keyed here on
                // evidence nobody else could have produced.
                DetachCommitted(minted, stored);
            }
            else if (ex is DbUpdateConcurrencyException)
            {
                // #176 — another request consumed this exact token first
                // (concurrent presentation of the same token). The winner already
                // minted the one live child; fail this one closed rather than fork
                // a second session. Reaching here means the probe found NO token
                // of ours, i.e. our transaction committed nothing — so this is a
                // genuine race, never our own replay wearing its costume. Benign
                // and EXPECTED under normal traffic, so — unlike the branch below —
                // this is not itself worth an Auth.RefreshRevocationFailed alert.
                return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidRefreshToken", "Refresh token is invalid."));
            }
            else
            {
                // Nothing of ours is durable and this is not a CAS loss: report the
                // real failure. Fails CLOSED — a session is never invented here.
                //
                // #273 codex review (P2e) — this SaveChangesAsync is ALSO a
                // refresh-token revocation (it sets stored.RevokedAt = now on the
                // presented token as part of the rotation, above), but it is a
                // tracked read-modify-save rather than one of the bulk
                // ExecuteUpdateAsync calls RunRevocationAsync wraps, so it sits
                // outside that "single hardening point for EVERY refresh-token
                // revocation this class performs" — a failure here that reaches
                // this branch (a real outage, not a benign #176 race and not our
                // own replay) must not silently skip the alert. Same event, same
                // log shape, same rethrow-never-swallow contract as
                // RunRevocationAsync — see LogRevocationFailed. Excluded on
                // OperationCanceledException, same as RunRevocationAsync: a client
                // hangup/cancellation aborting the save is not a security signal
                // worth alerting on.
                if (ex is not OperationCanceledException)
                    LogRevocationFailed(ex, user.Id);
                throw;
            }
        }

        // Roles re-read on every refresh so a demotion takes effect within one
        // access-token lifetime, not at next login.
        var roles = await userManager.GetRolesAsync(user);
        return Result.Success(jwtTokenService.CreateTokenPair(user, [.. roles], rawToken));
    }

    // #176/#364/#468 — identifies five distinct states for a revoked token from
    // one fresh no-tracking parent/replacement snapshot: a live same-epoch grace
    // replacement, a genuine same-epoch replay (including a bulk-revoked
    // no-pointer tip), a pointer atomically severed by concurrent logout, a
    // linked child written with a retired epoch by an old replica, or a
    // revocation stamped ahead of this request's clock. The last three fail
    // INERTLY because none is theft evidence in the current family.
    private async Task<(RefreshToken? Replacement, bool FailInert)>
        InspectGraceReplacementAsync(
        RefreshToken revoked, DateTimeOffset now, CancellationToken ct)
    {
        var inspection = await (
            from parent in db.RefreshTokens.AsNoTracking()
            where parent.TokenHash == revoked.TokenHash
                && parent.UserId == revoked.UserId
                && parent.AccountId == revoked.AccountId
                && parent.IssuedEpoch == revoked.IssuedEpoch
            from freshReplacement in db.RefreshTokens.AsNoTracking()
                .Where(candidate => candidate.TokenHash == parent.ReplacedByTokenHash
                    && candidate.UserId == parent.UserId
                    && candidate.AccountId == parent.AccountId)
                .DefaultIfEmpty()
            select new { Parent = parent, Replacement = freshReplacement })
            .SingleOrDefaultAsync(ct);

        if (inspection is null || inspection.Parent.RevokedAt is null)
            return (null, false);

        // The caller may have loaded P -> C immediately before logout atomically
        // fenced C and severed P. Comparing that previously observed link with
        // this fresh parent/child snapshot identifies only that concurrent
        // logout transition. A row that was already a revoked no-pointer tip at
        // the original lookup is instead the existing strict #176 replay shape.
        if (revoked.ReplacedByTokenHash is not null
            && inspection.Parent.ReplacedByTokenHash is null)
            return (null, true);

        if (inspection.Parent.ReplacedByTokenHash is null)
            return (null, false);

        var replacement = inspection.Replacement;
        if (replacement is not null && replacement.IssuedEpoch != inspection.Parent.IssuedEpoch)
            return (null, true);

        var graceSeconds = jwtOptions.Value.RefreshReuseGraceSeconds;
        var elapsed = now - inspection.Parent.RevokedAt.Value;

        // Replay evidence that does NOT depend on the clock is settled FIRST,
        // and is unchanged by #468: grace switched off, a second grace hop (the
        // one-hop bound that stops a stolen token being walked down the chain),
        // or a replacement that is no longer the live tip. None of the three is
        // a statement about WHEN the revocation happened, so no clock
        // disagreement can excuse them. Testing the skew case ahead of these
        // instead — as #468 first shipped — let an attacker suppress the family
        // revoke outright by replaying against a node whose clock trails the
        // stamping one: the leap-frog, the moved-on chain, and even a
        // deployment that disabled grace entirely all failed inert (codex
        // review of #468, pinned from all three sides in
        // RefreshGraceClockRaceTests).
        if (graceSeconds <= 0                        // grace disabled → strict replay
            || inspection.Parent.RevokedByGrace      // already a grace hop → don't chain (one-hop bound)
            || replacement is null || !replacement.IsActive(now))  // chain moved on → not a benign retry
            return (null, false);

        // What remains is a benign-looking retry off the live tip, where the
        // only open question is whether it landed inside the window — the one
        // question the clock answers.
        //
        // #468 — a RevokedAt stamped AHEAD of the instant we read it cannot be
        // explained by concurrency: RefreshAsync reads its clock after the
        // lookup, so anything we can observe was committed before that read. A
        // future stamp therefore means the clocks disagree (a node running
        // ahead, an NTP step), and a disagreeing clock is evidence about
        // nothing. Fail inert, exactly as a losing tab already does: no family
        // revocation. The previous code folded this into the window check and
        // answered it by revoking every session the user had, which is the one
        // outcome a clock anomaly must never cause.
        if (elapsed < TimeSpan.Zero)
            return (null, true);

        if (elapsed > TimeSpan.FromSeconds(graceSeconds))
            return (null, false);

        db.RefreshTokens.Attach(replacement);
        return (replacement, false);
    }
}
