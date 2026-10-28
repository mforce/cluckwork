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
    // One body for a wrong, reused or malformed code and a locked account, so
    // the answer never confirms that a guessed code was valid (G3.12).
    private static readonly Error SecondFactorIncorrect = Error.Validation(
        SecondFactorErrorCodes.Incorrect, "That code did not work. If you just used it, wait for the next one.");

    private static readonly Error SecondFactorKeyUnavailable = Error.Validation(
        SecondFactorErrorCodes.KeyUnavailable, "Your authenticator key cannot be read. Use a recovery code.");

    public async Task<Result<TokenPair>> CompleteSecondFactorLoginAsync(
        string challenge, string code, CancellationToken ct = default)
    {
        // The challenge first, so a malformed code on a dead challenge gets the 401.
        if (SecondFactorChallenge.Read(challenge, jwtOptions.Value, timeProvider.GetUtcNow()) is not { } claims)
            return ChallengeExpired();
        // Parsed before anything digests it: malformed input is refused, uncharged.
        if (!SecondFactorCode.TryParse(code, out var parsed))
            return Result.Failure<TokenPair>(SecondFactorIncorrect);

        // #579: the read refresh uses. Suspension also bumps the epoch, so one
        // that lands after this read leaves the minted token inert.
        var accountIsActive = await db.Accounts.IgnoreQueryFilters()
            .Where(account => account.Id == claims.AccountId)
            .Select(account => (bool?)account.IsActive)
            .FirstOrDefaultAsync(ct);
        if (accountIsActive is null) return ChallengeExpired();
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
                    await transaction.CommitAsync(token);
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
        FactorOutcome.Superseded => ChallengeExpired(),
        FactorOutcome.Unavailable => Result.Failure<TokenPair>(SecondFactorKeyUnavailable),
        FactorOutcome.Throttled throttled => Result.Failure<TokenPair>(new RetryLaterError(throttled.RetryAfter)),
        _ => Result.Failure<TokenPair>(SecondFactorIncorrect),
    };

    private Result<TokenPair> ChallengeExpired()
    {
        securityEvents.SecondFactorChallengeExpired();
        return Result.Failure<TokenPair>(Error.Validation(
            SecondFactorErrorCodes.ChallengeExpired, "Your sign-in expired. Sign in again."));
    }
}
