using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 decisions 8 and 17: the user turns their own two-factor off, or an
// Owner resets another user's. Both clear through ClearAsync; only a Cleared
// outcome is a factor change that ends sessions and connected apps.
public sealed partial class IdentityProvider
{
    // The answer CredentialEpochMiddleware gives a superseded token.
    private static readonly Error CredentialsSuperseded =
        new("Auth.CredentialsSuperseded", "Your credentials have been superseded. Sign in again.");

    public Task<Result<TokenPair?>> TurnOffTwoFactorAsync(
        Guid accountId, Guid userId, int credentialEpoch, CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            // The grant was spent before this lock, so a password reset, an
            // enrolment or a disable can land in between. Each bumps the
            // epoch, so the session that asked is then stale: it must not
            // clear the factor or mint a fresh pair.
            await UserRowLock.TakeAsync(db, accountId, userId, token);
            var epoch = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId && u.AccountId == accountId)
                .Select(u => (int?)u.CredentialEpoch)
                .SingleOrDefaultAsync(token);
            if (epoch != credentialEpoch)
                return Result.Failure<TokenPair?>(CredentialsSuperseded);

            var outcome = await twoFactor.ClearAsync(accountId, userId, token);
            if (outcome == ClearOutcome.NotFound)
                return Result.Failure<TokenPair?>(CredentialsSuperseded);
            if (outcome != ClearOutcome.Cleared)
            {
                await transaction.CommitAsync(token);
                return Result.Success<TokenPair?>(null);
            }

            // Every other session and connected app ends; this one gets a fresh
            // pair from the new epoch, as a password change does. Step-up proved
            // both factors, so the lineage starts now (decision 8).
            var user = await db.Users.SingleAsync(u => u.Id == userId && u.AccountId == accountId, token);
            var now = timeProvider.GetUtcNow();
            await RevokeAllActiveForUserAsync(user.Id, now, token);
            var (rawToken, tokenHash) = GenerateRefreshToken();
            db.RefreshTokens.Add(NewToken(user, tokenHash, signedInAt: now));
            await audit.WriteAsync(AuditActions.UserTwoFactorDisabled, "User", user.Id,
                reason: null, details: null, ct: token);
            await db.SaveChangesAsync(token);

            var roles = await userManager.GetRolesAsync(user);
            var pair = jwtTokenService.CreateTokenPair(user, [.. roles], rawToken);
            await transaction.CommitAsync(token);
            return Result.Success<TokenPair?>(pair);
        }, ct);

    // The role, email and disable pattern: the account lock, then the actor
    // re-check inside it, before ClearAsync writes anything.
    public Task<Result> ResetUserTwoFactorAsync(
        Guid accountId, Guid userId, Guid actingUserId, CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            var lockedAccount = await accounts.GetCurrentLockedAsync(token);
            if (lockedAccount is null || lockedAccount.Id != accountId)
                return Result.Failure(Error.NotFound("Accounts", accountId));

            var actor = await RequireActiveOwnerAsync(accountId, actingUserId, token);
            if (actor.IsFailure)
                return actor;

            switch (await twoFactor.ClearAsync(accountId, userId, token))
            {
                case ClearOutcome.NotFound:
                    return Result.Failure(Error.NotFound("Users", userId));
                case ClearOutcome.NotEnrolled:
                    break;
                case ClearOutcome.PendingDeleted:
                    // An ended enrolment can be a takeover in progress, so it is audited.
                    await audit.WriteAsync(AuditActions.UserTwoFactorReset, "User", userId,
                        reason: null, details: new { pending = true }, ct: token);
                    break;
                case ClearOutcome.Cleared:
                    await RevokeAllActiveForUserAsync(userId, timeProvider.GetUtcNow(), token);
                    await audit.WriteAsync(AuditActions.UserTwoFactorReset, "User", userId,
                        reason: null, details: null, ct: token);
                    break;
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Result.Success();
        }, ct);
}
