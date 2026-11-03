using Cluckwork.Application.Modules.Access.Contracts;

namespace Cluckwork.Application.Modules.Access.Users.ResetUserTwoFactor;

// #1274 decision 17: an Owner resets another user's two-factor as its own
// step-up action. The grant is spent before the account lock, as for disable
// (see DisableUserHandler's note on the accepted consequence).
public sealed class ResetUserTwoFactorHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result> HandleAsync(
        ResetUserTwoFactorCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
        if (!proof.IsSuccess) return proof;

        return await identity.ResetUserTwoFactorAsync(accountId, command.UserId, actingUserId, ct);
    }
}
