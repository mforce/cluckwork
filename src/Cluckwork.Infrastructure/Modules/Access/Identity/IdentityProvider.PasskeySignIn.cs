using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1285 (record 1282 "Checking a passkey", decisions 7, 11 and 12): a passkey
// alone signs in, and the passkey names the farm. Every refusal answers the
// one Auth.PasskeyRefused body, apart from a suspended farm.
public sealed partial class IdentityProvider
{
    public Task<PasskeyCeremonyStart> StartPasskeySignInAsync(string requestHost, CancellationToken ct = default) =>
        passkeys.StartSignInAsync(requestHost, ct);

    public async Task<Result<PasskeySignIn>> PasskeyLoginAsync(
        Guid ceremony, string credentialJson, string? farmCode, string requestHost, CancellationToken ct = default)
    {
        var result = await PasskeyLoginCoreAsync(ceremony, credentialJson, farmCode, requestHost, ct);
        // Decision 19: the one exit that logs a passkey sign-in, accepted or refused.
        if (result.IsFailure)
        {
            securityEvents.PasskeyFailed();
            return Result.Failure<PasskeySignIn>(result.Error);
        }
        securityEvents.PasskeySignIn(result.Value.UserId);
        return Result.Success(new PasskeySignIn(result.Value.Tokens, result.Value.Payload));
    }

    // The payload is the signed-in farm's code, as stored.
    private async Task<Result<SignedIn<string>>> PasskeyLoginCoreAsync(
        Guid ceremony, string credentialJson, string? farmCode, string requestHost, CancellationToken ct)
    {
        // Decision 11: a typed farm code is resolved first, so an unknown one is
        // refused before the ceremony is consumed; the passkey's own farm must
        // match a known one.
        Guid? farmAccountId = null;
        if (!string.IsNullOrWhiteSpace(farmCode))
        {
            if (await ResolveFarmCodeAsync(farmCode, ct) is not { } farm)
                return Result.Failure<SignedIn<string>>(SecondFactorErrors.PasskeyRefused);
            farmAccountId = farm.AccountId;
        }

        return await SignInOwnedAsync(async token =>
        {
            // Step 0, before any other check, so a ceremony is good for one attempt.
            var consumed = await passkeys.ConsumeAsync(ceremony, CeremonyPurpose.SignIn, userId: null, token);
            // Steps 1 and 2: no signature is checked for an unknown credential
            // or another farm's passkey.
            if (consumed is null
                || await passkeys.FindOwnerAsync(credentialJson, token) is not { } owner
                || (farmAccountId is { } farm && farm != owner.AccountId))
                return Result.Failure<AcceptedSignIn<string>>(SecondFactorErrors.PasskeyRefused);

            // Step 3, the read the code step makes. It also reads the farm's
            // code, which the SPA remembers after a sign-in with a blank one.
            if (await AccountStateAsync(owner.AccountId, token) is not { } account)
                return Result.Failure<AcceptedSignIn<string>>(SecondFactorErrors.PasskeyRefused);
            if (!account.IsActive)
                return Result.Failure<AcceptedSignIn<string>>(SecondFactorErrors.FarmSuspended);

            // Step 4: the binding read without a lock, as the password step
            // reads it, then the code step's checks under the lock.
            var row = await db.Users.AsNoTracking()
                .Where(u => u.Id == owner.UserId && u.AccountId == owner.AccountId)
                .Select(u => new { u.CredentialEpoch, u.SecurityStamp, u.SecondFactorSignIns })
                .SingleOrDefaultAsync(token);
            if (row is null) return Result.Failure<AcceptedSignIn<string>>(SecondFactorErrors.PasskeyRefused);
            var outcome = await twoFactor.VerifyAsync(owner.AccountId, owner.UserId,
                new FactorProof.Passkey(consumed, credentialJson, owner.CredentialId, requestHost),
                new ProofBinding(row.CredentialEpoch, ProofBinding.HashStamp(row.SecurityStamp), row.SecondFactorSignIns), token);
            return outcome is FactorOutcome.Accepted accepted
                ? Result.Success(new AcceptedSignIn<string>(owner.AccountId, owner.UserId, accepted, account.Slug))
                : Result.Failure<AcceptedSignIn<string>>(SecondFactorErrors.PasskeyRefused);
        }, ct);
    }
}
