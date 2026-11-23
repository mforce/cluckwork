using Cluckwork.Application.Modules.Access.Contracts;

namespace Cluckwork.Application.Modules.Access.Users.RemoveUserPasskeys;

// #1284 (record 1282 decision 10): an Owner removes another user's passkeys as
// its own step-up action, separate from the two-factor reset. The grant is
// spent before the account lock, as ResetUserTwoFactorHandler does.
public sealed class RemoveUserPasskeysHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result> HandleAsync(
        RemoveUserPasskeysCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
        if (!proof.IsSuccess) return proof;

        return await identity.RemoveUserPasskeysAsync(accountId, command.UserId, actingUserId, ct);
    }
}
