namespace Cluckwork.Infrastructure.Identity;

using System.Security.Cryptography;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed class IdentityProvider(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IJwtTokenService jwtTokenService,
    AppDbContext db,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider,
    Cluckwork.Application.Common.IAuditWriter audit) : IIdentityProvider
{
    // Pre-computed V3 PBKDF2 hash used to equalize login timing when the email is not found,
    // preventing user enumeration via response-time analysis.
    private static readonly string DummyHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!9x");

    public async Task<Result<TokenPair>> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            // Always pay the PBKDF2 cost so that "user not found" and "wrong password"
            // are indistinguishable by timing.
            userManager.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), DummyHash, password);
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidCredentials", "Invalid email or password."));
        }

        // Account lockout (#128): once failures reach the configured threshold the
        // account is locked for a cool-off window. A locked account is refused with
        // the SAME generic error (and PBKDF2 is still paid) as a wrong password, so
        // the reply never reveals whether an account exists or is locked.
        if (await userManager.IsLockedOutAsync(user))
        {
            // ?? DummyHash guards the (currently unreachable) passwordless-user case
            // so this stays a 401, never an NRE/500 that would leak account state.
            userManager.PasswordHasher.VerifyHashedPassword(user, user.PasswordHash ?? DummyHash, password);
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidCredentials", "Invalid email or password."));
        }

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            await RecordFailedAccessAsync(user);
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidCredentials", "Invalid email or password."));
        }

        // Correct password — clear any accumulated failures (no-op DB-wise if zero).
        await userManager.ResetAccessFailedCountAsync(user);

        var (rawToken, tokenHash) = GenerateRefreshToken();
        db.RefreshTokens.Add(NewToken(user, tokenHash));
        await db.SaveChangesAsync(ct);

        var roles = await userManager.GetRolesAsync(user);
        return Result.Success(jwtTokenService.CreateTokenPair(user, [.. roles], rawToken));
    }

    // AccessFailedAsync persists the increment under the user row's optimistic
    // concurrency stamp. Parallel failed logins for one account would otherwise
    // drop the losing writer's increment, letting a distributed burst dodge the
    // threshold — the exact attack per-account lockout (vs the per-IP limiter)
    // exists to stop. Retry against a freshly reloaded user until it commits.
    private async Task RecordFailedAccessAsync(ApplicationUser user)
    {
        // Bounded generously: only the concurrency-conflict path retries, and the
        // per-account contention that produces conflicts is itself capped by the
        // per-IP rate limiter (#143). The cap prevents an unbounded loop while
        // still letting every real failure land under normal contention.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if ((await userManager.AccessFailedAsync(user)).Succeeded)
                return;
            // The write lost the concurrency race. FindById would hand back the
            // same identity-map instance (stale stamp), so refresh the tracked
            // entity's values from the DB before retrying — `db` is the same
            // scoped context the UserManager store writes through.
            await db.Entry(user).ReloadAsync();
        }
    }

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
        db.RefreshTokens.Add(NewToken(user, newHash));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // #176 — another request consumed this exact token first (concurrent
            // presentation of the same token). The winner already minted the one
            // live child; fail this one closed rather than fork a second session.
            return Result.Failure<TokenPair>(Error.Validation("Identity.InvalidRefreshToken", "Refresh token is invalid."));
        }

        // Roles re-read on every refresh so a demotion takes effect within one
        // access-token lifetime, not at next login.
        var roles = await userManager.GetRolesAsync(user);
        return Result.Success(jwtTokenService.CreateTokenPair(user, [.. roles], rawToken));
    }

    public async Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, string? role,
        string? name = null, CancellationToken ct = default)
    {
        // One transaction around create + role assignment: a failed admin
        // creation must not survive as a usable role-less worker account
        // (codex review of PR #78). Disposal without commit rolls back.
        // #307 — joins IdempotencyMiddleware's ambient request transaction
        // when one is open, instead of nesting a second one.
        await using var transaction = await AmbientTransaction.BeginAsync(db.Database, ct);

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            AccountId = accountId,
            DisplayName = name // #163 — optional display name at creation
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
            reason: null, details: new { email, role = role ?? "Worker" }, ct: ct);
        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
        return Result.Success(user.Id);
    }

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
    private async Task<Result> ResetPasswordAndRevokeAsync(
        ApplicationUser user, string newPassword, string auditAction, string? reason,
        object? details, CancellationToken ct)
    {
        // #307 — joins IdempotencyMiddleware's ambient request transaction
        // when one is open, instead of nesting a second one.
        await using var transaction = await AmbientTransaction.BeginAsync(db.Database, ct);

        // Reset via a generated token — Identity's supported way to set a password
        // without the current one.
        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        var reset = await userManager.ResetPasswordAsync(user, resetToken, newPassword);
        if (!reset.Succeeded)
            return Result.Failure(Error.Validation("Users.PasswordRejected", Describe(reset)));

        // Clear any active lockout / failed-attempt count (#265 review). Without
        // this, the exact case break-glass exists for — a user locked out by
        // repeated failed logins — would get a fresh password that LoginAsync
        // still refuses until the lockout window expires (it checks
        // IsLockedOutAsync before the password), defeating the recovery.
        await userManager.ResetAccessFailedCountAsync(user);
        await userManager.SetLockoutEndDateAsync(user, null);

        await RevokeAllActiveForUserAsync(user.Id, timeProvider.GetUtcNow(), ct);
        await audit.WriteAsync(auditAction, "User", user.Id,
            reason: reason, details: details, ct: ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success();
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
        await using var transaction = await AmbientTransaction.BeginAsync(db.Database, ct);

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

        // Every session dies (other devices are signed out), then this caller gets
        // a fresh pair so the device that made the change stays signed in.
        var now = timeProvider.GetUtcNow();
        await RevokeAllActiveForUserAsync(user.Id, now, ct);

        var (rawToken, tokenHash) = GenerateRefreshToken();
        db.RefreshTokens.Add(NewToken(user, tokenHash));
        await audit.WriteAsync("User.PasswordChanged", "User", user.Id,
            reason: null, details: null, ct: ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var roles = await userManager.GetRolesAsync(user);
        return Result.Success(jwtTokenService.CreateTokenPair(user, [.. roles], rawToken));
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
        await db.RefreshTokens
            .Where(t => t.TokenHash == presentedHash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
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
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
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
