using Cluckwork.Application.Modules.Access.Contracts;

namespace Cluckwork.Application.Modules.Access.Users.TurnOffTwoFactor;

// #1274 decision 8: the user turns their own two-factor off. An enrolled
// user's step-up proved the password and a code, so the grant is the proof.
public sealed class TurnOffTwoFactorHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result<TokenPair?>> HandleAsync(
        Guid accountId, Guid userId, string? stepUpToken, CancellationToken ct)
    {
        var proof = await stepUp.ValidateAsync(accountId, userId, stepUpToken, ct);
        if (!proof.IsSuccess) return Result.Failure<TokenPair?>(proof.Error);

        return await identity.TurnOffTwoFactorAsync(accountId, userId, ct);
    }
}
