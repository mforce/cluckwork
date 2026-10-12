using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

public sealed partial class IdentityProvider(
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
    Cluckwork.Application.Modules.Farm.Accounts.IAccountRepository accounts,
    IAccountUserDirectory directory) : IIdentityProvider
{
    public async Task<FarmSignIn?> ResolveFarmCodeAsync(string farmCode, CancellationToken ct = default) =>
        await accounts.FindBySlugAsync(farmCode, ct) is { } account
            ? new FarmSignIn(account.Id, account.IsActive)
            : null;

    public async Task<Result<TokenPair>> LoginAsync(
        Guid accountId, string email, string password, CancellationToken ct = default)
    {
        // #532 — ACCOUNT-SCOPED. The old global userManager.FindByEmailAsync was
        // not merely unscoped, it was a crash: UserStore backs it with
        // SingleOrDefaultAsync, so the first email shared by two farms threw
        // here — breaking login for BOTH farms' users, not just the duplicate.
        var user = await directory.FindByAccountEmailAsync(accountId, email, ct);
        if (user is null)
        {
            // Always pay the PBKDF2 cost so that "user not found" and "wrong password"
            // are indistinguishable by timing.
            userManager.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), TimingEqualization.DummyHash, password);
            securityEvents.LoginFailed();
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidCredentials", "Invalid email or password."));
        }

        // #1031 — a stored epoch below 1 is refused exactly like a disabled
        // account. It must never mint, and repairing the row is an operator's
        // job, not a login's.
        if (user.DisabledAt is not null || user.CredentialEpoch < 1)
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

    private static bool IsConcurrencyFailure(IdentityResult result) =>
        result.Errors.Any(e => e.Code == "ConcurrencyFailure");

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
            where userRole.UserId == actingUserId && r.Name == Cluckwork.Domain.Modules.Farm.Contracts.Roles.Owner
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
            where r.Name == Cluckwork.Domain.Modules.Farm.Contracts.Roles.Owner
                && u.AccountId == accountId
                && u.Id != excludingUserId
                && u.DisabledAt == null
            select u.Id).CountAsync(token);

    private static string Describe(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(e => e.Description));
}
