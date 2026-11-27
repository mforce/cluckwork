using System.Text.Json;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1285 (record 1282 "Checking a passkey", decisions 7, 11 and 12): a passkey
// alone signs in, and the passkey names the farm. Every refusal answers the
// one Auth.PasskeyRefused body, apart from a suspended farm.
public sealed partial class IdentityProvider
{
    // Identity refuses a longer credential id (record 1282 "Checking a passkey").
    private const int MaxCredentialIdBytes = 1023;

    public Task<PasskeyCeremonyStart> StartPasskeySignInAsync(string requestHost, CancellationToken ct = default) =>
        passkeys.StartSignInAsync(requestHost, ct);

    public async Task<Result<PasskeySignIn>> PasskeyLoginAsync(
        Guid ceremony, string credentialJson, Guid? farmAccountId, string requestHost, CancellationToken ct = default)
    {
        (Guid UserId, string FarmCode)? signedIn = null;
        var result = await SignInOwnedAsync(async token =>
        {
            // Step 0, before any other check, so a ceremony is good for one attempt.
            var consumed = await passkeys.ConsumeAsync(ceremony, CeremonyPurpose.SignIn, userId: null, token);
            // Steps 1 and 2: no signature is checked for an unknown credential
            // or another farm's passkey.
            if (consumed is null || CredentialIdOf(credentialJson) is not { } credentialId
                || await passkeys.FindOwnerAsync(credentialId, token) is not { } owner
                || (farmAccountId is { } farm && farm != owner.AccountId))
                return RefusePasskeySignIn();

            // Step 3, the read the code step makes. It also reads the farm's
            // code, which the SPA remembers after a sign-in with a blank one.
            if (await AccountStateAsync(owner.AccountId, token) is not (true, var slug))
                return RefusePasskeySignIn(SecondFactorErrors.FarmSuspended);

            // Step 4: the binding read without a lock, as the password step
            // reads it, then the code step's checks under the lock.
            var row = await db.Users.AsNoTracking()
                .Where(u => u.Id == owner.UserId && u.AccountId == owner.AccountId)
                .Select(u => new { u.CredentialEpoch, u.SecurityStamp, u.SecondFactorSignIns })
                .SingleOrDefaultAsync(token);
            if (row is null) return RefusePasskeySignIn();
            var outcome = await twoFactor.VerifyAsync(owner.AccountId, owner.UserId,
                new SecondFactorCode.Passkey(ceremony, credentialJson),
                new ProofBinding(row.CredentialEpoch, ProofBinding.HashStamp(row.SecurityStamp), row.SecondFactorSignIns),
                new PasskeyAssertion(consumed, owner.CredentialId, requestHost), token);
            if (outcome is FactorOutcome.Accepted accepted)
            {
                signedIn = (owner.UserId, slug);
                return Result.Success(new AcceptedSignIn(owner.AccountId, owner.UserId, accepted));
            }
            // VerifyAsync logged the refusal.
            return Result.Failure<AcceptedSignIn>(SecondFactorErrors.PasskeyRefused);
        }, ct);

        if (!result.IsSuccess || signedIn is not { } done) return Result.Failure<PasskeySignIn>(result.Error);
        securityEvents.PasskeySignIn(done.UserId);
        return Result.Success(new PasskeySignIn(result.Value, done.FarmCode));
    }

    // A refusal made before VerifyAsync, which logs its own.
    private Result<AcceptedSignIn> RefusePasskeySignIn(Error? error = null)
    {
        securityEvents.PasskeyFailed();
        return Result.Failure<AcceptedSignIn>(error ?? SecondFactorErrors.PasskeyRefused);
    }

    // Step 1: the JSON's id, read as Identity's reader reads it, names matched
    // without case and the last duplicate winning. It only routes the request:
    // AssertAsync's signer check decides which credential signed.
    internal static byte[]? CredentialIdOf(string credentialJson)
    {
        try
        {
            using var document = JsonDocument.Parse(credentialJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            JsonElement? id = null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                    id = property.Value;
            if (id is not { ValueKind: JsonValueKind.String } value) return null;
            var bytes = WebEncoders.Base64UrlDecode(value.GetString()!);
            return bytes.Length is > 0 and <= MaxCredentialIdBytes ? bytes : null;
        }
        // A lone UTF-16 surrogate in a name or the id throws InvalidOperationException.
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
