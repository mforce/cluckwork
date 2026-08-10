namespace Cluckwork.Application.Features.Users.EnableUser;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

// #356 — re-enable a disabled user. There is deliberately no validator: the
// command carries no free-text field, only the route's id and the step-up
// header. See IIdentityProvider.EnableUserAsync for why this path must NOT
// touch CredentialEpoch.
public sealed class EnableUserHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result> HandleAsync(
        EnableUserCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
        if (!proof.IsSuccess) return proof;

        return await identity.EnableUserAsync(accountId, command.UserId, actingUserId, ct);
    }
}
