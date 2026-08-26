namespace Cluckwork.Application.Features.Users.SetUserPassword;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

// #165 base behaviour; #308/#360 — every administrative reset replaces an
// authenticator and can turn a short-lived stolen Owner token into an
// independently renewable login, regardless of the target's role. Validate a
// fresh step-up grant before even looking up the target so a proof-less caller
// cannot use the response to distinguish user ids.
public sealed class SetUserPasswordHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result> HandleAsync(
        SetUserPasswordCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
        if (!proof.IsSuccess) return proof;

        var target = await identity.GetUserAsync(accountId, command.UserId, ct);
        if (target is null)
            return Result.Failure(Cluckwork.Domain.Common.Error.NotFound("Users", command.UserId));

        return await identity.SetUserPasswordAsync(accountId, command.UserId, command.NewPassword, ct);
    }
}
