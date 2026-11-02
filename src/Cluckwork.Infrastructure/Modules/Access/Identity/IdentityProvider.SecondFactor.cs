using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 "What a caller sees": the login code step. Only it mints an enrolled
// user's refresh row, cookie and access token.
public sealed partial class IdentityProvider
{
    public async Task<Result<TokenPair>> CompleteSecondFactorLoginAsync(
        string challenge, string code, CancellationToken ct = default)
    {
        // A joined transaction would leave a refused outcome's charge to a
        // caller that can roll it back, so the code step owns its own.
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("The login code step runs in its own transaction, never a caller's.");

        // The challenge first, so a malformed code on a dead challenge gets the 401.
        if (SecondFactorChallenge.Read(challenge, jwtOptions.Value, timeProvider.GetUtcNow()) is not { } claims)
            return LogAndRefuseExpiredChallenge();
        // Parsed before anything digests it: malformed input is refused, uncharged.
        if (!SecondFactorCode.TryParse(code, out var parsed))
            return Result.Failure<TokenPair>(SecondFactorErrors.Incorrect);

        // #579: the read refresh uses. Suspension also bumps the epoch, so one
        // that lands after this read leaves the minted token inert.
        var accountIsActive = await db.Accounts.IgnoreQueryFilters()
            .Where(account => account.Id == claims.AccountId)
            .Select(account => (bool?)account.IsActive)
            .FirstOrDefaultAsync(ct);
        if (accountIsActive is null) return LogAndRefuseExpiredChallenge();
        if (accountIsActive == false)
            return Result.Failure<TokenPair>(Error.Validation(
                SecondFactorErrorCodes.FarmSuspended, "This farm is suspended. Contact your administrator."));

        var (rawToken, tokenHash) = GenerateRefreshToken();
        TokenPair? delivered = null;
        try
        {
            // Outside IdempotentUnit, so a refused outcome's charge commits. The
            // owned transaction runs once: a replay after a lost commit would
            // find the challenge spent (G3.14).
            return await AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
            {
                var outcome = await twoFactor.VerifyAsync(claims.AccountId, claims.UserId, parsed, claims.Binding, token);
                if (outcome is not FactorOutcome.Accepted accepted)
                {
                    // A refused outcome's charge stands even if the client has
                    // gone: a cancelled commit would roll it back.
                    await transaction.CommitAsync(CancellationToken.None);
                    return Refused(outcome);
                }

                // Decision 19: a superseded or refused attempt leaves the tenant
                // and the actor unresolved, so it can write no audit row.
                var user = await db.Users.SingleAsync(u => u.Id == claims.UserId && u.AccountId == claims.AccountId, token);
                var roles = await userManager.GetRolesAsync(user);
                tenant.Resolve(user.AccountId);
                currentUser.Resolve(user.Id, user.Email ?? string.Empty, [.. roles]);
                if (accepted.RecoveryCodesLeft is { } remaining)
                    await audit.WriteAsync(AuditActions.UserRecoveryCodeUsed, "User", user.Id,
                        reason: null, details: new { remaining }, ct: token);

                db.RefreshTokens.Add(NewToken(user, tokenHash, signedInAt: timeProvider.GetUtcNow()));
                await db.SaveChangesAsync(token);
                delivered = jwtTokenService.CreateTokenPair(user, [.. roles], rawToken);
                await transaction.CommitAsync(token);
                return Result.Success(delivered);
            }, ct);
        }
        catch (Exception)
        {
            // #269: the commit may have landed with its acknowledgement lost. The
            // refresh row this attempt minted proves the whole unit committed;
            // fails closed when the probe cannot tell.
            if (delivered is null || !await MintedTokenIsDurableAsync(tokenHash)) throw;
            return Result.Success(delivered);
        }
    }

    private Result<TokenPair> Refused(FactorOutcome outcome) => outcome switch
    {
        FactorOutcome.Superseded => LogAndRefuseExpiredChallenge(),
        FactorOutcome.Unavailable => Result.Failure<TokenPair>(SecondFactorErrors.KeyUnavailable),
        FactorOutcome.Throttled throttled => Result.Failure<TokenPair>(new RetryLaterError(throttled.RetryAfter)),
        _ => Result.Failure<TokenPair>(SecondFactorErrors.Incorrect),
    };

    private Result<TokenPair> LogAndRefuseExpiredChallenge()
    {
        securityEvents.SecondFactorChallengeExpired();
        return Result.Failure<TokenPair>(Error.Validation(
            SecondFactorErrorCodes.ChallengeExpired, "Your sign-in expired. Sign in again."));
    }
}
