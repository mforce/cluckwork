namespace Cluckwork.Infrastructure.Identity;

using System.Security.Cryptography;
using Cluckwork.Application.Common;
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
    ILogger<IdentityProvider> logger) : IIdentityProvider
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
            var justLockedOut = await RecordFailedAccessAsync(user);
            // #273 — LoginFailed carries NO user id/email on any of the three
            // branches (see SecurityEvents.LoginFailed): logging must not turn
            // into the identity-existence oracle the API response already
            // avoids. AccountLockedOut is a SEPARATE event, safe to name the
            // user on, because it only ever fires here — never on the
            // "user not found" branch — so its mere presence can't be used to
            // tell a nonexistent email apart from a wrong password.
            securityEvents.LoginFailed();
            if (justLockedOut)
                securityEvents.AccountLockedOut(user.Id);
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidCredentials", "Invalid email or password."));
        }

        // Correct password — clear any accumulated failures (no-op DB-wise if zero).
        await ResetFailedAccessCountAsync(user, ct);

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
        var now = timeProvider.GetUtcNow();

        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == presentedHash, ct);
        if (stored is null)
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
            var graced = await TryGraceReplacementAsync(stored, now, ct);
            if (graced is null)
            {
                // #273 — a genuine replay: log BEFORE attempting the revoke, so
                // detection is recorded even if the revoke itself throws (the
                // catch below logs that separately as Auth.RefreshRevocationFailed
                // and rethrows — this call still completes and returns normally).
                logger.LogWarning("{SecurityEvent} user={UserId} client={ClientIp}",
                    SecurityEvents.RefreshTokenReplayDetected, stored.UserId, ClientIp);
                await RevokeAllActiveForUserAsync(stored.UserId, now, ct);
                return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidRefreshToken", "Refresh token is invalid."));
            }
            stored = graced;
            viaGrace = true;
        }

        if (stored.ExpiresAt <= now)
            return Result.Failure<TokenPair>(Error.Validation("Identity.ExpiredRefreshToken", "Refresh token has expired."));

        var user = await userManager.FindByIdAsync(stored.UserId.ToString());
        if (user is null)
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidRefreshToken", "Refresh token is invalid."));

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
            if (await MintedTokenIsDurableAsync(newHash))
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
            await audit.WriteAsync("User.Create", "User", user.Id,
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
        await audit.WriteAsync("User.Update", "User", user.Id,
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
            user, newPassword, "User.PasswordSet", reason: null, details: null, ct);
    }

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
            user, newPassword, "User.BreakGlassReset", reason, details, ct);
    }

    // Shared core (#165 SetUserPassword + #265 break-glass): reset the password
    // without the current one — which applies the full policy and rotates the
    // SecurityStamp — clear any lockout, evict every live session, and append one
    // audit row, all in a single transaction so the password change and the
    // session revocation land together or not at all (#165 review). An already-
    // issued access token stays valid until it expires (~15 min) — no denylist.
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

            // Every session dies (other devices are signed out), then this caller gets
            // a fresh pair so the device that made the change stays signed in.
            var now = timeProvider.GetUtcNow();
            await RevokeAllActiveForUserAsync(user.Id, now, token);

            var (rawToken, tokenHash) = GenerateRefreshToken();
            db.RefreshTokens.Add(NewToken(user, tokenHash));
            await audit.WriteAsync("User.PasswordChanged", "User", user.Id,
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

        var rows = await db.Users
            .Where(u => u.AccountId == accountId)
            .OrderBy(u => u.Email)
            .Select(u => new { u.Id, u.Email, u.DisplayName })
            .ToListAsync(ct);
        return rows
            .Select(u => new UserSummary(
                u.Id, u.Email!, u.DisplayName, lookup.GetValueOrDefault(u.Id, "Worker")))
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
            .Select(u => new { u.Id, u.Email, u.DisplayName, u.Language })
            .FirstOrDefaultAsync(ct);
        if (user is null) return null;

        var roleNames = await (
            from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == userId
            select role.Name).ToListAsync(ct);
        var effectiveRole = roleNames.OrderByDescending(Rank).FirstOrDefault() ?? "Worker";

        return new UserProfile(user.Id, user.Email!, user.DisplayName, effectiveRole, user.Language);
    }

    public async Task<Result> SetLanguageAsync(
        Guid accountId, Guid userId, string? language, CancellationToken ct = default)
    {
        var affected = await db.Users
            .Where(u => u.Id == userId && u.AccountId == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, language), ct);
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
            CreatedAt = now,
            ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenDays)
        };
    }

    // #176 — returns the live replacement to rotate when `revoked` is a benign
    // grace retry (rotated within the grace window and its replacement is still
    // active), or null when it is a genuine replay that must revoke the family.
    private async Task<RefreshToken?> TryGraceReplacementAsync(
        RefreshToken revoked, DateTimeOffset now, CancellationToken ct)
    {
        var graceSeconds = jwtOptions.Value.RefreshReuseGraceSeconds;
        var elapsed = now - (revoked.RevokedAt ?? now);
        if (graceSeconds <= 0                       // grace disabled → strict replay
            || revoked.RevokedByGrace               // already a grace hop → don't chain (one-hop bound)
            || revoked.ReplacedByTokenHash is null
            || revoked.RevokedAt is null
            || elapsed < TimeSpan.Zero               // clock-skew guard: a future RevokedAt must not widen the window
            || elapsed > TimeSpan.FromSeconds(graceSeconds))
            return null;

        var replacement = await db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == revoked.ReplacedByTokenHash, ct);
        return replacement is not null && replacement.IsActive(now) ? replacement : null;
    }

    private async Task RevokeAllActiveForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        // Bulk update rather than tracked read-modify-save: it never trips the
        // #176 xmin concurrency token (so it is safe to call from the rotation
        // fail path) and revokes the whole family in one statement.
        await RunRevocationAsync(
            userId,
            () => db.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null)
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
