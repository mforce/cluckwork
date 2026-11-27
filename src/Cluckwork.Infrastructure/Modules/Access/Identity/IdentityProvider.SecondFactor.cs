using System.Diagnostics;
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
        var result = await CompleteSecondFactorCoreAsync(challenge, input, ct);
        // Decision 19: a refused passkey answer logs Auth.PasskeyFailed once,
        // here, whatever refused it.
        if (result.IsFailure && input is SecondFactorInput.Passkey) securityEvents.PasskeyFailed();
        return result;
    }

    private async Task<Result<TokenPair>> CompleteSecondFactorCoreAsync(
        string challenge, SecondFactorInput input, CancellationToken ct)
    {
        // Parsed before anything digests it: a malformed code is null, and is
        // refused uncharged, after the challenge.
        (SecondFactorCode? code, SecondFactorInput.Passkey? passkey) = input switch
        {
            SecondFactorInput.Passkey answer => (null, answer),
            SecondFactorInput.Code raw => (SecondFactorCode.TryParse(raw.Raw, out var parsed) ? parsed : null, (SecondFactorInput.Passkey?)null),
            _ => throw new UnreachableException(),
        };

        // The challenge first, so a malformed code on a dead challenge gets the 401.
        if (ReadChallenge(challenge) is not { } claims) return Result.Failure<TokenPair>(LogExpiredChallenge());
        if (passkey is null && code is null) return Result.Failure<TokenPair>(SecondFactorErrors.Incorrect);

        // #579: the read refresh uses. Suspension also bumps the epoch, so one
        // that lands after this read leaves the minted token inert.
        var account = await AccountStateAsync(claims.AccountId, ct);
        if (account is null) return Result.Failure<TokenPair>(LogExpiredChallenge());
        if (!account.Value.IsActive) return Result.Failure<TokenPair>(SecondFactorErrors.FarmSuspended);

        var result = await SignInOwnedAsync(async token =>
        {
            // Record 1282 decision 3: the ceremony is consumed by the owned
            // transaction's first statement, so every refusal after it spends it.
            FactorProof proof = passkey is null
                ? new FactorProof.Code(code!)
                : new FactorProof.Passkey(
                    await passkeys.ConsumeAsync(passkey.Ceremony, CeremonyPurpose.SecondStep, claims.UserId, token),
                    passkey.CredentialJson, ExpectedCredentialId: null, passkey.RequestHost);
            var outcome = await twoFactor.VerifyAsync(claims.AccountId, claims.UserId, proof, claims.Binding, token);
            return outcome is FactorOutcome.Accepted accepted
                ? Result.Success(new AcceptedSignIn<NoPayload>(claims.AccountId, claims.UserId, accepted, default))
                : Result.Failure<AcceptedSignIn<NoPayload>>(Refusal(outcome, proof));
        }, ct);
        return result.IsSuccess ? Result.Success(result.Value.Tokens) : Result.Failure<TokenPair>(result.Error);
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

    // Whether the farm is active, and its code, which a passkey sign-in hands
    // back. Null when the farm is gone.
    private async Task<(bool IsActive, string Slug)?> AccountStateAsync(Guid accountId, CancellationToken ct) =>
        await db.Accounts.IgnoreQueryFilters()
            .Where(account => account.Id == accountId)
            .Select(account => new { account.IsActive, account.Slug })
            .FirstOrDefaultAsync(ct) is { } row
            ? (row.IsActive, row.Slug)
            : null;

    // A proof the owned transaction accepted, for the user it names, and what
    // the finish hands back beside the tokens.
    private sealed record AcceptedSignIn<T>(Guid AccountId, Guid UserId, FactorOutcome.Accepted Proof, T Payload)
    {
        public override string ToString() => "[redacted]";
    }

    // A committed sign-in: the tokens, the user and the finish's payload.
    private sealed record SignedIn<T>(TokenPair Tokens, Guid UserId, T Payload)
    {
        public override string ToString() => "[redacted]";
    }

    // The code step hands back nothing beside the tokens.
    private readonly record struct NoPayload;

    // The owned transaction a sign-in finish runs in. Outside IdempotentUnit,
    // so a refused outcome's charge and a consumed ceremony commit, and never in
    // a caller's transaction, which could roll them back (C1). It runs once: a
    // replay after a lost commit would find the challenge or ceremony spent
    // (G3.14).
    private async Task<Result<SignedIn<T>>> SignInOwnedAsync<T>(
        Func<CancellationToken, Task<Result<AcceptedSignIn<T>>>> prove, CancellationToken ct)
    {
        var (rawToken, tokenHash) = GenerateRefreshToken();
        SignedIn<T>? delivered = null;
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
                    return Result.Failure<SignedIn<T>>(proof.Error);
                }

                // Decision 19: a superseded or refused attempt leaves the tenant
                // and the actor unresolved, so it can write no audit row.
                var (accountId, userId, accepted, payload) = proof.Value;
                var user = await db.Users.SingleAsync(u => u.Id == userId && u.AccountId == accountId, token);
                var roles = await userManager.GetRolesAsync(user);
                tenant.Resolve(user.AccountId);
                currentUser.Resolve(user.Id, user.Email ?? string.Empty, [.. roles]);
                await SecondFactorProof.WriteRecoveryCodeUsedAsync(audit, user.Id, accepted, token);
                if (accepted.Passkey is { } used)
                    await passkeys.RecordUseAsync(used, accountId, userId, token);

                db.RefreshTokens.Add(NewToken(user, tokenHash, signedInAt: timeProvider.GetUtcNow()));
                await db.SaveChangesAsync(token);
                delivered = new SignedIn<T>(jwtTokenService.CreateTokenPair(user, [.. roles], rawToken), userId, payload);
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

    // The code step's answer for each refusal; a refused passkey is
    // Auth.PasskeyRefused, never a code's answer.
    private Error Refusal(FactorOutcome outcome, FactorProof proof) => (outcome, proof) switch
    {
        (FactorOutcome.Superseded, _) => LogExpiredChallenge(),
        (FactorOutcome.Rejected, FactorProof.Passkey) => SecondFactorErrors.PasskeyRefused,
        (FactorOutcome.Rejected, FactorProof.Code) => SecondFactorErrors.Incorrect,
        (FactorOutcome.Unavailable, _) => SecondFactorErrors.KeyUnavailable,
        (FactorOutcome.Throttled throttled, _) => new RetryLaterError(throttled.RetryAfter),
        _ => throw new UnreachableException($"{outcome.GetType().Name} is not a refusal."),
    };

    private Error LogExpiredChallenge()
    {
        securityEvents.SecondFactorChallengeExpired();
        return SecondFactorErrors.ChallengeExpired;
    }
}
