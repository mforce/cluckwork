using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 "What a caller sees": the login code step. Only it and the passkey
// sign-in (#1285) mint the refresh row, cookie and access token of a user with
// a second factor.
public sealed partial class IdentityProvider
{
    public async Task<Result<TokenPair>> CompleteSecondFactorLoginAsync(
        string challenge, SecondFactorInput input, CancellationToken ct = default)
    {
        // The challenge first, so a malformed code on a dead challenge gets the 401.
        if (ReadChallenge(challenge) is not { } claims)
            return LogAndRefuseExpiredChallenge();
        // Parsed before anything digests it: malformed input is refused, uncharged.
        SecondFactorCode code;
        switch (input)
        {
            case SecondFactorInput.Passkey passkey:
                code = passkey.Proof;
                break;
            case SecondFactorInput.Code raw when SecondFactorCode.TryParse(raw.Raw, out var parsed):
                code = parsed;
                break;
            default:
                return Result.Failure<TokenPair>(SecondFactorErrors.Incorrect);
        }

        // #579: the read refresh uses. Suspension also bumps the epoch, so one
        // that lands after this read leaves the minted token inert.
        var accountIsActive = await AccountIsActiveAsync(claims.AccountId, ct);
        if (accountIsActive is null) return LogAndRefuseExpiredChallenge();
        if (accountIsActive == false) return Result.Failure<TokenPair>(FarmSuspended);

        return await SignInOwnedAsync(async token =>
        {
            // Record 1282 decision 3: the ceremony is consumed by the owned
            // transaction's first statement, so every refusal after it spends it.
            var passkey = input is SecondFactorInput.Passkey p
                ? new PasskeyAssertion(
                    await passkeys.ConsumeAsync(p.Proof.Ceremony, CeremonyPurpose.SecondStep, claims.UserId, token),
                    CredentialId: null, p.RequestHost)
                : null;
            var outcome = await twoFactor.VerifyAsync(claims.AccountId, claims.UserId, code, claims.Binding, passkey, token);
            return outcome is FactorOutcome.Accepted accepted
                ? Result.Success(new AcceptedSignIn(claims.AccountId, claims.UserId, accepted))
                : Result.Failure<AcceptedSignIn>(passkey is not null && outcome is FactorOutcome.Rejected
                    ? PasskeyRefused
                    : Refusal(outcome));
        }, ct);
    }

    // Decision 3: reads the challenge to learn the user, without spending it.
    public async Task<Result<PasskeyCeremonyStart>> StartSecondFactorPasskeyAsync(
        string? challenge, string requestHost, CancellationToken ct = default)
    {
        if (ReadChallenge(challenge) is { } claims
            && await passkeys.StartSecondStepAsync(claims.AccountId, claims.UserId, requestHost, ct) is { } start)
            return Result.Success(start);
        return Result.Failure<PasskeyCeremonyStart>(LogExpiredChallenge());
    }

    private ChallengeClaims? ReadChallenge(string? challenge) =>
        SecondFactorChallenge.Read(challenge, jwtOptions.Value, timeProvider.GetUtcNow());

    private Task<bool?> AccountIsActiveAsync(Guid accountId, CancellationToken ct) =>
        db.Accounts.IgnoreQueryFilters()
            .Where(account => account.Id == accountId)
            .Select(account => (bool?)account.IsActive)
            .FirstOrDefaultAsync(ct);

    // A proof the owned transaction accepted, for the user it names.
    private sealed record AcceptedSignIn(Guid AccountId, Guid UserId, FactorOutcome.Accepted Proof)
    {
        public override string ToString() => "[redacted]";
    }

    // The owned transaction a sign-in finish runs in. Outside IdempotentUnit,
    // so a refused outcome's charge and a consumed ceremony commit, and never in
    // a caller's transaction, which could roll them back (C1). It runs once: a
    // replay after a lost commit would find the challenge or ceremony spent
    // (G3.14).
    private async Task<Result<TokenPair>> SignInOwnedAsync(
        Func<CancellationToken, Task<Result<AcceptedSignIn>>> prove, CancellationToken ct)
    {
        var (rawToken, tokenHash) = GenerateRefreshToken();
        TokenPair? delivered = null;
        try
        {
            return await AmbientTransaction.RunOwnedAsync(db.Database, async (transaction, token) =>
            {
                var proof = await prove(token);
                if (proof.IsFailure)
                {
                    // A refused outcome's charge stands even if the client has
                    // gone: a cancelled commit would roll it back.
                    await transaction.CommitAsync(CancellationToken.None);
                    return Result.Failure<TokenPair>(proof.Error);
                }

                // Decision 19: a superseded or refused attempt leaves the tenant
                // and the actor unresolved, so it can write no audit row.
                var (accountId, userId, accepted) = proof.Value;
                var user = await db.Users.SingleAsync(u => u.Id == userId && u.AccountId == accountId, token);
                var roles = await userManager.GetRolesAsync(user);
                tenant.Resolve(user.AccountId);
                currentUser.Resolve(user.Id, user.Email ?? string.Empty, [.. roles]);
                await SecondFactorProof.WriteRecoveryCodeUsedAsync(audit, user.Id, accepted, token);
                if (accepted.Passkey is { } used)
                    await passkeys.RecordUseAsync(used, accountId, userId, token);

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

    private Error Refusal(FactorOutcome outcome) => outcome switch
    {
        FactorOutcome.Superseded => LogExpiredChallenge(),
        FactorOutcome.Unavailable => SecondFactorErrors.KeyUnavailable,
        FactorOutcome.Throttled throttled => new RetryLaterError(throttled.RetryAfter),
        _ => SecondFactorErrors.Incorrect,
    };

    private static readonly Error ChallengeExpired =
        Error.Validation(SecondFactorErrorCodes.ChallengeExpired, "Your sign-in expired. Sign in again.");

    private static readonly Error FarmSuspended =
        Error.Validation(SecondFactorErrorCodes.FarmSuspended, "This farm is suspended. Contact your administrator.");

    private Error LogExpiredChallenge()
    {
        securityEvents.SecondFactorChallengeExpired();
        return ChallengeExpired;
    }

    private Result<TokenPair> LogAndRefuseExpiredChallenge() => Result.Failure<TokenPair>(LogExpiredChallenge());
}
