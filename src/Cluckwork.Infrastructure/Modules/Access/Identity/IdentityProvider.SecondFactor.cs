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
            return Result.Failure<TokenPair>(LogExpiredChallenge());
        // Parsed before anything digests it: malformed input is refused, uncharged.
        var passkey = input as SecondFactorInput.Passkey;
        SecondFactorCode? code = passkey?.Proof;
        if (code is null && !SecondFactorCode.TryParse(((SecondFactorInput.Code)input).Raw, out code))
            return Result.Failure<TokenPair>(SecondFactorErrors.Incorrect);

        // #579: the read refresh uses. Suspension also bumps the epoch, so one
        // that lands after this read leaves the minted token inert.
        var accountIsActive = await AccountIsActiveAsync(claims.AccountId, ct);
        if (accountIsActive is null) return Result.Failure<TokenPair>(LogExpiredChallenge());
        if (accountIsActive == false) return Result.Failure<TokenPair>(SecondFactorErrors.FarmSuspended);

        return await SignInOwnedAsync(async token =>
        {
            // Record 1282 decision 3: the ceremony is consumed by the owned
            // transaction's first statement, so every refusal after it spends it.
            var assertion = passkey is null
                ? null
                : new PasskeyAssertion(
                    await passkeys.ConsumeAsync(passkey.Proof.Ceremony, CeremonyPurpose.SecondStep, claims.UserId, token),
                    CredentialId: null, passkey.RequestHost);
            var outcome = await twoFactor.VerifyAsync(claims.AccountId, claims.UserId, code, claims.Binding, assertion, token);
            return outcome is FactorOutcome.Accepted accepted
                ? Result.Success(new AcceptedSignIn(claims.AccountId, claims.UserId, accepted))
                : Result.Failure<AcceptedSignIn>(passkey is not null && outcome is FactorOutcome.Rejected
                    ? SecondFactorErrors.PasskeyRefused
                    : Refusal(outcome));
        }, ct);
    }

    // Decision 3: reads the challenge to learn the user, without spending it,
    // and refuses a challenge the code step would refuse a passkey for.
    public async Task<Result<PasskeyCeremonyStart>> StartSecondFactorPasskeyAsync(
        string? challenge, string requestHost, CancellationToken ct = default)
    {
        if (ReadChallenge(challenge) is { } claims
            && await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == claims.UserId && u.AccountId == claims.AccountId, ct) is { } user
            && TwoFactorCredentials.Admits(user, claims.Binding, SecondFactorKinds.Passkey))
            return Result.Success(await passkeys.StartSecondStepAsync(user, requestHost, ct));
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

    private Error LogExpiredChallenge()
    {
        securityEvents.SecondFactorChallengeExpired();
        return SecondFactorErrors.ChallengeExpired;
    }
}
