namespace Cluckwork.Infrastructure.Identity;

using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class IdentityProvider(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IJwtTokenService jwtTokenService,
    AppDbContext db,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider,
    Cluckwork.Application.Common.IAuditWriter audit,
    IStepUpGrantRegistry stepUpGrants,
    IHttpContextAccessor httpContextAccessor,
    AuthSecurityEventLogger securityEvents,
    ILogger<IdentityProvider> logger,
    Cluckwork.Application.Features.Accounts.IAccountRepository accounts) : IIdentityProvider
{
    public async Task<Result<TokenPair>> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            // Always pay the PBKDF2 cost so that "user not found" and "wrong password"
            // are indistinguishable by timing.
            userManager.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), TimingEqualization.DummyHash, password);
            securityEvents.LoginFailed();
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidCredentials", "Invalid email or password."));
        }

        if (user.DisabledAt is not null)
        {
            // Pay the same password-hash cost for a correct or incorrect
            // password, but never feed a disabled account into Identity's
            // failed-access counter. Otherwise password validity is observable
            // through both timing and durable lockout state, and an account can
            // emerge from a later re-enable already locked by guesses made while
            // it was disabled.
            userManager.PasswordHasher.VerifyHashedPassword(
                user, user.PasswordHash ?? TimingEqualization.DummyHash, password);
            // #273 codex review (round 4) — same identity-free LoginFailed as
            // every other unsuccessful branch (user-not-found, locked-out,
            // wrong-password): a disabled account is still an unsuccessful
            // /auth/login attempt, and omitting it here silently dropped
            // guesses against disabled accounts from the brute-force stream.
            securityEvents.LoginFailed();
            return Result.Failure<TokenPair>(Error.Validation(
                "Identity.InvalidCredentials", "Invalid email or password."));
        }

        // Account lockout (#128): once failures reach the configured threshold the
        // account is locked for a cool-off window. A locked account is refused with
        // the SAME generic error (and PBKDF2 is still paid) as a wrong password, so
        // the reply never reveals whether an account exists or is locked.
        if (await userManager.IsLockedOutAsync(user))
        {
            // DummyHash guards the (currently unreachable) passwordless-user case
            // so this stays a 401, never an NRE/500 that would leak account state.
            userManager.PasswordHasher.VerifyHashedPassword(user, user.PasswordHash ?? TimingEqualization.DummyHash, password);
            // #273 — same LoginFailed shape as the "user not found" branch above:
            // an attempt against an ALREADY-locked account re-fires LoginFailed but
            // never AccountLockedOut again — that fired once, at the transition.
            securityEvents.LoginFailed();
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidCredentials", "Invalid email or password."));
        }

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            // #273 — LoginFailed carries NO user id/email on any of the three
            // branches (see SecurityEvents.LoginFailed): logging must not turn
            // into the identity-existence oracle the API response already
            // avoids. AccountLockedOut is a SEPARATE event, safe to name the
            // user on, because it only ever fires here — never on the
            // "user not found" branch — so its mere presence can't be used to
            // tell a nonexistent email apart from a wrong password.
            //
            // Emitted BEFORE persisting lockout state, not after: the password
            // has already been confirmed wrong at this point, and
            // RecordFailedAccessAsync is a durable write that can throw (DB
            // trouble). A throw there must not silently drop LoginFailed from
            // the stream — the wrong-password fact is real regardless of
            // whether the lockout counter itself could be persisted
            // (codex review of #349).
            securityEvents.LoginFailed();
            var justLockedOut = await RecordFailedAccessAsync(user);
            if (justLockedOut)
                securityEvents.AccountLockedOut(user.Id);
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidCredentials", "Invalid email or password."));
        }

        // Bind the successful proof to the credential values that were actually
        // verified. ResetFailedAccessCountAsync may lose optimistic concurrency
        // to a password reset/disable and reload that newer row into `user`; the
        // old password must never mint tokens carrying the superseding epoch.
        var verifiedCredentialEpoch = user.CredentialEpoch;
        var verifiedSecurityStamp = user.SecurityStamp;

        // Correct password — clear any accumulated failures (no-op DB-wise if zero).
        await ResetFailedAccessCountAsync(user, ct);
        if (user.DisabledAt is not null
            || user.CredentialEpoch != verifiedCredentialEpoch
            || !string.Equals(user.SecurityStamp, verifiedSecurityStamp, StringComparison.Ordinal))
        {
            return Result.Failure<TokenPair>(Error.Validation(
                "Identity.InvalidCredentials", "Invalid email or password."));
        }

        var (rawToken, tokenHash) = GenerateRefreshToken();
        var minted = NewToken(user, tokenHash);
        db.RefreshTokens.Add(minted);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            // #269 — the same ambiguous commit RefreshAsync documents at length,
            // one step earlier in the session's life. Login is anonymous, so
            // IdempotencyMiddleware's tenant gate skips it and this INSERT is a
            // self-contained unit the execution strategy replays. Id and TokenHash
            // are both fixed before the first attempt (a retry does not regenerate
            // them), so a replay after the commit landed re-inserts the SAME row,
            // hits the unique TokenHash index, and turns a successful login into a
            // 409 "Data conflict" — measured pre-fix, together with a live refresh
            // token that was handed to nobody.
            //
            // Milder than the refresh case (the caller can just log in again, and
            // gets a clean session when they do) but the same defect, and the same
            // answer: if the token this attempt minted is in the database, the
            // login succeeded — deliver it. If it is not, nothing of ours is
            // durable and the real error stands.
            if (!await MintedTokenIsDurableAsync(tokenHash)) throw;
            DetachCommitted(minted);
        }

        var roles = await userManager.GetRolesAsync(user);
        return Result.Success(jwtTokenService.CreateTokenPair(user, [.. roles], rawToken));
    }

    // Shared with /auth/step-up (#308) via AccountLockout — see the note there
    // on why every password oracle must apply this, not just login.
    private Task<bool> RecordFailedAccessAsync(ApplicationUser user) =>
        AccountLockout.RecordFailedAccessAsync(userManager, db, user);

    private Task ResetFailedAccessCountAsync(ApplicationUser user, CancellationToken ct) =>
        AccountLockout.ResetFailedAccessCountAsync(userManager, db, user, ct);

    // #273 — resolved per-call (never cached on the instance): IdentityProvider
    // is scoped per REQUEST, so this is safe to read lazily, but reading it
    // fresh here rather than in the constructor keeps the dependency honest
    // for a caller with no ambient HttpContext (a future non-HTTP caller would
    // just see null -> "unknown", not throw).
    private string ClientIp =>
        httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public async Task<Result<TokenPair>> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var presentedHash = Hash(refreshToken);

        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == presentedHash, ct);
        if (stored is null)
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidRefreshToken", "Refresh token is invalid."));

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
        if (user is null || user.DisabledAt is not null || stored.IssuedEpoch != user.CredentialEpoch)
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

    public Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, string? role,
        string? name = null, bool mustChangePassword = false, CancellationToken ct = default) =>
        // One transaction around create + role assignment: a failed admin
        // creation must not survive as a usable role-less worker account
        // (codex review of PR #78). Disposal without commit rolls back.
        // #307 — joins IdempotencyMiddleware's ambient request transaction
        // when one is open, instead of nesting a second one.
        //
        // #269 — the delegate shape (rather than a scope the caller drives)
        // is what EnableRetryOnFailure forces: a user-initiated transaction
        // has to be opened inside an execution strategy. It is NOT retried —
        // AmbientTransaction's owned path runs it exactly once. Retrying it
        // would be actively wrong: a failed userManager.CreateAsync leaves
        // its ApplicationUser tracked as Added (EF does not detach it, and
        // minting a fresh Guid for the retry does not either), so a second
        // attempt flushes BOTH users into the unique email index and reports
        // a duplicate-key failure in place of the connection failure that
        // actually happened (#269 review). A blanket db.ChangeTracker.Clear()
        // is NOT the alternative — on the CLI/owned path this same
        // AppDbContext can be shared by a longer-lived caller (e.g.
        // SimulationDataSeeder) holding its own entities tracked across
        // several handler calls, and clearing silently drops that caller's
        // pending SaveChanges.
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                AccountId = accountId,
                DisplayName = name, // #163 — optional display name at creation
                MustChangePassword = mustChangePassword // #283 — true only for bootstrap-admin's Owner
            };

            var created = await userManager.CreateAsync(user, password);
            if (!created.Succeeded)
                return Result.Failure<Guid>(await CreateFailureAsync(created, email, accountId));

            if (role is not null)
            {
                if (!Cluckwork.Domain.Accounts.Roles.Assignable.Contains(role))
                    return Result.Failure<Guid>(Error.Validation(
                        "Users.UnknownRole", $"'{role}' is not an assignable role."));

                if (!await roleManager.RoleExistsAsync(role))
                {
                    var roleCreated = await roleManager.CreateAsync(
                        new ApplicationRole { Id = Guid.NewGuid(), Name = role });
                    if (!roleCreated.Succeeded)
                        return Result.Failure<Guid>(Error.Validation("Users.CreateFailed", Describe(roleCreated)));
                }

                var addedToRole = await userManager.AddToRoleAsync(user, role);
                if (!addedToRole.Succeeded)
                    return Result.Failure<Guid>(Error.Validation("Users.CreateFailed", Describe(addedToRole)));
            }

            // Same transaction as the creation (#93): the event needs its own
            // SaveChanges because UserManager flushed its writes already.
            await audit.WriteAsync(AuditActions.UserCreate, "User", user.Id,
                reason: null, details: new { email, role = role ?? "Worker" }, ct: token);
            await db.SaveChangesAsync(token);

            await transaction.CommitAsync(token);
            return Result.Success(user.Id);
        }, ct);

    public async Task<Result> UpdateUserAsync(
        Guid accountId, Guid userId, string? name, CancellationToken ct = default)
    {
        // Scoped to the account: a user id from another tenant simply doesn't
        // match, so this returns NotFound rather than editing a foreign user.
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Id == userId && u.AccountId == accountId, ct);
        if (user is null)
            return Result.Failure(Error.NotFound("Users", userId));

        user.DisplayName = name;
        // Rotate Identity's concurrency token so two concurrent edits don't
        // silently last-write-win: EF checks the ORIGINAL stamp in the UPDATE's
        // WHERE, so the loser matches no row and fails closed to a 409.
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        await audit.WriteAsync(AuditActions.UserUpdate, "User", user.Id,
            reason: null, details: new { name }, ct: ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(Error.Conflict(
                "Users.Conflict", "The user was modified by another request. Reload and retry."));
        }
        return Result.Success();
    }

    public async Task<Result> SetUserPasswordAsync(
        Guid accountId, Guid userId, string newPassword, CancellationToken ct = default)
    {
        // Account-scoped, exactly like UpdateUserAsync: a foreign user id doesn't
        // match and falls through to NotFound rather than resetting someone else's
        // tenant's credentials.
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Id == userId && u.AccountId == accountId, ct);
        if (user is null)
            return Result.Failure(Error.NotFound("Users", userId));

        return await ResetPasswordAndRevokeAsync(
            user, newPassword, AuditActions.UserPasswordSet, reason: null, details: null, ct);
    }

    // #355 — promote/demote. AmbientTransaction shape mirrors
    // ResetPasswordAndRevokeAsync: joins IdempotencyMiddleware's ambient
    // request transaction when one is open; on the owned path (a non-HTTP
    // caller) this is NOT retried — a replay would re-run the role mutation
    // and re-append the audit row a second time (#269, same reasoning as the
    // sibling password-reset method).
    public Task<Result> ChangeUserRoleAsync(
        Guid accountId, Guid userId, string? role, Guid actingUserId, CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            // The account-wide lock, taken UNCONDITIONALLY — even a change
            // that could never affect the Owner count (e.g. Manager ->
            // Sales) — so no future role-changing path can silently miss it
            // (matches UpdateFarmSettingsHandler's own unconditional-locking
            // precedent for #162). The result is VALIDATED, not discarded:
            // GetCurrentLockedAsync resolves from the ambient TenantContext,
            // not from the accountId parameter, so a caller whose tenant
            // context doesn't actually match accountId must fail rather than
            // silently locking (and guarding) the wrong account.
            var lockedAccount = await accounts.GetCurrentLockedAsync(token);
            if (lockedAccount is null || lockedAccount.Id != accountId)
                return Result.Failure(Error.NotFound("Accounts", accountId));

            var user = await db.Users.FirstOrDefaultAsync(
                u => u.Id == userId && u.AccountId == accountId, token);
            if (user is null)
                return Result.Failure(Error.NotFound("Users", userId));

            var actor = await RequireActiveOwnerAsync(accountId, actingUserId, token);
            if (actor.IsFailure)
                return actor;

            var currentRoleNames = (await userManager.GetRolesAsync(user)).ToList();

            // TRUE NO-OP: the requested role set (Worker -> {}, otherwise
            // {role}) equals the target's ACTUAL current role-row set. This
            // single set-equality check correctly handles Worker->Worker
            // (both sides {}), an ordinary unchanged role ({X} == {X}), and
            // fails correctly for the multi-role adversarial case
            // ({Owner,Manager} != {Owner}) — a stray extra row is a real
            // state change even when the requested role already matches one
            // of the rows.
            var requestedRoleSet = role is null
                ? new HashSet<string>()
                : new HashSet<string> { role };
            if (requestedRoleSet.SetEquals(currentRoleNames))
                return Result.Success();

            // LAST-OWNER GUARD: only relevant on an actual demotion away
            // from Owner. Disabled Owners are excluded from the survivor
            // count — a no-op today (nothing sets DisabledAt yet), but
            // closes a real future landmine: once the sibling disable-user
            // slice ships, a farm with one active Owner and one already-
            // disabled Owner could otherwise have its only WORKING Owner
            // demoted, because a naive count still sees "2 Owners."
            if (currentRoleNames.Contains(Cluckwork.Domain.Accounts.Roles.Owner)
                && role != Cluckwork.Domain.Accounts.Roles.Owner)
            {
                if (await CountOtherActiveOwnersAsync(accountId, userId, token) == 0)
                    return Result.Failure(Error.Validation(
                        "Users.LastOwner",
                        "This is the account's last Owner — promote another user before demoting this one."));
            }

            // Apply: remove every current role row (PLURAL — Identity permits
            // more than one row per user even though every ordinary write
            // path here assigns exactly one; a singular removal of just one
            // role could silently leave a second stray row behind), then add
            // the new one if not Worker.
            //
            // Identity's UserStore.UpdateAsync swallows a concurrency loss
            // into a FAILED IdentityResult rather than throwing (this
            // codebase's own documented prior incident — see
            // AccountLockout.cs's comment on RecordFailedAccessAsync /
            // ResetFailedAccessCountAsync) — so each result is inspected for
            // the exact "ConcurrencyFailure" code, mapped to Users.Conflict,
            // separately from whatever the final SaveChangesAsync catch
            // below handles.
            if (currentRoleNames.Count > 0)
            {
                var removed = await userManager.RemoveFromRolesAsync(user, currentRoleNames);
                if (!removed.Succeeded)
                    return Result.Failure(IsConcurrencyFailure(removed)
                        ? Error.Conflict("Users.Conflict", "The user was modified by another request. Reload and retry.")
                        : Error.Validation("Users.RoleChangeFailed", Describe(removed)));
            }

            if (role is not null)
            {
                if (!Cluckwork.Domain.Accounts.Roles.Assignable.Contains(role))
                    return Result.Failure(Error.Validation(
                        "Users.UnknownRole", $"'{role}' is not an assignable role."));

                if (!await roleManager.RoleExistsAsync(role))
                {
                    var roleCreated = await roleManager.CreateAsync(
                        new ApplicationRole { Id = Guid.NewGuid(), Name = role });
                    if (!roleCreated.Succeeded)
                        return Result.Failure(Error.Validation("Users.RoleChangeFailed", Describe(roleCreated)));
                }

                var added = await userManager.AddToRoleAsync(user, role);
                if (!added.Succeeded)
                    return Result.Failure(IsConcurrencyFailure(added)
                        ? Error.Conflict("Users.Conflict", "The user was modified by another request. Reload and retry.")
                        : Error.Validation("Users.RoleChangeFailed", Describe(added)));
            }

            // Rotate the TARGET's SecurityStamp. CredentialEpoch kills bearer
            // and refresh tokens, but a step-up grant (#308) is a THIRD,
            // separate credential validated against SecurityStamp, not the
            // epoch (StepUpGrantService.ValidateAsync) — without this, a
            // grant issued to the target just before their own role changed
            // (e.g. right before being promoted to Owner) would still be
            // spendable after they sign back in with a fresh epoch (codex
            // review, PR #475). Same IdentityResult-concurrency handling as
            // the role-row mutations above, for the same reason.
            var stampRotated = await userManager.UpdateSecurityStampAsync(user);
            if (!stampRotated.Succeeded)
                return Result.Failure(IsConcurrencyFailure(stampRotated)
                    ? Error.Conflict("Users.Conflict", "The user was modified by another request. Reload and retry.")
                    : Error.Validation("Users.RoleChangeFailed", Describe(stampRotated)));

            user.CredentialEpoch++;
            await RevokeAllActiveForUserAsync(user.Id, timeProvider.GetUtcNow(), token);

            // "Worker" sentinel for an empty set, matching CreateUserAsync's
            // own audit convention (role ?? "Worker") — an array so the
            // multi-role cleanup-only case (a stray row removed, the
            // effective/highest role unchanged) reads as the real state
            // change it is (e.g. oldRoles: ["Admin","Manager"], newRoles:
            // ["Admin"]) instead of a misleading single "X -> X" string.
            var oldRoles = currentRoleNames.Count > 0 ? currentRoleNames.ToArray() : ["Worker"];
            var newRoles = role is not null ? new[] { role } : ["Worker"];
            await audit.WriteAsync(AuditActions.UserRoleChanged, "User", user.Id,
                reason: null, details: new { oldRoles, newRoles }, ct: token);

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure(Error.Conflict(
                    "Users.Conflict", "The user was modified by another request. Reload and retry."));
            }
            await transaction.CommitAsync(token);
            return Result.Success();
        }, ct);

    private static bool IsConcurrencyFailure(IdentityResult result) =>
        result.Errors.Any(e => e.Code == "ConcurrencyFailure");

    // #356 — deliberately NOT the role path's bare "Reload and retry." wording.
    // Disable/enable require step-up UNCONDITIONALLY, and the grant is
    // single-use and is spent BEFORE the account lock is taken (see
    // DisableUserHandler's note), so by the time this conflict is raised the
    // caller's password proof is already gone and resubmitting the identical
    // request answers 403, not success. The copy says so rather than promising
    // a retry that cannot work.
    private static Error ConcurrencyConflict() => Error.Conflict(
        "Users.Conflict",
        "The user was modified by another request, and your password confirmation has already been used. "
        + "Reload, confirm your password again, and retry.");

    // Re-verify the ACTOR is still authorized, INSIDE the caller's account-
    // locked transaction. ASP.NET's authorization middleware checked their role
    // once, at the START of the request — before the transaction, and before
    // the account lock, ever ran. Without this, an Owner whose own demotion (or
    // disable) committed while their UNRELATED request sat queued behind the
    // lock could still complete it afterwards. A disabled actor retains its
    // Owner ROLE ROW — only authentication is blocked — so DisabledAt is
    // checked here too, not just effective role.
    //
    // MUST be a fresh, untracked read (AsNoTracking + a plain join, not
    // db.Users.FirstOrDefaultAsync/userManager.GetRolesAsync): the step-up
    // paths already ran this actor through StepUpGrantService.ValidateAsync's
    // userManager.FindByIdAsync BEFORE the transaction started, on this SAME
    // scoped DbContext. EF's identity map means any later TRACKED query for the
    // same PK returns that cached instance's property values — NOT a fresh row
    // — silently defeating this whole re-check (codex review, PR #475).
    //
    // Shared by ChangeUserRoleAsync (#355) and DisableUserAsync/
    // EnableUserAsync (#356): "who may remove an Owner's access" is one rule,
    // and three copies of it is three chances to fix only two.
    private async Task<Result> RequireActiveOwnerAsync(
        Guid accountId, Guid actingUserId, CancellationToken token)
    {
        var actorRow = await db.Users.AsNoTracking()
            .Where(u => u.Id == actingUserId && u.AccountId == accountId)
            .Select(u => new { u.DisabledAt })
            .SingleOrDefaultAsync(token);
        if (actorRow is null || actorRow.DisabledAt is not null)
            return Result.Failure(AppError.Forbidden());

        var actorIsOwner = await (
            from userRole in db.UserRoles
            join r in db.Roles on userRole.RoleId equals r.Id
            where userRole.UserId == actingUserId && r.Name == Cluckwork.Domain.Accounts.Roles.Owner
            select r.Name).AnyAsync(token);

        return actorIsOwner ? Result.Success() : Result.Failure(AppError.Forbidden());
    }

    // Owners of this account, EXCLUDING `excludingUserId`, who are not disabled.
    // Both callers of the last-Owner guard ask the same question, and the
    // DisabledAt exclusion is the part that is easy to omit: a naive count sees
    // an already-disabled co-Owner as a survivor and lets the only WORKING
    // Owner be removed.
    private Task<int> CountOtherActiveOwnersAsync(
        Guid accountId, Guid excludingUserId, CancellationToken token) => (
            from userRole in db.UserRoles
            join r in db.Roles on userRole.RoleId equals r.Id
            join u in db.Users on userRole.UserId equals u.Id
            where r.Name == Cluckwork.Domain.Accounts.Roles.Owner
                && u.AccountId == accountId
                && u.Id != excludingUserId
                && u.DisabledAt == null
            select u.Id).CountAsync(token);

    // #356 — disable a user. See IIdentityProvider for the full contract; the
    // load-bearing part is that this is NOT just a flag write. The flag is what
    // CredentialEpochMiddleware reads today (#364); the EPOCH BUMP is what
    // makes a later re-enable safe, because it leaves every pre-disable access
    // token permanently behind the account's current epoch.
    //
    // AmbientTransaction shape mirrors ChangeUserRoleAsync: it joins
    // IdempotencyMiddleware's ambient request transaction when one is open, and
    // on the owned path (a non-HTTP caller) is NOT retried — a replay would
    // re-run the mutation and re-append the audit row (#269).
    public Task<Result> DisableUserAsync(
        Guid accountId, Guid userId, Guid actingUserId, string? reason,
        CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            // The account-wide lock, taken UNCONDITIONALLY — even for a target
            // who could never affect the Owner count. The last-active-Owner
            // guard is NOT race-safe on ConcurrencyStamp: two Owners disabling
            // each other touch DIFFERENT rows and share no concurrency token,
            // so both would read "two active Owners" and both would commit.
            // The result is VALIDATED, not discarded: GetCurrentLockedAsync
            // resolves from the ambient TenantContext, not from the accountId
            // parameter, so a caller whose tenant context does not match must
            // fail rather than silently locking (and guarding) another account.
            var lockedAccount = await accounts.GetCurrentLockedAsync(token);
            if (lockedAccount is null || lockedAccount.Id != accountId)
                return Result.Failure(Error.NotFound("Accounts", accountId));

            var user = await db.Users.FirstOrDefaultAsync(
                u => u.Id == userId && u.AccountId == accountId, token);
            if (user is null)
                return Result.Failure(Error.NotFound("Users", userId));

            var actor = await RequireActiveOwnerAsync(accountId, actingUserId, token);
            if (actor.IsFailure)
                return actor;

            // TRUE NO-OP: already disabled. Skips the second epoch bump, the
            // restamped DisabledAt (which would rewrite the answer to "when
            // were they disabled") and the audit row.
            if (user.DisabledAt is not null)
                return Result.Success();

            var targetIsOwner = await (
                from userRole in db.UserRoles
                join r in db.Roles on userRole.RoleId equals r.Id
                where userRole.UserId == userId && r.Name == Cluckwork.Domain.Accounts.Roles.Owner
                select r.Name).AnyAsync(token);
            if (targetIsOwner
                && await CountOtherActiveOwnersAsync(accountId, userId, token) == 0)
            {
                return Result.Failure(Error.Validation(
                    "Users.LastOwner",
                    "This is the account's last active Owner — promote or re-enable another Owner first."));
            }

            var now = timeProvider.GetUtcNow();
            user.DisabledAt = now;
            user.DisabledBy = actingUserId;

            // Rotate the target's SecurityStamp. CredentialEpoch kills bearer
            // and refresh tokens, but a step-up grant (#308) is a THIRD,
            // separate credential validated against SecurityStamp — without
            // this, a grant minted moments before the disable would still be
            // spendable once the user is re-enabled. Identity's
            // UserStore.UpdateAsync swallows a concurrency loss into a FAILED
            // IdentityResult rather than throwing, so the result is inspected
            // for the exact "ConcurrencyFailure" code separately from whatever
            // the SaveChangesAsync catch below handles.
            var stampRotated = await userManager.UpdateSecurityStampAsync(user);
            if (!stampRotated.Succeeded)
                return Result.Failure(IsConcurrencyFailure(stampRotated)
                    ? ConcurrencyConflict()
                    : Error.Validation("Users.DisableFailed", Describe(stampRotated)));

            user.CredentialEpoch++;
            await RevokeAllActiveForUserAsync(user.Id, now, token);

            await audit.WriteAsync(AuditActions.UserDisabled, "User", user.Id,
                reason: reason, details: null, ct: token);

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure(ConcurrencyConflict());
            }
            await transaction.CommitAsync(token);
            return Result.Success();
        }, ct);

    // #356 — re-enable. Deliberately asymmetric with DisableUserAsync: it
    // clears the two Disabled* columns and audits, and touches NOTHING else.
    // No epoch bump, no restored pre-disable epoch, no stamp rotation, no
    // token re-issue. That asymmetry IS the feature — it is what stops a
    // re-enable from resurrecting every access token the disable killed.
    public Task<Result> EnableUserAsync(
        Guid accountId, Guid userId, Guid actingUserId, CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            // Same unconditional lock as the disable path. Enable removes no
            // Owner, so there is no survivor count to protect — the lock is
            // here so a disable and an enable of the same user serialize
            // instead of interleaving into an inconsistent DisabledAt/epoch
            // pair.
            var lockedAccount = await accounts.GetCurrentLockedAsync(token);
            if (lockedAccount is null || lockedAccount.Id != accountId)
                return Result.Failure(Error.NotFound("Accounts", accountId));

            var user = await db.Users.FirstOrDefaultAsync(
                u => u.Id == userId && u.AccountId == accountId, token);
            if (user is null)
                return Result.Failure(Error.NotFound("Users", userId));

            var actor = await RequireActiveOwnerAsync(accountId, actingUserId, token);
            if (actor.IsFailure)
                return actor;

            // TRUE NO-OP: already active. No audit row for a non-event.
            if (user.DisabledAt is null)
                return Result.Success();

            // Both columns describe ONE live fact. Leaving DisabledBy behind
            // would be a column that reads as current and is not; the history
            // lives in the audit trail, which both directions write.
            user.DisabledAt = null;
            user.DisabledBy = null;

            // Rotate the stamp — and note this does NOT breach the "enable
            // touches nothing else" asymmetry, which exists to stop a re-enable
            // REVIVING a credential. A rotation only ever invalidates.
            //
            // It is here because a plain SaveChangesAsync leaves ConcurrencyStamp
            // untouched, while Identity's UserStore.UpdateAsync issues a
            // FULL-ENTITY update guarded on that same stamp. So an Owner's
            // concurrent SetUserPassword — which reads the user tracked, then
            // spends the whole PBKDF2 window in GeneratePasswordResetTokenAsync
            // /ResetPasswordAsync before writing — would still match the
            // unrotated stamp and write DisabledAt back from its pre-enable
            // snapshot. The Owner would have received 204 and a User.Enabled
            // audit row for an enable that was silently undone, with the user
            // still locked out. Rotating makes that stale write lose its CAS and
            // surface as Users.Conflict instead. Pinned by
            // ConcurrentStampChange_DuringTheEnableItself_Is409.
            var stampRotated = await userManager.UpdateSecurityStampAsync(user);
            if (!stampRotated.Succeeded)
                return Result.Failure(IsConcurrencyFailure(stampRotated)
                    ? ConcurrencyConflict()
                    : Error.Validation("Users.EnableFailed", Describe(stampRotated)));

            await audit.WriteAsync(AuditActions.UserEnabled, "User", user.Id,
                reason: null, details: null, ct: token);

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure(ConcurrencyConflict());
            }
            await transaction.CommitAsync(token);
            return Result.Success();
        }, ct);

    public async Task<Result> BreakGlassResetAsync(
        Guid accountId, Guid userId, string newPassword, string? reason,
        CancellationToken ct = default)
    {
        // Same account-scoped lookup as SetUserPasswordAsync — a foreign id
        // resolves to NotFound, never a cross-tenant reset.
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Id == userId && u.AccountId == accountId, ct);
        if (user is null)
            return Result.Failure(Error.NotFound("Users", userId));

        // Record WHERE the offline command ran (#265 review): the CLI has no
        // authenticated actor, so the audit row would otherwise attribute the
        // reset to "(unresolved)". Capturing host + OS user gives the break-glass
        // row real accountability beyond the free-text reason.
        var details = new { host = Environment.MachineName, osUser = Environment.UserName };

        // A DISTINCT audit action + the operator's reason so a break-glass reset
        // stands out from an ordinary Owner-initiated one (#265).
        return await ResetPasswordAndRevokeAsync(
            user, newPassword, AuditActions.UserBreakGlassReset, reason, details, ct);
    }

    // Shared core (#165 SetUserPassword + #265 break-glass): reset the password
    // without the current one — which applies the full policy and rotates the
    // SecurityStamp — clear any lockout, evict every live session, and append one
    // audit row, all in a single transaction so the password change and the
    // session revocation land together or not at all (#165 review). Since #364
    // it also bumps CredentialEpoch in that same transaction (below), so an
    // already-issued access token is rejected by CredentialEpochMiddleware on
    // its very next request — no longer merely bounded by the ~15-min
    // access-token lifetime.
    private Task<Result> ResetPasswordAndRevokeAsync(
        ApplicationUser user, string newPassword, string auditAction, string? reason,
        object? details, CancellationToken ct)
    {
        // #307 — joins IdempotencyMiddleware's ambient request transaction
        // when one is open, instead of nesting a second one.
        //
        // #269 — the delegate shape is EnableRetryOnFailure's requirement (a
        // user-initiated transaction must be opened inside an execution
        // strategy), not a retry: AmbientTransaction's owned path — the one
        // `recover-admin` takes, having no ambient transaction — runs this
        // exactly once. Replaying it would flush the failed attempt's still-
        // Added audit row and refresh token a second time (EF does not detach
        // them), leaving a duplicate audit entry and an active token that was
        // never issued to anyone (#269 review).
        return AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            // Reset via a generated token — Identity's supported way to set a password
            // without the current one.
            var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
            var reset = await userManager.ResetPasswordAsync(user, resetToken, newPassword);
            if (!reset.Succeeded)
                return Result.Failure(Error.Validation("Users.PasswordRejected", Describe(reset)));

            // #283 — any successful password reset clears a pending first-run gate.
            // Covers an Owner using SetUserPassword or an offline break-glass reset
            // on a user who never got around to their forced first-run change; the
            // user obviously already has a working password at that point, so
            // there is nothing left to force. `user` is the same DbContext-tracked
            // instance db.SaveChangesAsync persists below — no separate save needed.
            user.MustChangePassword = false;
            user.CredentialEpoch++;

            // Clear any active lockout / failed-attempt count (#265 review). Without
            // this, the exact case break-glass exists for — a user locked out by
            // repeated failed logins — would get a fresh password that LoginAsync
            // still refuses until the lockout window expires (it checks
            // IsLockedOutAsync before the password), defeating the recovery.
            await userManager.ResetAccessFailedCountAsync(user);
            await userManager.SetLockoutEndDateAsync(user, null);

            await RevokeAllActiveForUserAsync(user.Id, timeProvider.GetUtcNow(), token);
            await audit.WriteAsync(auditAction, "User", user.Id,
                reason: reason, details: details, ct: token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Result.Success();
        }, ct);
    }

    public async Task<Result<TokenPair>> ChangeOwnPasswordAsync(
        Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return Result.Failure<TokenPair>(Error.Validation(
                "Identity.InvalidCredentials", "Invalid email or password."));

        // One transaction around change + revoke + re-issue: the bulk revoke is
        // immediate SQL, so a failure before the fresh token is saved would sign
        // the caller out of everything while reporting an error (#165 review).
        // #307 — joins IdempotencyMiddleware's ambient request transaction
        // when one is open, instead of nesting a second one.
        //
        // #269 — this endpoint is EXEMPT from idempotency wrapping
        // (IdempotencyMiddleware.ResponseNotCacheable — a password change is
        // self-invalidating, so replay was never useful here), so every real
        // call takes AmbientTransaction's "owned" path. That path runs this
        // exactly once; the delegate shape is EnableRetryOnFailure's
        // requirement, not a retry. A replay here would both re-add the
        // refresh token the failed attempt already tracked as Added and
        // re-verify `currentPassword` against a hash a prior attempt may
        // already have changed (see SingleAttemptExecution).
        return await AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            // ChangePasswordAsync verifies the current password AND applies the policy.
            var changed = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
            if (!changed.Succeeded)
            {
                // Distinguish "your current password is wrong" from "the new one is
                // too weak" — the user needs to know which to fix. Neither leaks
                // anything: the caller is already authenticated as this user.
                var wrongCurrent = changed.Errors.Any(e => e.Code == "PasswordMismatch");
                return Result.Failure<TokenPair>(wrongCurrent
                    ? Error.Validation("Users.CurrentPasswordIncorrect", "Current password is incorrect.")
                    : Error.Validation("Users.PasswordRejected", Describe(changed)));
            }

            // #283 — this is the SPA's first-login "set your password" screen's
            // actual mechanism (it reuses this endpoint: the operator already
            // knows the generated first-run password as their "current" one).
            // Clearing the flag here is what lets the fresh token pair below omit
            // the must_change_password claim, un-gating the rest of the app.
            if (user.MustChangePassword)
                user.MustChangePassword = false;
            user.CredentialEpoch++;

            // Every session dies (other devices are signed out), then this caller gets
            // a fresh pair so the device that made the change stays signed in.
            var now = timeProvider.GetUtcNow();
            await RevokeAllActiveForUserAsync(user.Id, now, token);

            var (rawToken, tokenHash) = GenerateRefreshToken();
            db.RefreshTokens.Add(NewToken(user, tokenHash));
            await audit.WriteAsync(AuditActions.UserPasswordChanged, "User", user.Id,
                reason: null, details: null, ct: token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);

            var roles = await userManager.GetRolesAsync(user);
            return Result.Success(jwtTokenService.CreateTokenPair(user, [.. roles], rawToken));
        }, ct);
    }

    // Identity's duplicate-email wording is only surfaced when the email
    // already belongs to THIS account. A duplicate in another tenant gets a
    // generic message so the endpoint is not a cross-tenant registration
    // oracle (single-farm today, multi-tenant infrastructure dormant).
    private async Task<Error> CreateFailureAsync(IdentityResult result, string email, Guid accountId)
    {
        if (!result.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
            return Error.Validation("Users.CreateFailed", Describe(result));

        var existing = await userManager.FindByEmailAsync(email);
        return existing is not null && existing.AccountId == accountId
            ? Error.Validation("Users.DuplicateEmail", "A user with this email already exists.")
            : Error.Validation("Users.CreateFailed", "Could not create the user.");
    }

    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(Guid accountId, CancellationToken ct = default)
    {
        var roleByUser = await (
            from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            select new { userRole.UserId, role.Name })
            .ToListAsync(ct);
        // Highest role wins, matching AuthPolicies.EffectiveRole — an
        // Owner+ReadOnly user must list as Owner, not by insertion order
        // (codex/conventions review of #104).
        var lookup = roleByUser
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => Rank(x.Name)).First().Name!);

        // #356 — disabled users are LISTED, not filtered out: an Owner cannot
        // re-enable someone the list refuses to show them.
        var rows = await db.Users
            .Where(u => u.AccountId == accountId)
            .OrderBy(u => u.Email)
            .Select(u => new { u.Id, u.Email, u.DisplayName, u.DisabledAt })
            .ToListAsync(ct);
        return rows
            .Select(u => new UserSummary(
                u.Id, u.Email!, u.DisplayName, lookup.GetValueOrDefault(u.Id, "Worker"), u.DisabledAt))
            .ToList();
    }

    // Highest role wins, matching AuthPolicies.EffectiveRole — an Owner+ReadOnly
    // user resolves to Owner, not by insertion order.
    private static int Rank(string? name) => name switch
    {
        Cluckwork.Domain.Accounts.Roles.Owner => 4,
        Cluckwork.Domain.Accounts.Roles.Manager => 3,
        Cluckwork.Domain.Accounts.Roles.Sales => 2,
        Cluckwork.Domain.Accounts.Roles.ReadOnly => 1,
        _ => 0,
    };

    public async Task<UserProfile?> GetUserAsync(
        Guid accountId, Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users
            .Where(u => u.Id == userId && u.AccountId == accountId)
            .Select(u => new { u.Id, u.Email, u.DisplayName, u.Language, u.PreferredStepperUnit })
            .FirstOrDefaultAsync(ct);
        if (user is null) return null;

        var roleNames = await (
            from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == userId
            select role.Name).ToListAsync(ct);
        var effectiveRole = roleNames.OrderByDescending(Rank).FirstOrDefault() ?? "Worker";

        return new UserProfile(
            user.Id, user.Email!, user.DisplayName, effectiveRole, user.Language,
            user.PreferredStepperUnit);
    }

    public async Task<Result> SetLanguageAsync(
        Guid accountId, Guid userId, string? language, CancellationToken ct = default)
    {
        var affected = await db.Users
            .Where(u => u.Id == userId && u.AccountId == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, language), ct);
        return affected == 0 ? Result.Failure(Error.NotFound("Users", userId)) : Result.Success();
    }

    // #444 — same shape as SetLanguageAsync; the caller has already confirmed a
    // non-null unit is still an active EggUnitConversion.
    public async Task<Result> SetStepperUnitAsync(
        Guid accountId, Guid userId, EggUnit? unit, CancellationToken ct = default)
    {
        var affected = await db.Users
            .Where(u => u.Id == userId && u.AccountId == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PreferredStepperUnit, unit), ct);
        return affected == 0 ? Result.Failure(Error.NotFound("Users", userId)) : Result.Success();
    }

    private static string Describe(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(e => e.Description));

    public async Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        // Bulk conditional update, not a tracked read-modify-save: the #176 xmin
        // concurrency token would otherwise make this throw if the token was
        // rotated concurrently. WHERE RevokedAt == null makes it idempotent.
        var presentedHash = Hash(refreshToken);
        var now = timeProvider.GetUtcNow();
        Guid? ownerId = null;

        // #273 codex review (round 2, P2c) — the failure boundary covers the
        // owner LOOKUP as well as the update. The lookup is a prerequisite of
        // the revocation, not something happening beside it: if it throws, the
        // bulk update below never runs, so the revocation fails just as
        // completely as if the update had thrown — but it used to do so in
        // silence, with no Auth.RefreshRevocationFailed for a deployment to
        // alert on. One boundary over the whole sequence, and EXACTLY ONE
        // emission either way: the update inside is the raw statement
        // (RevokeByHashCoreAsync), never a second RunRevocationAsync wrapper,
        // so a failing update cannot log the event twice.
        //
        // `ownerId` is read through a closure at catch time rather than passed
        // in, because who the token belongs to is only known once the lookup
        // this boundary now covers has succeeded — a lookup failure logs the
        // event with no user id, which is honest, rather than not at all.
        await RunRevocationAsync(() => ownerId, async () =>
        {
            // #308 — a real logout must invalidate any outstanding step-up grant
            // for this user (a grant captured before this logout must not work
            // after it), so read the owning user id BEFORE the bulk update.
            //
            // Deliberately NOT filtered on RevokedAt: the cookie may already be
            // stale because a background refresh rotated it moments earlier, and
            // #336's review caught that the revoked-only lookup silently skipped
            // recording the logout in exactly that case — leaving a grant valid
            // for the rest of its lifetime after the user had logged out.
            // Identifying the user is what matters here, not whether this
            // particular row is still live. A genuinely unknown token still
            // records nothing (logout is best-effort and always fires, see
            // AuthEndpoints.Logout).
            var tokenRow = await db.RefreshTokens
                .Where(t => t.TokenHash == presentedHash)
                .Select(t => new { t.UserId })
                .FirstOrDefaultAsync(ct);
            ownerId = tokenRow?.UserId;

            // #336 review (2nd round) — record BEFORE the bulk update, not
            // after. RecordLogout is in-memory and cannot fail;
            // ExecuteUpdateAsync hits the database and can throw (transient
            // connection loss, deadlock). With the record afterwards, that
            // exception skipped it entirely: the SPA has already cleared its
            // local token and treats logout as best-effort, so the user believes
            // they logged out while a captured access token plus an unexpired
            // step-up grant stayed usable for the rest of the grant's life.
            // Recording first means a database failure over-revokes (grants
            // dead, refresh row possibly still live) instead of under-revoking —
            // the same fail-safe direction AuthEndpoints.Logout applies to the
            // bearer path one layer up. That fix corrected only the outer call
            // site; this is the same hazard inside the cookie path.
            if (tokenRow is not null)
                stepUpGrants.RecordLogout(tokenRow.UserId, now);

            await RevokeByHashCoreAsync(presentedHash, now, ct);
        });
    }

    // #336 review — the access-token half of logout revocation. The cookie owner
    // above and the caller's authenticated identity can be DIFFERENT users (see
    // IIdentityProvider.RecordLogoutAsync), so the cookie alone cannot identify
    // who logged out. Records the instant only; refresh tokens are untouched.
    //
    // Uses the same injected TimeProvider as the cookie path, so a single logout
    // that records both users stamps them with one consistent instant. The
    // registry keeps the LATEST recorded instant per user, so a same-user logout
    // that reaches both paths is idempotent rather than double-counted.
    public Task RecordLogoutAsync(Guid userId, CancellationToken ct = default)
    {
        stepUpGrants.RecordLogout(userId, timeProvider.GetUtcNow());
        return Task.CompletedTask;
    }

    private RefreshToken NewToken(ApplicationUser user, string tokenHash)
    {
        var now = timeProvider.GetUtcNow();
        return new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            AccountId = user.AccountId,
            TokenHash = tokenHash,
            IssuedEpoch = user.CredentialEpoch,
            CreatedAt = now,
            ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenDays)
        };
    }

    // #176/#364/#468 — identifies four distinct states for a revoked token: a
    // live same-epoch grace replacement, a genuine same-epoch replay, a linked
    // child written with a retired epoch by an old replica, or a revocation
    // stamped ahead of this request's clock. The last two must fail INERTLY —
    // they are mixed-version and clock-disagreement evidence respectively, and
    // neither is evidence that the current-epoch family was stolen, so neither
    // may answer with a family revocation.
    private async Task<(RefreshToken? Replacement, bool FailInert)>
        InspectGraceReplacementAsync(
        RefreshToken revoked, DateTimeOffset now, CancellationToken ct)
    {
        if (revoked.ReplacedByTokenHash is null || revoked.RevokedAt is null)
            return (null, false);

        var replacement = await db.RefreshTokens.FirstOrDefaultAsync(t =>
            t.TokenHash == revoked.ReplacedByTokenHash
            && t.UserId == revoked.UserId
            && t.AccountId == revoked.AccountId, ct);
        if (replacement is not null && replacement.IssuedEpoch != revoked.IssuedEpoch)
            return (null, true);

        var graceSeconds = jwtOptions.Value.RefreshReuseGraceSeconds;
        var elapsed = now - revoked.RevokedAt.Value;

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
            || revoked.RevokedByGrace                // already a grace hop → don't chain (one-hop bound)
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

        return (replacement, false);
    }

    private async Task RevokeAllActiveForUserAsync(
        Guid userId, DateTimeOffset now, CancellationToken ct) =>
        await RevokeAllActiveForUserAsync(userId, issuedEpoch: null, now, ct);

    private async Task RevokeAllActiveForUserAsync(
        Guid userId, int? issuedEpoch, DateTimeOffset now, CancellationToken ct)
    {
        // Bulk update rather than tracked read-modify-save: it never trips the
        // #176 xmin concurrency token (so it is safe to call from the rotation
        // fail path) and revokes the whole family in one statement.
        // Merge note: #273's RunRevocationAsync wrapper and #364's epoch scoping
        // are independent and BOTH apply. The wrapper only adds an alertable
        // event when the update throws; the filter decides which rows it
        // touches. Losing either half is silent — without the wrapper a failed
        // lock-out goes unreported, and without the filter a revoke reaches a
        // newer epoch's sessions.
        await RunRevocationAsync(
            userId,
            () => db.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null
                    && (!issuedEpoch.HasValue || t.IssuedEpoch == issuedEpoch.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct));
    }

    // #273 — hardening point for the BULK-UPDATE refresh-token revocations
    // this class performs (replay-triggered family revocation, logout,
    // password reset/change, break-glass — via RevokeAllActiveForUserAsync and
    // RevokeRefreshTokenAsync): if the bulk update itself throws instead of
    // completing, the safety action meant to lock a suspected attacker out of
    // every session never ran. That is worth its own alertable event
    // (Auth.RefreshRevocationFailed), separate from whatever triggered the
    // revoke attempt (a replay, a logout, a password change, ...) — logging it
    // here once, rather than at each bulk-update call site, is what keeps it
    // from being forgotten at a future one. Logs and RETHROWS: the caller's
    // existing behavior on a DB failure (bubble up, eventually a 500) is
    // unchanged — this only adds observability, never swallows the failure.
    // OperationCanceledException is excluded: a client hangup/cancellation
    // aborting the update is not a security signal worth alerting on.
    //
    // #273 codex review (P2e) — NOT the only revocation this class performs:
    // RefreshAsync's own token rotation also revokes (a tracked
    // read-modify-save, not a bulk update — see its own try/catch), and that
    // path cannot funnel through this method's Func<Task> shape without also
    // swallowing the DbUpdateConcurrencyException it must handle differently
    // (a benign race, not a failure worth alerting on). Both paths log through
    // the same LogRevocationFailed helper below, so they stay identical in
    // shape even though they can't share this wrapper's control flow.
    //
    // #273 codex review (round 2, P2c) — the boundary takes the user id as an
    // ACCESSOR, not a value, because RevokeRefreshTokenAsync's boundary now also
    // covers the lookup that discovers who the token belongs to: at the moment
    // the boundary is entered there is no user id yet, and at the moment it
    // catches there may or may not be one. The Guid?-taking overload below keeps
    // the three call sites that do know it up front unchanged.
    private async Task RunRevocationAsync(Func<Guid?> userId, Func<Task> revoke)
    {
        try
        {
            await revoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRevocationFailed(ex, userId());
            throw;
        }
    }

    private Task RunRevocationAsync(Guid? userId, Func<Task> revoke) =>
        RunRevocationAsync(() => userId, revoke);

    // #273 codex review (P2e) — the actual log call, factored out so
    // RunRevocationAsync's three bulk-update revocations (family revoke,
    // logout, break-glass/password-reset/change's RevokeAllActiveForUserAsync)
    // and RefreshAsync's tracked rotation (which revokes by read-modify-save,
    // not a bulk update, so it cannot go through RunRevocationAsync's
    // Func<Task> shape without also swallowing the DbUpdateConcurrencyException
    // it needs to handle separately) log the IDENTICAL event/shape rather than
    // risking the two call sites drifting apart.
    private void LogRevocationFailed(Exception ex, Guid? userId) =>
        logger.LogError(ex, "{SecurityEvent} user={UserId}",
            SecurityEvents.RefreshRevocationFailed, userId);

    // The RAW single-token revoke, with no failure boundary of its own: its only
    // caller (RevokeRefreshTokenAsync) already runs inside a boundary that
    // starts one statement earlier, at the owner lookup. Wrapping here as well
    // would emit Auth.RefreshRevocationFailed twice for one failed logout.
    private Task RevokeByHashCoreAsync(string tokenHash, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.TokenHash == tokenHash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    // #269 — "did the save I just attempted actually commit?", answered by the
    // one piece of evidence that cannot belong to anybody else: the hash of a
    // 256-bit value THIS attempt generated moments ago and has not yet handed to
    // a single caller. No other request, and no competing consumer of the same
    // refresh token, can have written this row — they mint their own random
    // token. So its presence is proof the attempt's transaction became durable,
    // and its absence is proof nothing of ours did (EF's save is one transaction:
    // the rotation UPDATE and the INSERT land together or not at all).
    //
    // Fails CLOSED by construction: the caller rethrows unless this returns true,
    // so a probe that cannot reach the database reports the original error and
    // the client retries — never a session invented from a failed read.
    private Task<bool> MintedTokenIsDurableAsync(string mintedHash) =>
        // AnyAsync compiles to SELECT EXISTS (...) — a scalar. It therefore reads
        // the DATABASE and can never be answered from the change tracker's own
        // still-Added copy of the very row it is asking about, which a
        // FirstOrDefaultAsync could be via identity resolution.
        //
        // CancellationToken.None deliberately: a client that has already
        // disconnected must not leave a live token stranded, and this call is
        // what decides whether one was minted. Same reasoning as
        // IdempotencyMiddleware's post-failure claim probe.
        db.RefreshTokens.AnyAsync(t => t.TokenHash == mintedHash, CancellationToken.None);

    // The save threw, so EF still tracks these as Added/Modified even though the
    // database has already accepted them. Detach exactly the entities this call
    // touched, so any later SaveChanges cannot re-flush an already-durable row
    // (a duplicate key or a phantom concurrency conflict, attributed to us).
    //
    // Per-entity, never db.ChangeTracker.Clear(): on the owned/CLI path this same
    // AppDbContext can be shared by a longer-lived caller holding its own pending
    // writes, and clearing drops them silently (the regression that broke
    // SimulationDataSeeder — see SingleAttemptExecution). This is the same
    // surgical detach IdempotencyMiddleware.TryClaimOrInspectAsync uses.
    private void DetachCommitted(params object[] entities)
    {
        foreach (var entity in entities)
            db.Entry(entity).State = EntityState.Detached;
    }

    // 256-bit random token; only the SHA-256 hash is persisted.
    private static (string Raw, string Hash) GenerateRefreshToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
