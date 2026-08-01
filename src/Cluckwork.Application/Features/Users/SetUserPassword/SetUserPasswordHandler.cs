namespace Cluckwork.Application.Features.Users.SetUserPassword;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

// #165 base behaviour; #308 — resetting an OWNER's password hands the
// resetter that Owner's account, so it additionally requires a valid, recent
// step-up grant (see StepUpGrantService for the full threat model). Resetting
// a Worker/Manager/Sales/ReadOnly password is ordinary farm administration
// and stays ungated — issue #308 explicitly calls out avoiding a blanket
// prompt on it.
public sealed class SetUserPasswordHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result> HandleAsync(
        SetUserPasswordCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var target = await identity.GetUserAsync(accountId, command.UserId, ct);
        if (target is null) return Result.Failure(Cluckwork.Domain.Common.Error.NotFound("Users", command.UserId));

        if (target.Role == Cluckwork.Domain.Accounts.Roles.Owner)
        {
            var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
            if (!proof.IsSuccess) return proof;
        }

        return await identity.SetUserPasswordAsync(accountId, command.UserId, command.NewPassword, ct);
    }
}
